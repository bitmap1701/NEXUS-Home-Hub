using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NEXUSHomeHub.Core
{
    public sealed record CockpitCommandResult(
        string Kind,
        bool Ok,
        string Message,
        DispatchPackage? Dispatch,
        BoostResult? Boost,
        double ElapsedMs,
        VerdictOutcome? Verdict = null,
        LatestDispatchSnapshot? Snapshot = null,
        DispatchPhaseTimings? Phases = null,
        NexusConfigLoadResult? ConfigLoad = null,
        System.Threading.Tasks.Task<ConsultOutcome>? AiTask = null,
        System.Threading.Tasks.Task<QaOutcome>? QaTask = null,
        AskSyncBreakdown? SyncTiming = null,
        System.Threading.Tasks.Task<RefineOutcome>? RefineTask = null,
        GeminiUsageStats? UsageStats = null,
        GeminiCacheStats? CacheStats = null,
        VaultGroundingResult? GroundingResult = null,
        bool CacheHit = false);

    /// <summary>Where a Cockpit dispatch spends its time (Gate 5 diagnosis of the P95 regression).</summary>
    public sealed record DispatchPhaseTimings(double RouteAndInsertMs, double LatestFileMs, double AuditMs);

    /// <summary>
    /// TASK-04 §3.A / TASK-11: UI-independent command handling for the Cockpit command box (also driven by the pipe and
    /// HostService --selftest, so it can be verified without a window).
    ///   /boost [process] | /boost off   -> GameBoost (ADR-06) / restore Background Yield
    ///   /status | /telemetry            -> HostService RAM, foreground window, agents, mode
    ///   /mode micro|sprint|full         -> default NEXUS mode for later dispatches
    ///   /pass [#id] [evidence]          -> verdict PASSED (TASK-05, ADR-12); no id = newest dispatch
    ///   /fail | /needswork [#id] [why]  -> verdict NEEDS_WORK, 3rd strike -> ESCALATED (ADR-13)
    ///   /verdict &lt;id&gt; &lt;PASSED|NEEDS_WORK&gt; [notes]
    ///   /reload                         -> re-read nexus_config.json (TASK-06 ADR-20); /config shows what is applied
    ///   /ask [--auto-qa] [--fresh] &lt;question&gt; -> TASK-07 / TASK-11: SQLite WAL cache hit (&lt; 2 ms) or Gemini answer in background
    ///   /ask! [--auto-qa] &lt;question&gt;    -> TASK-11 ADR-44: bypass cache and refresh entry
    ///   /ground | /g [--auto-qa] [--fresh] &lt;query&gt; -> TASK-11 ADR-45: Top-3 sections (&lt;= 1500 chars) from 03_Local_Data_Vault
    ///   /cache [stats | clear]          -> TASK-11 ADR-44: view or clear gemini_response_cache
    ///   /apikey set &lt;key&gt; | status | clear -> TASK-07 DPAPI key vault (the key is never logged or stored in clear)
    ///   /qa [#id] [evidence]            -> TASK-08: QA agent review of a dispatch (newest by default), verdict applied
    ///   /retry [#id] [--auto-qa] [--force] [hint] -> TASK-09: Primary Agent rewrites its answer from the QA critique
    ///   /followup | /f [#id] [--auto-qa] &lt;text&gt; -> TASK-09: next turn of the same dispatch's conversation (always fresh)
    ///   /usage | /tokens                -> TASK-10: RPM/TPM sliding window, daily/session tokens, HTTP retry stats
    ///   anything else                   -> SquadDispatcher (in-process) + latest_dispatch.json
    /// </summary>
    public sealed class CockpitCommandProcessor
    {
        private static readonly Regex HashId = new(@"^#(\d+)\s*(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant); // ADR-29
        private static readonly Regex VerdictArgs = new(@"^#?(\d+)\s+(\S+)\s*(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private readonly SquadDispatcher _dispatcher;
        private readonly DispatchVerdictService _verdicts;
        private readonly SqliteMemoryStore _store;
        private readonly AgentRegistry _registry;
        private readonly NexusConfigService _config;
        private readonly AgentConsultService _consult;

        /// <summary>TASK-06 ADR-21: everything comes from the shared <see cref="NexusRuntime"/>.</summary>
        /// <param name="consult">Test hook (TASK-07 selftests: isolated key vault); default runtime.Consult.</param>
        public CockpitCommandProcessor(NexusRuntime runtime, AgentConsultService? consult = null)
        {
            _consult = consult ?? runtime.Consult;
            _dispatcher = runtime.Dispatcher;
            _store = runtime.Store;
            _registry = runtime.Registry;
            _verdicts = runtime.Verdicts;
            _config = runtime.Config;
            LatestDispatchPath = runtime.LatestDispatchPath;
            DefaultMode = ModeFromConfig();
        }

        public string QaReportPath => _verdicts.QaReportPath;

        public NexusConfigService Config => _config;

        public NexusMode DefaultMode { get; private set; }

        public string LatestDispatchPath { get; }

        /// <param name="input">Raw text from the command box.</param>
        /// <param name="previousForeground">Window that had focus before the Cockpit took it (shielded by /boost).</param>
        /// <param name="dryRunBoost">Plan /boost without changing any process (selftest).</param>
        public CockpitCommandResult Execute(string input, IntPtr previousForeground, bool dryRunBoost = false)
        {
            var stopwatch = Stopwatch.StartNew();
            string text = (input ?? string.Empty).Trim();

            // Legacy inputs from TASK-02/03 keep working.
            if (text.StartsWith("@agent", StringComparison.OrdinalIgnoreCase)) text = text.Substring("@agent".Length).Trim();
            if (text.Equals("telemetry", StringComparison.OrdinalIgnoreCase)) text = "/status";

            if (text.Length == 0) return Done("empty", false, "Gõ nhiệm vụ để gọi Agent Squad, hoặc /boost, /status, /mode.", stopwatch);

            // TASK-07 Zero-Secret-Leak: a pasted API key outside "/apikey set" is never dispatched, stored or logged.
            if (!IsApiKeySet(text) && DpapiSecretVault.GoogleApiKeyPattern.IsMatch(text))
                return Done("blocked", false, "[APIKEY] Dòng lệnh có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", stopwatch);
            if (text.Equals("hide", StringComparison.OrdinalIgnoreCase)) return Done("hide", true, "Ẩn Cockpit.", stopwatch);

            if (text.StartsWith('/'))
            {
                string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string command = parts[0].ToLowerInvariant();
                string? argument = parts.Length > 1 ? parts[1] : null;

                switch (command)
                {
                    case "/boost":
                        return ExecuteBoost(argument, previousForeground, dryRunBoost, stopwatch);
                    case "/status":
                    case "/telemetry":
                        return Done("status", true, BuildStatus(previousForeground), stopwatch);
                    case "/pass":
                    {
                        var (id, notes) = SplitHashId(argument);
                        return ExecuteVerdict(id, "PASSED", notes, stopwatch);
                    }
                    case "/fail":
                    case "/needswork":
                    {
                        var (id, notes) = SplitHashId(argument);
                        return ExecuteVerdict(id, "NEEDS_WORK", notes, stopwatch);
                    }
                    case "/verdict":
                    {
                        Match m = VerdictArgs.Match(argument ?? string.Empty);
                        if (!m.Success || DispatchVerdictService.NormalizeVerdict(m.Groups[2].Value) == null)
                            return Done("verdict", false, "Dùng: /verdict <id> <PASSED|NEEDS_WORK> [ghi chú]", stopwatch);
                        return ExecuteVerdict(long.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, stopwatch);
                    }
                    case "/apikey":
                        return ExecuteApiKey(argument, stopwatch);
                    case "/ask":
                        return ExecuteAsk(argument, freshByCommand: false, stopwatch);
                    case "/ask!":
                        return ExecuteAsk(argument, freshByCommand: true, stopwatch);
                    case "/ground":
                    case "/g":
                        return ExecuteGround(argument, freshByCommand: false, stopwatch);
                    case "/ground!":
                    case "/g!":
                        return ExecuteGround(argument, freshByCommand: true, stopwatch);
                    case "/cache":
                        return ExecuteCache(argument, stopwatch);
                    case "/qa":
                        return ExecuteQa(argument, stopwatch);
                    case "/retry":
                        return ExecuteRefine(AgentConsultService.RetryKind, argument, stopwatch);
                    case "/followup":
                    case "/f":
                        return ExecuteRefine(AgentConsultService.FollowUpKind, argument, stopwatch);
                    case "/usage":
                    case "/tokens":
                        return ExecuteUsage(stopwatch);
                    case "/reload":
                        return ExecuteReload(stopwatch);
                    case "/config":
                        return Done("config", true, _config.Describe(), stopwatch);
                    case "/mode":
                        if (!string.IsNullOrWhiteSpace(argument) && SquadDispatcher.TryParseMode(argument, out NexusMode mode))
                        {
                            DefaultMode = mode;
                            return Done("mode", true, $"[MODE] Chế độ NEXUS mặc định: {mode}.", stopwatch);
                        }
                        return Done("mode", false, "Dùng: /mode micro | sprint | full", stopwatch);
                    default:
                        return Done("unknown", false, $"Lệnh không hỗ trợ: {command}. Dùng /ask, /ask!, /ground (/g), /cache, /qa, /retry, /f, /usage, /tokens, /boost, /status, /mode, /pass, /fail, /verdict, /reload, /config, /apikey hoặc gõ nhiệm vụ.", stopwatch);
                }
            }

            double routeMs = stopwatch.Elapsed.TotalMilliseconds;
            DispatchOutcome outcome = _dispatcher.Dispatch(text, DefaultMode.ToString(), null);
            routeMs = stopwatch.Elapsed.TotalMilliseconds - routeMs;
            if (outcome.Package == null)
            {
                return Done("dispatch", false, $"[DISPATCH] Lỗi: {outcome.Error}", stopwatch);
            }

            DispatchPackage package = outcome.Package;
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromDispatch(package, text, "CockpitUI");
            double fileStart = stopwatch.Elapsed.TotalMilliseconds;
            string? writeError = LatestDispatchFile.Write(LatestDispatchPath, snapshot);
            double auditStart = stopwatch.Elapsed.TotalMilliseconds;
            _store.WriteAuditLog("cockpit_dispatch", ActionRiskLevel.Green_SafeRead,
                JsonSerializer.Serialize(new { dispatchId = package.DispatchId, squad = package.SquadId }), "DispatchedFromCockpit");
            var phases = new DispatchPhaseTimings(Math.Round(routeMs, 3), Math.Round(auditStart - fileStart, 3),
                Math.Round(stopwatch.Elapsed.TotalMilliseconds - auditStart, 3));

            string message =
                $"[DISPATCH #{package.DispatchId}] {package.SquadId} {package.SquadName} → {package.PrimaryAgent.Name} | " +
                $"QA {package.QaAgent.Name} | {package.NexusMode}" +
                (writeError == null ? string.Empty : $" | latest_dispatch.json LỖI: {writeError}");

            stopwatch.Stop();
            return new CockpitCommandResult("dispatch", writeError == null, message, package, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, snapshot, phases);
        }

        /// <summary>Text safe to echo on the HUD or return through the pipe: "/apikey set" arguments and key-like strings are masked.</summary>
        public static string RedactForDisplay(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (IsApiKeySet(trimmed)) return "/apikey set ••••••";
            return DpapiSecretVault.Redact(trimmed);
        }

        private static bool IsApiKeySet(string text) =>
            Regex.IsMatch(text.TrimStart(), @"^/apikey\s+set(\s|$)", RegexOptions.IgnoreCase);

        /// <summary>TASK-07 §3.1. Audit rows record the action and the key length only.</summary>
        private CockpitCommandResult ExecuteApiKey(string? argument, Stopwatch stopwatch)
        {
            string[] parts = (argument ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string action = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            DpapiSecretVault vault = _consult.Vault;
            string model = _config.Current.GeminiModel;

            switch (action)
            {
                case "set":
                {
                    string raw = parts.Length > 1 ? parts[1] : string.Empty;
                    string? error = vault.Store(raw);
                    _store.WriteAuditLog("cockpit_apikey", ActionRiskLevel.Yellow_ReversibleTune,
                        JsonSerializer.Serialize(new { action = "set", length = raw.Trim().Length }), error == null ? "StoredDpapi" : "Rejected");
                    return Done("apikey", error == null, error == null
                        ? $"[APIKEY] Đã mã hóa DPAPI (CurrentUser) → {DpapiSecretVault.FileName}: {DpapiSecretVault.MaskApiKey(raw.Trim(), ApiKeySource.Dpapi)} | model {model}"
                        : $"[APIKEY] Không lưu: {error}", stopwatch);
                }
                case "status":
                case "":
                {
                    ApiKeyResolution key = vault.Resolve();
                    string state = key.Source switch
                    {
                        ApiKeySource.Dpapi => $"Configured via DPAPI: {DpapiSecretVault.MaskApiKey(key.Key, key.Source)}",
                        ApiKeySource.Environment => $"Configured via ENV ({DpapiSecretVault.EnvironmentVariable}): {DpapiSecretVault.MaskApiKey(key.Key, key.Source)}",
                        _ => "Not Configured"
                    };
                    string extra = (key.Error != null ? $" | Cảnh báo: {key.Error}" : string.Empty) +
                                   (GeminiAgentClient.MockEnabled ? $" | MOCK ({GeminiAgentClient.MockEnvironmentVariable}) đang bật" : string.Empty);
                    return Done("apikey", true, $"[APIKEY] {state} | model {model}{extra}", stopwatch);
                }
                case "clear":
                {
                    bool removed = vault.Clear(out string? error);
                    _store.WriteAuditLog("cockpit_apikey", ActionRiskLevel.Yellow_ReversibleTune,
                        JsonSerializer.Serialize(new { action = "clear", removed }), error == null ? "Cleared" : "ClearFailed");
                    ApiKeyResolution after = vault.Resolve();
                    string message = error != null
                        ? $"[APIKEY] Không xóa được {DpapiSecretVault.FileName}: {error}"
                        : (removed ? $"[APIKEY] Đã xóa {DpapiSecretVault.FileName}." : $"[APIKEY] Không có {DpapiSecretVault.FileName} để xóa.") +
                          (after.Source == ApiKeySource.Environment ? $" Lưu ý: vẫn còn khóa từ biến môi trường {DpapiSecretVault.EnvironmentVariable}." : " Trạng thái: Not Configured.");
                    return Done("apikey", error == null, message, stopwatch);
                }
                default:
                    return Done("apikey", false, "Dùng: /apikey set <KEY> | /apikey status | /apikey clear", stopwatch);
            }
        }

        private static (bool AutoQa, bool Fresh, string Text) ParseAskOrGroundFlags(string? argument, bool defaultAutoQa, bool freshByCommand)
        {
            bool autoQa = defaultAutoQa;
            bool fresh = freshByCommand;
            string rest = (argument ?? string.Empty).Trim();
            while (rest.Length > 0)
            {
                int space = rest.IndexOf(' ');
                string token = space < 0 ? rest : rest.Substring(0, space);
                if (token.Equals("--auto-qa", StringComparison.OrdinalIgnoreCase))
                {
                    autoQa = true;
                }
                else if (token.Equals("--fresh", StringComparison.OrdinalIgnoreCase) || token.Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    fresh = true;
                }
                else
                {
                    break;
                }
                rest = space < 0 ? string.Empty : rest.Substring(space + 1).TrimStart();
            }
            return (autoQa, fresh, rest);
        }

        /// <summary>
        /// TASK-07 §3.4 / TASK-08 ADR-28 / TASK-10 §3.2 / TASK-11 ADR-44:
        /// Checks SQLite WAL response cache first (unless <paramref name="freshByCommand"/> or <c>--fresh</c>);
        /// on Cache Hit, returns in &lt; 2 ms with 0 quota/tokens deducted.
        /// </summary>
        private CockpitCommandResult ExecuteAsk(string? argument, bool freshByCommand, Stopwatch stopwatch)
        {
            var (autoQa, fresh, question) = ParseAskOrGroundFlags(argument, _config.Current.AutoQaAfterAsk, freshByCommand);
            if (question.Length == 0)
                return Done("ask", false, "Dùng: /ask [--auto-qa] [--fresh] <câu hỏi / nhiệm vụ> (hoặc /ask! <câu hỏi>)", stopwatch);

            double t0 = stopwatch.Elapsed.TotalMilliseconds;
            bool leaked = DpapiSecretVault.GoogleApiKeyPattern.IsMatch(question);
            double t1 = stopwatch.Elapsed.TotalMilliseconds;
            if (leaked) return Done("blocked", false, "[ASK] Câu hỏi có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", stopwatch);

            if (!fresh && _consult.TryExecuteCachedConsult(question, DefaultMode.ToString(), null, "ask",
                    string.Empty, Array.Empty<string>(), out DispatchPackage? cachedPkg, out ConsultOutcome? cachedOutcome,
                    out double cachedDispatchMs, out double cachedFileMs) &&
                cachedPkg != null && cachedOutcome != null)
            {
                double t2Hit = stopwatch.Elapsed.TotalMilliseconds;
                System.Threading.Tasks.Task<ConsultOutcome> cachedAiTask = System.Threading.Tasks.Task.FromResult(cachedOutcome);
                System.Threading.Tasks.Task<QaOutcome>? cachedQaTask = autoQa
                    ? System.Threading.Tasks.Task.Run(() => _consult.RunQaReviewAsync(cachedPkg.DispatchId, null))
                    : null;
                double t3Hit = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                var hitTiming = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2Hit - t1, 3), Math.Round(t3Hit - t2Hit, 3),
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), cachedDispatchMs, cachedFileMs);
                string hitMsg =
                    $"[ASK #{cachedPkg.DispatchId} // CACHE HIT] {cachedPkg.SquadId} {cachedPkg.SquadName} → {cachedPkg.PrimaryAgent.Name} | " +
                    $"0 tokens deducted (saved {cachedOutcome.SavedTotalTokens:N0} total tokens) in {hitTiming.TotalSyncMs:F2} ms";
                return new CockpitCommandResult("ask", true, hitMsg, cachedPkg, null, hitTiming.TotalSyncMs, null, cachedOutcome.Snapshot,
                    AiTask: cachedAiTask, QaTask: cachedQaTask, SyncTiming: hitTiming, CacheHit: true);
            }

            if (!_consult.QuotaGuard.TryAcquireRequest(_config.Current, _store, out _))
            {
                double guardDone = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                double totalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                return new CockpitCommandResult("quota", false, GeminiQuotaGuard.BlockMessage, null, null, totalMs,
                    SyncTiming: new AskSyncBreakdown(Math.Round(guardDone - t0, 3), 0, 0, totalMs));
            }

            ConsultStart start = _consult.Start(question, DefaultMode.ToString(), null, guardChecked: true);
            double t2 = stopwatch.Elapsed.TotalMilliseconds;
            if (start.Package == null || start.Snapshot == null)
                return Done("ask", false, $"[ASK] Lỗi: {start.Error}", stopwatch);

            DispatchPackage package = start.Package;
            System.Threading.Tasks.Task<ConsultOutcome> aiTask =
                System.Threading.Tasks.Task.Run(() => _consult.CompleteAsync(package, question, default, quotaSlotAcquired: true, commandType: "ask"));
            System.Threading.Tasks.Task<QaOutcome>? qaTask = autoQa
                ? aiTask.ContinueWith(t => t.Result.AiOk
                        ? _consult.RunQaReviewAsync(package.DispatchId, null)
                        : System.Threading.Tasks.Task.FromResult(_consult.SkipQa(package.DispatchId, AgentConsultService.QaSkippedNoAnswer)),
                    System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.None,
                    System.Threading.Tasks.TaskScheduler.Default).Unwrap()
                : null;
            double t3 = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Stop();
            var timing = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), Math.Round(t3 - t2, 3),
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), start.DispatchMs, start.LatestFileMs, start.DispatchTimings);

            string message =
                $"[ASK #{package.DispatchId}{(fresh ? " // FRESH" : string.Empty)}] {package.SquadId} {package.SquadName} → {package.PrimaryAgent.Name} | " +
                $"THINKING... Calling Gemini ({(GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : _config.Current.GeminiModel)})" +
                (autoQa ? $" → QA {package.QaAgent.Name}" : string.Empty) +
                (start.FileError == null ? string.Empty : $" | latest_dispatch.json LỖI: {start.FileError}");
            return new CockpitCommandResult("ask", true, message, package, null, timing.TotalSyncMs, null, start.Snapshot,
                AiTask: aiTask, QaTask: qaTask, SyncTiming: timing, CacheHit: false);
        }

        /// <summary>
        /// TASK-11 ADR-45: /ground &lt;query&gt; (alias /g &lt;query&gt;) extracts Top-3 sections (&lt;= 1500 chars) from 03_Local_Data_Vault/,
        /// checks GeminiResponseCache with the grounding fingerprint (&lt; 2 ms on cache hit), and records grounding_sources in latest_dispatch.json.
        /// </summary>
        private CockpitCommandResult ExecuteGround(string? argument, bool freshByCommand, Stopwatch stopwatch)
        {
            var (autoQa, fresh, query) = ParseAskOrGroundFlags(argument, _config.Current.AutoQaAfterAsk, freshByCommand);
            if (query.Length == 0)
                return Done("ground", false, "Dùng: /ground [--auto-qa] [--fresh] <truy vấn>  (bí danh /g <truy vấn>)", stopwatch);

            double t0 = stopwatch.Elapsed.TotalMilliseconds;
            bool leaked = DpapiSecretVault.GoogleApiKeyPattern.IsMatch(query);
            if (leaked) return Done("blocked", false, "[GROUND] Truy vấn có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", stopwatch);

            VaultGroundingResult grounding = _consult.Grounding.Search(query, VaultGroundingEngine.MaxTopSections, VaultGroundingEngine.MaxTotalAttachedChars);
            double t1 = stopwatch.Elapsed.TotalMilliseconds;

            if (!fresh && _consult.TryExecuteCachedConsult(query, DefaultMode.ToString(), null, "ground",
                    grounding.GroundingFingerprint, grounding.GroundingSources,
                    out DispatchPackage? cachedPkg, out ConsultOutcome? cachedOutcome,
                    out double cachedDispatchMs, out double cachedFileMs) &&
                cachedPkg != null && cachedOutcome != null)
            {
                double t2Hit = stopwatch.Elapsed.TotalMilliseconds;
                System.Threading.Tasks.Task<ConsultOutcome> cachedAiTask = System.Threading.Tasks.Task.FromResult(cachedOutcome);
                System.Threading.Tasks.Task<QaOutcome>? cachedQaTask = autoQa
                    ? System.Threading.Tasks.Task.Run(() => _consult.RunQaReviewAsync(cachedPkg.DispatchId, null))
                    : null;
                double t3Hit = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                var hitTiming = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2Hit - t1, 3), Math.Round(t3Hit - t2Hit, 3),
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), cachedDispatchMs, cachedFileMs);
                string sourcesLabel = grounding.GroundingSources.Count > 0 ? string.Join(", ", grounding.GroundingSources) : "(none)";
                string hitMsg =
                    $"[GROUND #{cachedPkg.DispatchId} // CACHE HIT] {cachedPkg.SquadId} → {cachedPkg.PrimaryAgent.Name} | " +
                    $"Sources ({grounding.GroundingSources.Count}, {grounding.TotalChars} chars): {sourcesLabel} | " +
                    $"0 tokens deducted (saved {cachedOutcome.SavedTotalTokens:N0} total tokens) in {hitTiming.TotalSyncMs:F2} ms";
                return new CockpitCommandResult("ground", true, hitMsg, cachedPkg, null, hitTiming.TotalSyncMs, null, cachedOutcome.Snapshot,
                    AiTask: cachedAiTask, QaTask: cachedQaTask, SyncTiming: hitTiming, GroundingResult: grounding, CacheHit: true);
            }

            if (!_consult.QuotaGuard.TryAcquireRequest(_config.Current, _store, out _))
            {
                double guardDone = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                double totalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                return new CockpitCommandResult("quota", false, GeminiQuotaGuard.BlockMessage, null, null, totalMs,
                    SyncTiming: new AskSyncBreakdown(Math.Round(guardDone - t0, 3), 0, 0, totalMs), GroundingResult: grounding);
            }

            ConsultStart start = _consult.Start(query, DefaultMode.ToString(), null, guardChecked: true, groundingSources: grounding.GroundingSources);
            double t2 = stopwatch.Elapsed.TotalMilliseconds;
            if (start.Package == null || start.Snapshot == null)
                return Done("ground", false, $"[GROUND] Lỗi: {start.Error}", stopwatch);

            DispatchPackage package = start.Package;
            string groundedPrompt = VaultGroundingEngine.BuildGroundedPrompt(query, grounding);
            System.Threading.Tasks.Task<ConsultOutcome> aiTask =
                System.Threading.Tasks.Task.Run(() => _consult.CompleteAsync(
                    package,
                    query,
                    default,
                    quotaSlotAcquired: true,
                    commandType: "ground",
                    promptForAi: groundedPrompt,
                    groundingFingerprint: grounding.GroundingFingerprint,
                    groundingSources: grounding.GroundingSources));
            System.Threading.Tasks.Task<QaOutcome>? qaTask = autoQa
                ? aiTask.ContinueWith(t => t.Result.AiOk
                        ? _consult.RunQaReviewAsync(package.DispatchId, null)
                        : System.Threading.Tasks.Task.FromResult(_consult.SkipQa(package.DispatchId, AgentConsultService.QaSkippedNoAnswer)),
                    System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.None,
                    System.Threading.Tasks.TaskScheduler.Default).Unwrap()
                : null;
            double t3 = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Stop();
            var timing = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), Math.Round(t3 - t2, 3),
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), start.DispatchMs, start.LatestFileMs, start.DispatchTimings);

            string sourcesList = grounding.GroundingSources.Count > 0 ? string.Join(", ", grounding.GroundingSources) : "(không khớp phân đoạn nào)";
            string message =
                $"[GROUND #{package.DispatchId}] {package.SquadId} → {package.PrimaryAgent.Name} | " +
                $"Top-{grounding.GroundingSources.Count} ({grounding.TotalChars} chars): {sourcesList} | " +
                $"THINKING... Calling Gemini ({(GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : _config.Current.GeminiModel)})";
            return new CockpitCommandResult("ground", true, message, package, null, timing.TotalSyncMs, null, start.Snapshot,
                AiTask: aiTask, QaTask: qaTask, SyncTiming: timing, GroundingResult: grounding, CacheHit: false);
        }

        /// <summary>TASK-11 ADR-44: /cache [stats | clear]</summary>
        private CockpitCommandResult ExecuteCache(string? argument, Stopwatch stopwatch)
        {
            string sub = (argument ?? string.Empty).Trim().ToLowerInvariant();
            if (sub == "clear")
            {
                int removed = _consult.ResponseCache.Clear();
                _store.WriteAuditLog("cockpit_cache_clear", ActionRiskLevel.Yellow_ReversibleTune,
                    JsonSerializer.Serialize(new { removed }), "ClearedCache");
                GeminiCacheStats after = _consult.ResponseCache.GetStats();
                stopwatch.Stop();
                return new CockpitCommandResult("cache", true,
                    $"[CACHE] Đã xóa {removed} mục khỏi gemini_response_cache. Entries hiện tại: {after.TotalEntries}.",
                    null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), CacheStats: after);
            }

            if (sub.Length == 0 || sub == "stats")
            {
                GeminiCacheStats s = _consult.ResponseCache.GetStats();
                string msg =
                    $"[CACHE] Entries: {s.ActiveEntries} active / {s.TotalEntries} total (TTL {s.DefaultTtlSeconds}s) | " +
                    $"Hits: {s.TotalHits:N0} | Saved Tokens: {s.SavedTotalTokens:N0} " +
                    $"(prompt {s.PromptTokensSaved:N0}, completion {s.CompletionTokensSaved:N0})";
                stopwatch.Stop();
                return new CockpitCommandResult("cache", true, msg, null, null,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), CacheStats: s);
            }

            return Done("cache", false, "Dùng: /cache | /cache stats | /cache clear", stopwatch);
        }

        /// <summary>TASK-08 /qa [#id] [evidence]: resolve + cache check (&lt; 2 ms on hit) or Gemini QA review in the background.</summary>
        private CockpitCommandResult ExecuteQa(string? argument, Stopwatch stopwatch)
        {
            var (id, evidence) = SplitHashId(argument);
            double t0 = stopwatch.Elapsed.TotalMilliseconds;
            bool leaked = DpapiSecretVault.GoogleApiKeyPattern.IsMatch(evidence);
            double t1 = stopwatch.Elapsed.TotalMilliseconds;
            if (leaked) return Done("blocked", false, "[QA] Bằng chứng có chuỗi giống API key: không gửi, không lưu.", stopwatch);

            QaStart start = _consult.StartQa(id, evidence, writeFile: false);
            double t2 = stopwatch.Elapsed.TotalMilliseconds;
            if (start.Row == null || start.Snapshot == null) return Done("qa", false, $"[QA] Lỗi: {start.Error}", stopwatch);

            if (_consult.TryExecuteCachedQa(start.Row, evidence, out QaOutcome? cachedQa, out QaCacheHitTimings? qaCacheTimings) && cachedQa != null)
            {
                double t3Hit = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                var hitTiming = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), Math.Round(t3Hit - t2, 3),
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), QaCache: qaCacheTimings);
                return new CockpitCommandResult("qa", true,
                    $"[QA #{start.DispatchId} // CACHE HIT] {cachedQa.QaAgent} → {cachedQa.Verdict} (0 tokens deducted, saved {cachedQa.SavedTotalTokens:N0} total tokens)",
                    null, null, hitTiming.TotalSyncMs, null, cachedQa.Snapshot,
                    QaTask: System.Threading.Tasks.Task.FromResult(cachedQa), SyncTiming: hitTiming, CacheHit: true);
            }

            if (!_consult.QuotaGuard.TryAcquireRequest(_config.Current, _store, out _))
            {
                stopwatch.Stop();
                double totalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                return new CockpitCommandResult("quota", false, GeminiQuotaGuard.BlockMessage, null, null, totalMs, null, start.Snapshot,
                    SyncTiming: new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), 0, totalMs));
            }

            long dispatchId = start.DispatchId;
            System.Threading.Tasks.Task<QaOutcome> qaTask =
                System.Threading.Tasks.Task.Run(() => _consult.RunQaReviewAsync(dispatchId, evidence, default, quotaSlotAcquired: true));
            double t3 = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Stop();
            var timing = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), Math.Round(t3 - t2, 3),
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));

            return new CockpitCommandResult("qa", true,
                $"[QA #{dispatchId}] {start.Snapshot.QaAgent.Name} đang kiểm định câu trả lời của {start.Snapshot.PrimaryAgent.Name}...",
                null, null, timing.TotalSyncMs, null, start.Snapshot, QaTask: qaTask, SyncTiming: timing);
        }

        /// <summary>
        /// TASK-09 /retry and /followup. Synchronous part (timed as <see cref="AskSyncBreakdown"/>): key guard, resolve the
        /// dispatch + circuit breaker (retry), quota guard, spawn the background task.
        /// </summary>
        private CockpitCommandResult ExecuteRefine(string kind, string? argument, Stopwatch stopwatch)
        {
            bool retry = kind == AgentConsultService.RetryKind;
            var (id, autoQaFlag, force, text) = ParseRefineArgs(argument, allowForce: retry);
            bool autoQa = autoQaFlag || _config.Current.AutoQaAfterAsk;
            string tag = retry ? "RETRY" : "FOLLOW-UP";
            if (!retry && text.Length == 0)
                return Done(kind, false, "Dùng: /followup [#id] [--auto-qa] <câu hỏi tiếp theo>  (bí danh /f)", stopwatch);

            double t0 = stopwatch.Elapsed.TotalMilliseconds;
            bool leaked = DpapiSecretVault.GoogleApiKeyPattern.IsMatch(text);
            double t1 = stopwatch.Elapsed.TotalMilliseconds;
            if (leaked) return Done("blocked", false, $"[{tag}] Nội dung có chuỗi giống API key: không gửi, không lưu.", stopwatch);

            RefineStart start = retry ? _consult.StartRetry(id, text, force, writeFile: false) : _consult.StartFollowUp(id, text, writeFile: false);
            double t2 = stopwatch.Elapsed.TotalMilliseconds;
            if (start.Row == null || start.Snapshot == null) return Done(kind, false, $"[{tag}] Lỗi: {start.Error}", stopwatch);
            if (start.Blocked)
            {
                stopwatch.Stop();
                return new CockpitCommandResult(kind, false, start.Error ?? AgentConsultService.CircuitBreakerMessage(start.DispatchId), null, null,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, start.Snapshot,
                    SyncTiming: new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), 0, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)));
            }

            if (!_consult.QuotaGuard.TryAcquireRequest(_config.Current, _store, out _))
            {
                stopwatch.Stop();
                double totalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                return new CockpitCommandResult("quota", false, GeminiQuotaGuard.BlockMessage, null, null, totalMs, null, start.Snapshot,
                    SyncTiming: new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), 0, totalMs));
            }

            long dispatchId = start.DispatchId;
            System.Threading.Tasks.Task<RefineOutcome> refineTask = retry
                ? System.Threading.Tasks.Task.Run(() => _consult.CompleteRetryAsync(dispatchId, text, force, default, quotaSlotAcquired: true))
                : System.Threading.Tasks.Task.Run(() => _consult.CompleteFollowUpAsync(dispatchId, text, default, quotaSlotAcquired: true));
            System.Threading.Tasks.Task<QaOutcome>? qaTask = autoQa
                ? refineTask.ContinueWith(t => t.Result.AiOk
                        ? _consult.RunQaReviewAsync(dispatchId, null)
                        : System.Threading.Tasks.Task.FromResult(_consult.NotReviewed(dispatchId,
                            $"[LOCAL QA SKIPPED] {(t.Result.Blocked ? "bị chặn bởi cầu chì 3 lần thử" : "không có câu trả lời mới")}: QA không chạy.")),
                    System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.None,
                    System.Threading.Tasks.TaskScheduler.Default).Unwrap()
                : null;
            double t3 = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Stop();
            var timing = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2 - t1, 3), Math.Round(t3 - t2, 3),
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));

            LatestDispatchSnapshot s = start.Snapshot;
            string message = retry
                ? $"[RETRY #{dispatchId}] {s.PrimaryAgent.Name} đang tự sửa theo nhận xét QA (lượt {s.TurnCount + 1}, strike {s.RetryCount}/{DispatchVerdictService.StrikeLimit})" +
                  (force && AgentConsultService.IsEscalated(start.Row) ? " | --force: vượt cầu chì 3 lần thử" : string.Empty)
                : $"[FOLLOW-UP #{dispatchId}] lượt {s.TurnCount + 1} → {s.PrimaryAgent.Name} (nhớ tối đa {_config.Current.MaxConversationTurns} cặp lượt)";
            message += (autoQa ? $" → QA {s.QaAgent.Name}" : string.Empty) +
                       $" | {(GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : _config.Current.GeminiModel)}";
            return new CockpitCommandResult(kind, true, message, null, null, timing.TotalSyncMs, null, s,
                QaTask: qaTask, SyncTiming: timing, RefineTask: refineTask);
        }

        /// <summary>TASK-10 §3.4: /usage (alias /tokens) displays current RPM, TPM, Daily Tokens, Session Tokens, and HTTP retry stats.</summary>
        private CockpitCommandResult ExecuteUsage(Stopwatch stopwatch)
        {
            GeminiUsageStats s = _consult.QuotaGuard.GetStats(_config.Current, _store);
            string message =
                $"[USAGE] RPM: {s.CurrentRpm}/{s.MaxRequestsPerMinute} | " +
                $"TPM: {s.CurrentTpm:N0}/{s.MaxTokensPerMinute:N0} | " +
                $"Daily Tokens (UTC): {s.DailyTotalTokens:N0}/{s.MaxDailyTokens:N0} (prompt {s.DailyPromptTokens:N0}, completion {s.DailyCompletionTokens:N0}; live API only) | " +
                $"Mock/test tokens today: {s.MockTokensUsed:N0} (not limited) | " +
                $"Session: {s.SessionTotalTokens:N0} tokens ({s.SessionRequests} reqs, {s.SessionHttpRetries} HTTP retries, {s.SessionQuotaBlocks} quota blocks) | " +
                $"Backoff: maxRetries={s.HttpMaxRetries}, baseDelay={s.HttpRetryBaseDelayMs}ms";
            stopwatch.Stop();
            return new CockpitCommandResult("usage", true, message, null, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), UsageStats: s);
        }

        /// <summary>Leading "#id", "--auto-qa" and (retry) "--force" tokens in any order, then the free text.</summary>
        private static (long? Id, bool AutoQa, bool Force, string Text) ParseRefineArgs(string? argument, bool allowForce)
        {
            long? id = null;
            bool autoQa = false, force = false;
            string rest = (argument ?? string.Empty).Trim();
            while (rest.Length > 0)
            {
                int space = rest.IndexOf(' ');
                string token = space < 0 ? rest : rest.Substring(0, space);
                if (token.Equals("--auto-qa", StringComparison.OrdinalIgnoreCase)) autoQa = true;
                else if (allowForce && token.Equals("--force", StringComparison.OrdinalIgnoreCase)) force = true;
                else if (id == null && token.Length > 1 && token[0] == '#' && long.TryParse(token.AsSpan(1), out long parsed) && parsed > 0) id = parsed;
                else break;
                rest = space < 0 ? string.Empty : rest.Substring(space + 1).TrimStart();
            }
            return (id, autoQa, force, rest);
        }

        private NexusMode ModeFromConfig() =>
            SquadDispatcher.TryParseMode(_config.Current.DefaultNexusMode, out NexusMode mode) ? mode : NexusMode.Micro;

        /// <summary>ADR-20: re-read nexus_config.json; a broken file never stops the Cockpit (last valid config stays).</summary>
        private CockpitCommandResult ExecuteReload(Stopwatch stopwatch)
        {
            NexusConfigLoadResult load = _config.Reload();
            DefaultMode = ModeFromConfig();
            _consult.QuotaGuard.SeedFromStore(_store);
            NexusConfig c = _config.Current;
            string applied =
                $"defaultNexusMode={c.DefaultNexusMode}, memoryTrimFloorMb={c.MemoryTrimFloorMb}, watcherDebounceMs={c.WatcherDebounceMs}, " +
                $"maxRPM={c.MaxRequestsPerMinute}, maxTPM={c.MaxTokensPerMinute}, maxDailyTokens={c.MaxDailyTokens}, " +
                $"httpMaxRetries={c.HttpMaxRetries}, httpRetryBaseDelayMs={c.HttpRetryBaseDelayMs}, " +
                $"monitor=[{string.Join(", ", c.PreferredMonitorKeywords)}], protectedProcessPrefixes={c.ProtectedProcessPrefixes.Count}";
            string message = load.Ok
                ? $"[CONFIG] Đã nạp lại nexus_config.json: {applied}" +
                  (load.Warnings.Count > 0 ? $" | Cảnh báo: {string.Join(" ", load.Warnings)}" : string.Empty)
                : $"[CONFIG] LỖI: {load.Error} → giữ cấu hình hợp lệ gần nhất ({applied})" +
                  (load.Restored ? " | đã ghi lại file chuẩn" : string.Empty) +
                  (load.BackupPath != null ? $" | bản hỏng: {Path.GetFileName(load.BackupPath)}" : string.Empty);
            _store.WriteAuditLog("cockpit_config_reload", ActionRiskLevel.Green_SafeRead,
                JsonSerializer.Serialize(new { ok = load.Ok, restored = load.Restored, warnings = load.Warnings.Count, fingerprint = _config.Fingerprint }),
                load.Ok ? "Reloaded" : "KeptLastValid");
            stopwatch.Stop();
            return new CockpitCommandResult("reload", load.Ok, message, null, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), ConfigLoad: load);
        }

        private CockpitCommandResult ExecuteVerdict(long? dispatchId, string verdict, string notes, Stopwatch stopwatch)
        {
            VerdictOutcome outcome = _verdicts.Apply(dispatchId, verdict, notes);
            stopwatch.Stop();
            double ms = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
            if (!outcome.Ok || outcome.Dispatch == null)
            {
                return new CockpitCommandResult("verdict", false, $"[VERDICT] Lỗi: {outcome.Error}", null, null, ms, outcome);
            }

            RecentDispatch row = outcome.Dispatch;
            string label = DispatchVerdictService.StatusLabel(row.Status, row.RetryCount);
            string message = outcome.EscalatedNow
                ? $"[3-STRIKE ESCALATION] Dispatch #{row.Id} → {label}. Đã ghi biên bản vào qa_report.md."
                : $"[VERDICT] Dispatch #{row.Id} → {label}" + (row.EvidenceNotes.Length > 0 ? $" | {row.EvidenceNotes}" : string.Empty);
            if (outcome.FileError != null) message += $" | latest_dispatch.json LỖI: {outcome.FileError}";

            return new CockpitCommandResult("verdict", outcome.FileError == null, message, null, null, ms, outcome,
                LatestDispatchFile.FromRecord(row, _registry, "CockpitUI"));
        }

        /// <summary>"#12 note" -> (12, "note"); "note" -> (null, "note"). A bare number is a note ("200Hz ok"), not an id.</summary>
        private static (long? Id, string Notes) SplitHashId(string? argument)
        {
            if (string.IsNullOrWhiteSpace(argument)) return (null, string.Empty);
            Match m = HashId.Match(argument.Trim());
            return m.Success ? (long.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim()) : (null, argument.Trim());
        }

        private CockpitCommandResult ExecuteBoost(string? argument, IntPtr previousForeground, bool dryRun, Stopwatch stopwatch)
        {
            if (string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase))
            {
                int restored = GameBoost.RestoreYield();
                _store.WriteAuditLog("cockpit_boost_off", ActionRiskLevel.Yellow_ReversibleTune, "{}", $"Restored={restored}");
                return Done("boost", true, $"[BOOST] Đã khôi phục ưu tiên Normal cho {restored} tiến trình.", stopwatch);
            }

            BoostResult result = GameBoost.Run(argument, previousForeground, dryRun);
            _store.WriteAuditLog("cockpit_boost", ActionRiskLevel.Yellow_ReversibleTune,
                JsonSerializer.Serialize(new
                {
                    target = result.TargetProcess,
                    status = result.TargetStatus,
                    trimmed = result.TrimmedCount,
                    freedMb = result.FreedMb,
                    yielded = result.YieldedCount,
                    dryRun
                }),
                result.TargetStatus);

            stopwatch.Stop();
            return new CockpitCommandResult("boost", true, result.Message, null, result,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));
        }

        private string BuildStatus(IntPtr previousForeground)
        {
            using Process self = Process.GetCurrentProcess();
            IntPtr foreground = previousForeground != IntPtr.Zero ? previousForeground : ForegroundWindowInfo.Current();
            string title = ForegroundWindowInfo.TitleOf(foreground);
            return
                $"[STATUS] HostService RAM: WS {self.WorkingSet64 / (1024.0 * 1024.0):F1} MB, Private {self.PrivateMemorySize64 / (1024.0 * 1024.0):F1} MB | " +
                $"CPU time {self.TotalProcessorTime.TotalSeconds:F1}s | Foreground: '{(title.Length > 0 ? title : "(không rõ)")}' | " +
                $"Agents: {_registry.Count} | Dispatches: {_store.CountDispatchLogs()} | Mode: {DefaultMode} | " +
                $"Boost yield đang giữ: {GameBoost.PendingYieldCount}";
        }

        private static CockpitCommandResult Done(string kind, bool ok, string message, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            return new CockpitCommandResult(kind, ok, message, null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));
        }
    }
}
