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
        bool CacheHit = false,
        LocalUtilityResult? Local = null,
        ClipboardCopyResult? Copy = null,
        MfdPinResult? PinResult = null,
        VisionResult? Vision = null,
        string? UiAction = null,
        CtxResult? Ctx = null,
        System.Threading.CancellationTokenSource? Cancellation = null, // TASK-16c: Hủy (Esc) on the running card cancels this request
        System.Threading.Tasks.Task<LocalUtilityResult>? LocalTask = null); // TASK-17: /net status, /net ping, /ptt finish on a pool thread

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
    ///   /calc | /c | /= &lt;expr&gt;          -> TASK-14 ADR-59: local calculator (Dec / Hex / Bin), 0 tokens
    ///   /timer &lt;duration&gt; [label] | list | cancel &lt;id|all&gt;; /timers -> TASK-14: countdown, Passive HUD on expiry
    ///   /note &lt;text&gt; | list [N] | find &lt;keyword&gt; -> TASK-14: zettelkasten/quick_notes.md, read by /ground at once
    ///   /img [--fresh] [--file &lt;path&gt;] [question] -> TASK-12 ADR-61: clipboard / file image (JPEG &lt;= 250 KB) + Gemini inline_data
    ///   /snap [--fresh] [question]      -> TASK-12: MSI G272 capture behind the Combat-Mode Shield and Privacy Window Guard
    ///   /ui [collapsed|half|full|toggle|xuyen on|off|tuthu on|off|keys] -> TASK-16 U1: HUD height / click-through (window applies it)
    ///   /watch [test]                   -> TASK-16 U3: wrist webhook status, or one test POST (fire-and-forget, 3 s timeout)
    ///   /ctx [status] | /ctx [--fresh] [--no-clip] &lt;question&gt; -> TASK-15 ADR-62: active window context behind the
    ///                                    Anti-Cheat Safe Guard + Sensitive Window Denylist (blocked / status = 0 tokens)
    ///   /net [status] | /net port &lt;p&gt; | /net ping &lt;host&gt; -> TASK-17 N1 ADR-67: radar (NIC, pings &lt;= 200 ms in parallel,
    ///                                    NITRO5-WORKER, ports), one port, 3 pings; 0 tokens, status / ping finish in the background
    ///   /mic [status] | list | select &lt;tên|số|auto&gt; -> TASK-17 V1: PreferredMicSelector (FIFINE first, virtual mics ignored)
    ///   /ptt test [giây] | /ptt stop       -> TASK-17 V1: 16 kHz PCM capture of the chosen mic + VAD, buffers freed at the end
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
        private readonly LocalUtilityService _local;
        private readonly HudWidgetService _hudWidgets;
        private readonly MfdPinManager _pins;
        private readonly VisionConsultService _vision;
        private readonly NetworkRadar _network;
        private readonly MicService _mic;
        private readonly string _source;
        private System.Threading.CancellationTokenSource? _pttCts;

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
            _local = runtime.LocalUtilities;
            _hudWidgets = runtime.HudWidgets;
            _pins = runtime.Pins;
            _vision = runtime.Vision;
            _network = runtime.Network;
            _mic = runtime.Mic;
            _source = runtime.Source;
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
            // ("@agent <task>" only: "@agents-orchestrator <task>" is an agent id for the TASK-16c @ palette below)
            if (Regex.IsMatch(text, @"^@agent(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) text = text.Substring("@agent".Length).Trim();
            if (text.Equals("telemetry", StringComparison.OrdinalIgnoreCase)) text = "/status";

            if (text.Length == 0) return Done("empty", false, "Gõ nhiệm vụ để gọi Agent Squad, hoặc /boost, /status, /mode.", stopwatch);

            // TASK-07 Zero-Secret-Leak: a pasted API key outside "/apikey set" is never dispatched, stored or logged.
            if (!IsApiKeySet(text) && DpapiSecretVault.GoogleApiKeyPattern.IsMatch(text))
                return Done("blocked", false, text.StartsWith("/note", StringComparison.OrdinalIgnoreCase)
                    ? "[NOTE BLOCKED] Secret Guard: ghi chú có chuỗi giống API key → không ghi vào quick_notes.md, không lưu SQLite. Dùng /apikey set <KEY>."
                    : text.StartsWith("/pin", StringComparison.OrdinalIgnoreCase)
                        ? "[PIN BLOCKED] Secret Guard: nội dung có chuỗi giống API key → không ghim, không ghi mfd_pins.json. Dùng /apikey set <KEY>."
                        : "[APIKEY] Dòng lệnh có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", stopwatch);
            if (text.Equals("hide", StringComparison.OrdinalIgnoreCase)) return Done("hide", true, "Ẩn Cockpit.", stopwatch);

            if (text.StartsWith('/'))
            {
                string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string command = parts[0].ToLowerInvariant();
                string? argument = parts.Length > 1 ? parts[1] : null;
                if (text.Length > 1 && text[1] == '=')
                {
                    // TASK-14: "/=2+3" works without a space
                    command = "/=";
                    argument = text.Substring(2).Trim();
                }

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
                    case "/copy":
                        return ExecuteCopy(argument, stopwatch);
                    case "/pin":
                        return ExecutePin(argument, stopwatch);
                    case "/pins":
                        return ExecutePin("list", stopwatch);
                    case "/unpin":
                        return ExecuteUnpin(argument, stopwatch);
                    case "/img":
                    case "/image":
                        return ExecuteVision(argument, snap: false, previousForeground, stopwatch);
                    case "/snap":
                        return ExecuteVision(argument, snap: true, previousForeground, stopwatch);
                    case "/mem":
                    case "/memory":
                        return Done("mem", true, MemoryDiagnostics.Describe(MemoryDiagnostics.Capture(_registry, _consult.Grounding)), stopwatch);
                    case "/usage":
                    case "/tokens":
                        return ExecuteUsage(stopwatch);
                    case "/calc":
                    case "/c":
                    case "/=":
                        return ExecuteLocal(LocalUtilityService.Calc, argument, stopwatch);
                    case "/timer":
                        return ExecuteLocal(LocalUtilityService.Timer, argument, stopwatch);
                    case "/timers":
                        return ExecuteLocal(LocalUtilityService.Timer, string.IsNullOrWhiteSpace(argument) ? "list" : argument, stopwatch);
                    case "/note":
                        return ExecuteLocal(LocalUtilityService.Note, argument, stopwatch);
                    case "/reload":
                        return ExecuteReload(stopwatch);
                    case "/config":
                        return Done("config", true, _config.Describe(), stopwatch);
                    case "/ui":
                        return ExecuteUi(argument, stopwatch);
                    case "/watch":
                        return ExecuteWatch(argument, stopwatch);
                    case "/ctx":
                        return ExecuteCtx(argument, previousForeground, stopwatch);
                    case "/net":
                        return ExecuteNet(argument, stopwatch);
                    case "/mic":
                        return ExecuteMic(argument, stopwatch);
                    case "/ptt":
                        return ExecutePtt(argument, stopwatch);
                    case "/mode":
                        if (!string.IsNullOrWhiteSpace(argument) && SquadDispatcher.TryParseMode(argument, out NexusMode mode))
                        {
                            DefaultMode = mode;
                            return Done("mode", true, $"[MODE] Chế độ NEXUS mặc định: {mode}.", stopwatch);
                        }
                        return Done("mode", false, "Dùng: /mode micro | sprint | full", stopwatch);
                    default:
                        return Done("unknown", false, $"Lệnh không hỗ trợ: {command}. Dùng /ask, /ask!, /ground (/g), /cache, /qa, /retry, /f, /usage, /tokens, /calc (/c, /=), /timer, /note, /copy, /pin, /pins, /unpin, /img, /snap, /mem, /boost, /status, /mode, /pass, /fail, /verdict, /reload, /config, /apikey, /ui, /watch, /ctx, /net, /mic, /ptt hoặc gõ nhiệm vụ.", stopwatch);
                }
            }

            // TASK-16c: "@<agentId> <task>" (the @ palette of the command box) dispatches to that agent of the 283-agent registry.
            string? preferredAgent = null;
            if (text.StartsWith('@'))
            {
                string[] at = text.Substring(1).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (at.Length == 2 && _registry.TryGet(at[0], out AgentProfile picked))
                {
                    preferredAgent = picked.Id;
                    text = at[1];
                }
            }

            double routeMs = stopwatch.Elapsed.TotalMilliseconds;
            DispatchOutcome outcome = _dispatcher.Dispatch(text, DefaultMode.ToString(), preferredAgent);
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
                    out double cachedDispatchMs, out double cachedFileMs, out ConsultCacheHitTimings? cacheProfile) &&
                cachedPkg != null && cachedOutcome != null)
            {
                double t2Hit = stopwatch.Elapsed.TotalMilliseconds;
                System.Threading.Tasks.Task<ConsultOutcome> cachedAiTask = System.Threading.Tasks.Task.FromResult(cachedOutcome);
                System.Threading.Tasks.Task<QaOutcome>? cachedQaTask = autoQa
                    ? System.Threading.Tasks.Task.Run(() => _consult.RunQaReviewAsync(cachedPkg.DispatchId, null))
                    : null;
                double t3Hit = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                double hitTotalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                var hitTiming = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2Hit - t1, 3), Math.Round(t3Hit - t2Hit, 3),
                    hitTotalMs, cachedDispatchMs, cachedFileMs,
                    ConsultCache: cacheProfile == null ? null : cacheProfile with { GuardMs = Math.Round(t1 - t0, 3), TotalSyncMs = hitTotalMs });
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
            var cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = cts.Token;
            System.Threading.Tasks.Task<ConsultOutcome> aiTask =
                System.Threading.Tasks.Task.Run(() => _consult.CompleteAsync(package, question, token, quotaSlotAcquired: true, commandType: "ask"));
            System.Threading.Tasks.Task<QaOutcome>? qaTask = autoQa
                ? aiTask.ContinueWith(t => t.Result.AiOk
                        ? _consult.RunQaReviewAsync(package.DispatchId, null, token)
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
                AiTask: aiTask, QaTask: qaTask, SyncTiming: timing, CacheHit: false, Cancellation: cts);
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
            double tGuard = stopwatch.Elapsed.TotalMilliseconds;

            VaultGroundingResult grounding = _consult.Grounding.Search(query, VaultGroundingEngine.MaxTopSections, VaultGroundingEngine.MaxTotalAttachedChars);
            double t1 = stopwatch.Elapsed.TotalMilliseconds;

            if (!fresh && _consult.TryExecuteCachedConsult(query, DefaultMode.ToString(), null, "ground",
                    grounding.GroundingFingerprint, grounding.GroundingSources,
                    out DispatchPackage? cachedPkg, out ConsultOutcome? cachedOutcome,
                    out double cachedDispatchMs, out double cachedFileMs, out ConsultCacheHitTimings? cacheProfile) &&
                cachedPkg != null && cachedOutcome != null)
            {
                double t2Hit = stopwatch.Elapsed.TotalMilliseconds;
                System.Threading.Tasks.Task<ConsultOutcome> cachedAiTask = System.Threading.Tasks.Task.FromResult(cachedOutcome);
                System.Threading.Tasks.Task<QaOutcome>? cachedQaTask = autoQa
                    ? System.Threading.Tasks.Task.Run(() => _consult.RunQaReviewAsync(cachedPkg.DispatchId, null))
                    : null;
                double t3Hit = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Stop();
                double hitTotalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                var hitTiming = new AskSyncBreakdown(Math.Round(t1 - t0, 3), Math.Round(t2Hit - t1, 3), Math.Round(t3Hit - t2Hit, 3),
                    hitTotalMs, cachedDispatchMs, cachedFileMs,
                    ConsultCache: cacheProfile == null ? null : cacheProfile with
                    {
                        GuardMs = Math.Round(tGuard - t0, 3),
                        GroundingSearchMs = Math.Round(t1 - tGuard, 3),
                        TotalSyncMs = hitTotalMs
                    });
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
            var cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = cts.Token;
            System.Threading.Tasks.Task<ConsultOutcome> aiTask =
                System.Threading.Tasks.Task.Run(() => _consult.CompleteAsync(
                    package,
                    query,
                    token,
                    quotaSlotAcquired: true,
                    commandType: "ground",
                    promptForAi: groundedPrompt,
                    groundingFingerprint: grounding.GroundingFingerprint,
                    groundingSources: grounding.GroundingSources));
            System.Threading.Tasks.Task<QaOutcome>? qaTask = autoQa
                ? aiTask.ContinueWith(t => t.Result.AiOk
                        ? _consult.RunQaReviewAsync(package.DispatchId, null, token)
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
                AiTask: aiTask, QaTask: qaTask, SyncTiming: timing, GroundingResult: grounding, CacheHit: false, Cancellation: cts);
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
            var cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = cts.Token;
            System.Threading.Tasks.Task<RefineOutcome> refineTask = retry
                ? System.Threading.Tasks.Task.Run(() => _consult.CompleteRetryAsync(dispatchId, text, force, token, quotaSlotAcquired: true))
                : System.Threading.Tasks.Task.Run(() => _consult.CompleteFollowUpAsync(dispatchId, text, token, quotaSlotAcquired: true));
            System.Threading.Tasks.Task<QaOutcome>? qaTask = autoQa
                ? refineTask.ContinueWith(t => t.Result.AiOk
                        ? _consult.RunQaReviewAsync(dispatchId, null, token)
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
                QaTask: qaTask, SyncTiming: timing, RefineTask: refineTask, Cancellation: cts);
        }

        /// <summary>
        /// TASK-14 ADR-59: /calc, /timer, /note. Synchronous, 0 tokens, no quota slot, no SQLite dispatch row; the result goes
        /// to latest_dispatch.json (ADR-14) so the card and McpTools see it. ElapsedMs = whole Execute path (Gate 14 E2 SLA).
        /// </summary>
        private CockpitCommandResult ExecuteLocal(string utility, string? argument, Stopwatch stopwatch)
        {
            LocalUtilityResult local = _local.Execute(utility, argument);
            stopwatch.Stop();
            return new CockpitCommandResult(utility, local.Ok, local.Message, null, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, local.Snapshot, Local: local);
        }

        /// <summary>
        /// TASK-13: /copy [n] (Alt+C): code block n of the current response (1-based); without n the first code block, or
        /// the whole response when it has none. Synchronous; the Win32 clipboard write is part of ElapsedMs (Gate 13 E2).
        /// </summary>
        private CockpitCommandResult ExecuteCopy(string? argument, Stopwatch stopwatch)
        {
            string arg = (argument ?? string.Empty).Trim().TrimStart('#');
            int? index = null;
            if (arg.Length > 0)
            {
                if (!int.TryParse(arg, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
                    return Done("copy", false, "[COPY] Dùng: /copy [số thứ tự khối code, từ 1] (Alt+C = /copy).", stopwatch);
                index = n;
            }
            (string? text, string label) = _hudWidgets.CopyTarget(index);
            if (text == null) return Done("copy", false, $"[COPY] Không chép được: {label}.", stopwatch);
            ClipboardCopyResult copy = ClipboardService.Copy(text);
            stopwatch.Stop();
            string where = copy.SystemClipboard ? "Clipboard" : $"bộ đệm LastCopiedText ({copy.SystemClipboardError})";
            return new CockpitCommandResult("copy", true, $"[COPY] Đã chép {label}, {text.Length} ký tự → {where}.", null, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, null, Copy: copy);
        }

        /// <summary>
        /// TASK-13: /pin (Alt+P) = first card of the current response; /pin #n = card n; /pin list = /pins;
        /// /pin clear = /unpin all; /pin &lt;text&gt; = a note card. Right MFD keeps at most maxPinnedCards (FIFO).
        /// </summary>
        private CockpitCommandResult ExecutePin(string? argument, Stopwatch stopwatch)
        {
            string arg = (argument ?? string.Empty).Trim();
            MfdPinResult result;
            if (arg.Equals("list", StringComparison.OrdinalIgnoreCase) || arg.Equals("ls", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<MfdPin> pins = _pins.List();
                result = new MfdPinResult(true, false, MfdPinManager.Describe(pins, _pins.MaxPins), pins, null, 0, false);
            }
            else if (arg.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                result = _pins.Unpin("all");
            }
            else if (arg.Length == 0 || (arg[0] == '#' && arg.Length > 1 && arg.Skip(1).All(char.IsDigit)))
            {
                int? index = arg.Length == 0 ? null : int.Parse(arg.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
                (HudWidgetCard? card, string? error) = _hudWidgets.PinTarget(index);
                result = card == null
                    ? new MfdPinResult(false, false, $"[PIN] Không ghim được: {error}.", _pins.List(), null, 0, false)
                    : _pins.Pin(card.Type, card.Title, card.Language, card.Content, _source);
            }
            else
            {
                result = _pins.Pin("note", arg, null, arg, _source);
            }
            stopwatch.Stop();
            return new CockpitCommandResult(result.Blocked ? "blocked" : "pin", result.Ok, result.Message, null, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, null, PinResult: result);
        }

        private CockpitCommandResult ExecuteUnpin(string? argument, Stopwatch stopwatch)
        {
            if (string.IsNullOrWhiteSpace(argument))
                return Done("pin", false, "[PIN] Dùng: /unpin <pinId|all> (xem /pins).", stopwatch);
            MfdPinResult result = _pins.Unpin(argument);
            stopwatch.Stop();
            return new CockpitCommandResult("pin", result.Ok, result.Message, null, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, null, PinResult: result);
        }

        /// <summary>
        /// TASK-12 ADR-61: /img [--fresh] [--file &lt;path&gt;] [question] and /snap [--fresh] [question]. The synchronous part
        /// (shields, source signature, Vision cache hit) is timed in ElapsedMs; compression and Gemini run in AiTask.
        /// </summary>
        private CockpitCommandResult ExecuteVision(string? argument, bool snap, IntPtr previousForeground, Stopwatch stopwatch)
        {
            VisionResult vision;
            if (snap)
            {
                var (fresh, question) = ParseSnapArguments(argument);
                vision = _vision.Execute("snap", question, null, DefaultMode.ToString(), null, fresh, previousForeground);
            }
            else
            {
                var (source, path, question, fresh, error) = VisionConsultService.ParseImgArguments(argument);
                if (error != null) return Done("img", false, $"[VISION] {error}", stopwatch);
                vision = _vision.Execute(source, question, path, DefaultMode.ToString(), null, fresh, previousForeground);
            }
            stopwatch.Stop();
            string kind = vision.Blocked ? "blocked" : vision.Kind;
            return new CockpitCommandResult(kind, vision.Ok, vision.Message, vision.Package, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, vision.Snapshot,
                AiTask: vision.AiTask, CacheHit: vision.CacheHit, Vision: vision);
        }

        private static (bool Fresh, string Question) ParseSnapArguments(string? argument)
        {
            string rest = (argument ?? string.Empty).Trim();
            bool fresh = false;
            while (rest.StartsWith("--fresh", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("-f ", StringComparison.OrdinalIgnoreCase) ||
                   rest.Equals("-f", StringComparison.OrdinalIgnoreCase))
            {
                fresh = true;
                int space = rest.IndexOf(' ');
                rest = space < 0 ? string.Empty : rest.Substring(space + 1).TrimStart();
            }
            return (fresh, rest);
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
                $"monitor=[{string.Join(", ", c.PreferredMonitorKeywords)}], protectedProcessPrefixes={c.ProtectedProcessPrefixes.Count}, " +
                $"networkTargets={(c.NetworkTargets is { Count: > 0 } nt ? $"{nt.Count} (config)" : "4 (mặc định)")}, " + // TASK-17a
                $"stt={LocalSttService.DescribeRoute(c.Stt ?? SttConfig.Default)}"; // TASK-18a
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
                // TASK-13a: the ADR-09 metric (Private Working Set), not PrivateMemorySize64 (commit charge).
                $"[STATUS] HostService RAM: {MemoryDiagnostics.HudLabel(MemoryDiagnostics.PrivateWorkingSetMb())} (WS {self.WorkingSet64 / (1024.0 * 1024.0):F1} MB) | " +
                $"CPU time {self.TotalProcessorTime.TotalSeconds:F1}s | Foreground: '{(title.Length > 0 ? title : "(không rõ)")}' | " +
                $"Agents: {_registry.Count} | Dispatches: {_store.CountDispatchLogs()} | Mode: {DefaultMode} | " +
                $"Boost yield đang giữ: {GameBoost.PendingYieldCount}";
        }

        /// <summary>Clipboard sequence of the text last sent with /ctx: the same copied text is not attached twice ("vừa copy").</summary>
        private static uint _lastCtxClipSequence;

        /// <summary>
        /// TASK-15 (ADR-62) /ctx. <paramref name="window"/> = the window the user came from (captured on wake with
        /// GetForegroundWindow only). Both shields run here, never on wake. Status, blocks and errors are local results
        /// (0 tokens, no dispatch row); a sensitive window leaves no title anywhere (message, audit row, latest_dispatch.json).
        /// A question goes through the /g path: GeminiResponseCache keyed by the question + the CTX_V1 fingerprint of
        /// app / title / clipboard text, so asking again on the same window is a cache hit (0 tokens).
        /// </summary>
        private CockpitCommandResult ExecuteCtx(string? argument, IntPtr window, Stopwatch stopwatch)
        {
            string arg = (argument ?? string.Empty).Trim();
            bool fresh = false, noClip = false;
            while (true)
            {
                if (arg.StartsWith("--fresh", StringComparison.OrdinalIgnoreCase)) { fresh = true; arg = arg.Substring(7).TrimStart(); continue; }
                if (arg.StartsWith("--no-clip", StringComparison.OrdinalIgnoreCase)) { noClip = true; arg = arg.Substring(9).TrimStart(); continue; }
                if (arg.StartsWith("--rescan", StringComparison.OrdinalIgnoreCase)) { ActiveWindowContext.InvalidateScan(); arg = arg.Substring(8).TrimStart(); continue; }
                break;
            }
            bool statusOnly = arg.Length == 0 || arg.Equals("status", StringComparison.OrdinalIgnoreCase);
            uint clipSequence = 0;
            Func<string?>? clip = noClip ? null : () =>
            {
                string? text = ClipboardService.TryReadText(ActiveWindowContext.MaxClipChars, out uint seq);
                clipSequence = seq;
                return seq != 0 && seq == _lastCtxClipSequence ? null : text; // only text copied since the last /ctx question
            };
            CtxResult ctx = ActiveWindowContext.Inspect(window, clip);
            _store.WriteAuditLog("cockpit_ctx", ActionRiskLevel.Green_SafeRead, JsonSerializer.Serialize(new
            {
                status = ctx.Status,
                process = ctx.ProcessName,            // null for a sensitive window
                rule = ctx.MatchedRule,
                shield = ctx.Shield,
                titleChars = ctx.WindowTitle?.Length ?? 0,
                clipChars = ctx.ClipText?.Length ?? 0,
                guardMs = ctx.GuardMs,
                question = !statusOnly
            }), ctx.Status);

            if (ctx.Status != ActiveWindowContext.StatusOk || statusOnly)
            {
                string message = ctx.Status == ActiveWindowContext.StatusOk
                    ? $"[CTX] NEXUS đang thấy: app={ctx.ProcessName} | title={ctx.WindowTitle}" +
                      (ctx.ClipText != null ? $" | clipboard {ctx.ClipText.Length} ký tự sẽ gửi kèm" : " | không kèm clipboard") +
                      $" | khiên {ctx.GuardMs:F3} ms. Gõ /ctx <câu hỏi> để hỏi (0 token cho tới lúc đó)."
                    : ctx.Message;
                string commandText = statusOnly ? "/ctx status" : "/ctx (câu hỏi không gửi vì bị chặn)";
                LatestDispatchSnapshot snap = LatestDispatchFile.FromLocalUtility(LatestDispatchFile.LocalUtilityEvent, "ctx", commandText,
                    message, ctx.Status == ActiveWindowContext.StatusOk ? "PASSED" : "BLOCKED", _source, ctx.TotalMs);
                string? fileError = LatestDispatchFile.Write(LatestDispatchPath, snap);
                stopwatch.Stop();
                return new CockpitCommandResult("ctx", ctx.Status == ActiveWindowContext.StatusOk, message + (fileError != null ? $" (latest_dispatch.json: {fileError})" : string.Empty),
                    null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), Snapshot: snap, Ctx: ctx);
            }

            string question = arg;
            var sources = new[] { $"ctx:{ctx.ProcessName}" };
            double t0 = stopwatch.Elapsed.TotalMilliseconds;
            if (ctx.ClipText != null) _lastCtxClipSequence = clipSequence;
            if (!fresh && _consult.TryExecuteCachedConsult(question, DefaultMode.ToString(), null, "ctx", ctx.Fingerprint, sources,
                    out DispatchPackage? cachedPkg, out ConsultOutcome? cachedOutcome, out double cachedDispatchMs, out double cachedFileMs,
                    out ConsultCacheHitTimings? cacheProfile) && cachedPkg != null && cachedOutcome != null)
            {
                stopwatch.Stop();
                double hitMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                var hitTiming = new AskSyncBreakdown(Math.Round(ctx.TotalMs, 3), Math.Round(stopwatch.Elapsed.TotalMilliseconds - t0, 3), 0, hitMs,
                    cachedDispatchMs, cachedFileMs, ConsultCache: cacheProfile == null ? null : cacheProfile with { GuardMs = ctx.GuardMs, TotalSyncMs = hitMs });
                return new CockpitCommandResult("ctx", true,
                    $"[CTX #{cachedPkg.DispatchId} // CACHE HIT] {ctx.ProcessName} | 0 tokens (saved {cachedOutcome.SavedTotalTokens:N0}) in {hitMs:F2} ms",
                    cachedPkg, null, hitMs, null, cachedOutcome.Snapshot, AiTask: System.Threading.Tasks.Task.FromResult(cachedOutcome),
                    SyncTiming: hitTiming, CacheHit: true, Ctx: ctx);
            }
            if (!_consult.QuotaGuard.TryAcquireRequest(_config.Current, _store, out _))
                return new CockpitCommandResult("quota", false, GeminiQuotaGuard.BlockMessage, null, null,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), Ctx: ctx);

            ConsultStart start = _consult.Start(question, DefaultMode.ToString(), null, guardChecked: true, groundingSources: sources, eventName: "ctx");
            if (start.Package == null || start.Snapshot == null)
                return Done("ctx", false, $"[CTX] Lỗi: {start.Error}", stopwatch);
            DispatchPackage package = start.Package;
            string prompt = ActiveWindowContext.BuildPrompt(question, ctx.ContextBlock!);
            var cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = cts.Token;
            System.Threading.Tasks.Task<ConsultOutcome> aiTask = System.Threading.Tasks.Task.Run(() => _consult.CompleteAsync(
                package, question, token, quotaSlotAcquired: true, commandType: "ctx", promptForAi: prompt,
                groundingFingerprint: ctx.Fingerprint, groundingSources: sources));
            stopwatch.Stop();
            double totalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
            return new CockpitCommandResult("ctx", true,
                $"[CTX #{package.DispatchId}] {ctx.ProcessName} ({ctx.ContextBlock!.Length} ký tự ngữ cảnh) | THINKING... Calling Gemini " +
                $"({(GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : _config.Current.GeminiModel)})",
                package, null, totalMs, null, start.Snapshot, AiTask: aiTask,
                SyncTiming: new AskSyncBreakdown(Math.Round(ctx.TotalMs, 3), 0, 0, totalMs, start.DispatchMs, start.LatestFileMs, start.DispatchTimings),
                CacheHit: false, Ctx: ctx, Cancellation: cts);
        }

        /// <summary>TASK-16 U1: validates /ui; the Cockpit window applies <see cref="CockpitCommandResult.UiAction"/>.</summary>
        private static CockpitCommandResult ExecuteUi(string? argument, Stopwatch stopwatch)
        {
            string? action = CockpitLayout.ParseUiAction(argument);
            if (action == null)
                return Done("ui", false, "Dùng: /ui collapsed | half | full | toggle | xuyen on|off | tuthu on|off | keys", stopwatch);
            stopwatch.Stop();
            return new CockpitCommandResult("ui", true, $"[UI] {action}", null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), UiAction: action);
        }

        /// <summary>TASK-16 U3: /watch shows the webhook state (host only, never the URL); /watch test sends one POST without waiting.</summary>
        private CockpitCommandResult ExecuteWatch(string? argument, Stopwatch stopwatch)
        {
            NexusConfig config = _config.Current;
            string? url = WatchWebhook.ResolveUrl(config);
            string arg = (argument ?? string.Empty).Trim().ToLowerInvariant();
            if (arg is "test" or "thu" or "thử")
            {
                if (url == null)
                    return Done("watch", false, $"[WATCH] Chưa có URL: đặt biến môi trường {WatchWebhook.UrlVariable} hoặc watchWebhookUrl trong nexus_config.json.", stopwatch);
                _ = WatchWebhook.Notify(WatchWebhook.TestEvent, "Thử rung từ NEXUS Hub (/watch test).", force: true, config: config);
                return Done("watch", true, $"[WATCH] Đã gửi thử tới {WatchWebhook.HostOf(url)} (chạy nền, timeout {WatchWebhook.TimeoutMs / 1000} s; kết quả: /watch).", stopwatch);
            }
            if (arg.Length > 0) return Done("watch", false, "Dùng: /watch | /watch test", stopwatch);
            WatchWebhookResult? last = WatchWebhook.LastResult;
            string lastText = last == null ? "chưa gửi lần nào"
                : last.Ok ? $"{last.Event} OK HTTP {last.StatusCode} trong {last.ElapsedMs:F0} ms ({last.AtUtc})"
                : $"{last.Event} LỖI {last.Error ?? ("HTTP " + last.StatusCode)} sau {last.ElapsedMs:F0} ms ({last.AtUtc})";
            return Done("watch", true, url == null
                ? $"[WATCH] Tắt (không có {WatchWebhook.UrlVariable} / watchWebhookUrl). Lần cuối: {lastText}."
                : $"[WATCH] Bật → {WatchWebhook.HostOf(url)} (timer hết giờ, ESCALATED 3 lần). Lần cuối: {lastText}.", stopwatch);
        }

        public const string NetUsage = "Dùng: /net (= /net status: NIC, ping gateway / NITRO5 / TV, cổng) | /net port <số cổng> | /net ping <IP hoặc tên máy>";
        public const string PttUsage = "Dùng: /ptt [giây, 0.5–30, mặc định 2] (nói rồi chữ vào ô lệnh) | /ptt test [giây] | /ptt status | /ptt stop";

        /// <summary>
        /// TASK-17 N1. /net port answers at once (one read of the TCP / UDP tables, &lt; 15 ms); /net status and /net ping run on a
        /// pool thread (every ping cut at 200 ms) and land in the thread as the Radar card / a local line when they finish.
        /// </summary>
        private CockpitCommandResult ExecuteNet(string? argument, Stopwatch stopwatch)
        {
            string[] parts = (argument ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
            string rest = parts.Length > 1 ? parts[1] : string.Empty;
            switch (sub)
            {
                case "status":
                case "radar":
                {
                    NetworkRadar radar = _network;
                    LocalUtilityService local = _local;
                    System.Threading.Tasks.Task<LocalUtilityResult> task = System.Threading.Tasks.Task.Run(async () =>
                    {
                        NetRadarSnapshot snap = await radar.ScanAsync().ConfigureAwait(false);
                        return local.Record(LocalUtilityService.Net, "/net status", "status", true,
                            NetworkRadar.Summary(snap) + "\n" + NetworkRadar.Describe(snap), snap, snap.TotalMs);
                    });
                    stopwatch.Stop();
                    return new CockpitCommandResult("net", true,
                        $"[NET] Đang dò mạng: ping song song gateway / NITRO5 / TV, tối đa {NetworkRadar.PingTimeoutMs} ms mỗi đích…", null, null,
                        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), LocalTask: task);
                }
                case "port":
                {
                    if (!int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) ||
                        port < 1 || port > 65535)
                        return Done("net", false, $"[NET] Cổng phải là số 1–65535. {NetUsage}", stopwatch);
                    NetPortCheck check = NetworkRadar.CheckPort(port);
                    LocalUtilityResult r = _local.Record(LocalUtilityService.Net, $"/net port {port}", "port", true, NetworkRadar.DescribePort(check), check, check.ElapsedMs);
                    stopwatch.Stop();
                    return new CockpitCommandResult("net", true, r.Message, null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                        null, r.Snapshot, Local: r);
                }
                case "ping":
                {
                    string host = rest.Trim();
                    if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace))
                        return Done("net", false, $"[NET] Thiếu địa chỉ. {NetUsage}", stopwatch);
                    LocalUtilityService local = _local;
                    System.Threading.Tasks.Task<LocalUtilityResult> task = System.Threading.Tasks.Task.Run(async () =>
                    {
                        NetPingSeries series = await NetworkRadar.PingSeriesAsync(host).ConfigureAwait(false);
                        return local.Record(LocalUtilityService.Net, $"/net ping {host}", "ping", series.Error == null,
                            NetworkRadar.DescribePing(series), series, series.ElapsedMs);
                    });
                    stopwatch.Stop();
                    return new CockpitCommandResult("net", true, $"[NET] Đang ping {host} ({NetworkRadar.PingSeriesSamples} mẫu, tối đa {NetworkRadar.PingTimeoutMs} ms/mẫu)…",
                        null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), LocalTask: task);
                }
                default:
                    return Done("net", false, $"[NET] {NetUsage}", stopwatch);
            }
        }

        /// <summary>TASK-17 V1: /mic status | list | select (Core Audio endpoint list, a few ms; nothing is opened).</summary>
        private CockpitCommandResult ExecuteMic(string? argument, Stopwatch stopwatch)
        {
            string[] parts = (argument ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
            string rest = parts.Length > 1 ? parts[1] : string.Empty;
            string message;
            bool ok;
            object? data;
            double computeMs;
            switch (sub)
            {
                case "status":
                case "list":
                {
                    (MicSelection selection, string? error, double ms) = _mic.Inventory();
                    message = sub == "list" ? MicService.DescribeList(selection) : _mic.DescribeStatus(selection, error);
                    ok = selection.Selected != null;
                    data = new { selected = selection.Selected, reason = selection.Reason, ignored = selection.Ignored, devices = selection.All, error };
                    computeMs = ms;
                    break;
                }
                case "select":
                {
                    long t0 = Stopwatch.GetTimestamp();
                    (ok, message) = _mic.Select(rest);
                    data = null;
                    computeMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
                    break;
                }
                default:
                    return Done("mic", false, "[MIC] Dùng: /mic | /mic list | /mic select <tên hoặc số thứ tự | auto>", stopwatch);
            }
            LocalUtilityResult r = _local.Record(LocalUtilityService.Mic, $"/mic {sub} {rest}".TrimEnd(), sub, ok, message, data, computeMs);
            stopwatch.Stop();
            return new CockpitCommandResult("mic", ok, r.Message, null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, r.Snapshot, Local: r);
        }

        /// <summary>
        /// TASK-17 V1: /ptt test [s] opens the selected mic for s seconds on a pool thread (16 kHz PCM16 mono, VAD, STT stub,
        /// every buffer freed); /ptt stop ends a running session early. Never more than one session.
        /// </summary>
        private CockpitCommandResult ExecutePtt(string? argument, Stopwatch stopwatch)
        {
            string[] parts = (argument ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "test";
            // TASK-18 ADR-71: "/ptt" and "/ptt <s>" = the same session as /ptt test (the recognized text goes to the command box)
            if (double.TryParse(sub.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                parts = new[] { "test", parts[0] };
            if (sub == "status")
            {
                // TASK-18 E3: no device enumeration, no worker start: < 300 ms, never blocks
                string stt = _mic.Stt is LocalSttService local ? local.DescribeStatus() : $"[STT] {_mic.Stt.Name}";
                PttResult? last = _mic.LastSession;
                return Done("ptt", true, $"{stt} · micro {(_mic.Busy ? "đang thu" : "rảnh")}" +
                    (last != null ? $" · phiên cuối {last.Device}: VAD = {(last.Vad.Vad ? "True" : "False")}, bộ đệm còn {last.OutstandingBytesAfter} B" : string.Empty), stopwatch);
            }
            if (sub == "stop")
            {
                bool running = _mic.Busy && _pttCts != null;
                _pttCts?.Cancel();
                return Done("ptt", running, running ? "[PTT] Dừng phiên thu âm, đang giải phóng bộ đệm…" : "[PTT] Không có phiên thu âm nào đang chạy.", stopwatch);
            }
            if (parts.Length > 0 && parts[0].ToLowerInvariant() != "test") return Done("ptt", false, $"[PTT] {PttUsage}", stopwatch);
            double seconds = MicService.DefaultTestSeconds;
            if (parts.Length > 1 && !double.TryParse(parts[1].Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds))
                return Done("ptt", false, $"[PTT] Số giây không hợp lệ. {PttUsage}", stopwatch);
            seconds = Math.Clamp(seconds, 0.5, MicService.MaxSessionSeconds);
            if (_mic.Busy) return Done("ptt", false, "[PTT] Đang có một phiên thu âm khác (/ptt stop để dừng).", stopwatch);
            return StartPtt(seconds, "test", $"/ptt test {seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}", stopwatch);
        }

        /// <summary>TASK-17 V1 shared by /ptt test and the F1 switch (stream until unticked, at most 30 s).</summary>
        /// <summary>TASK-19 selftest: when set, /ptt sessions read this source instead of the microphone.</summary>
        public Func<IPcmSource>? PttSourceOverride { get; set; }

        public CockpitCommandResult StartPtt(double seconds, string action, string commandText, Stopwatch? stopwatch = null)
        {
            stopwatch ??= Stopwatch.StartNew();
            var cts = new System.Threading.CancellationTokenSource();
            _pttCts?.Dispose();
            _pttCts = cts;
            MicService mic = _mic;
            LocalUtilityService local = _local;
            IPcmSource? overrideSource = PttSourceOverride?.Invoke();
            System.Threading.Tasks.Task<LocalUtilityResult> task = System.Threading.Tasks.Task.Run(() =>
            {
                PttResult r = overrideSource != null ? mic.Run(overrideSource, "selftest", seconds, cts.Token) : mic.RunDevice(seconds, cts.Token);
                return local.Record(LocalUtilityService.Ptt, commandText, action, r.Ok, MicService.DescribeResult(r), r, r.CapturedMs);
            });
            stopwatch.Stop();
            return new CockpitCommandResult("ptt", true,
                $"[PTT] Mở micro {seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s (16 kHz, 16-bit, mono, VAD)…",
                null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), LocalTask: task);
        }

        /// <summary>TASK-17 V1: ends the running /ptt or F1 stream (no-op when none).</summary>
        public void StopPtt() => _pttCts?.Cancel();

        private static CockpitCommandResult Done(string kind, bool ok, string message, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            return new CockpitCommandResult(kind, ok, message, null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));
        }
    }
}
