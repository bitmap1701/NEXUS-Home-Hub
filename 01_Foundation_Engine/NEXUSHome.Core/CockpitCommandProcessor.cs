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
        System.Threading.Tasks.Task<ConsultOutcome>? AiTask = null);

    /// <summary>Where a Cockpit dispatch spends its time (Gate 5 diagnosis of the P95 regression).</summary>
    public sealed record DispatchPhaseTimings(double RouteAndInsertMs, double LatestFileMs, double AuditMs);

    /// <summary>
    /// TASK-04 §3.A: UI-independent command handling for the Cockpit command box (also driven by the pipe and
    /// HostService --selftest, so it can be verified without a window).
    ///   /boost [process] | /boost off   -> GameBoost (ADR-06) / restore Background Yield
    ///   /status | /telemetry            -> HostService RAM, foreground window, agents, mode
    ///   /mode micro|sprint|full         -> default NEXUS mode for later dispatches
    ///   /pass [#id] [evidence]          -> verdict PASSED (TASK-05, ADR-12); no id = newest dispatch
    ///   /fail | /needswork [#id] [why]  -> verdict NEEDS_WORK, 3rd strike -> ESCALATED (ADR-13)
    ///   /verdict &lt;id&gt; &lt;PASSED|NEEDS_WORK&gt; [notes]
    ///   /reload                         -> re-read nexus_config.json (TASK-06 ADR-20); /config shows what is applied
    ///   /ask &lt;question&gt;                 -> TASK-07: dispatch now (sync), Gemini answer in the background (AiTask)
    ///   /apikey set &lt;key&gt; | status | clear -> TASK-07 DPAPI key vault (the key is never logged or stored in clear)
    ///   anything else                   -> SquadDispatcher (in-process) + latest_dispatch.json
    /// </summary>
    public sealed class CockpitCommandProcessor
    {
        private static readonly Regex HashId = new(@"^#(\d+)\s*(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex VerdictArgs = new(@"^#?(\d+)\s+(\S+)\s*(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

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
                        return ExecuteAsk(argument, stopwatch);
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
                        return Done("unknown", false, $"Lệnh không hỗ trợ: {command}. Dùng /ask, /boost, /status, /mode, /pass, /fail, /verdict, /reload, /config, /apikey hoặc gõ nhiệm vụ.", stopwatch);
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

        /// <summary>
        /// TASK-07 §3.4: synchronous part only (route + DISPATCHED + latest_dispatch.json "THINKING"); the Gemini call runs
        /// on the thread pool and is returned as <see cref="CockpitCommandResult.AiTask"/> so the Cockpit can give focus back at once.
        /// </summary>
        private CockpitCommandResult ExecuteAsk(string? argument, Stopwatch stopwatch)
        {
            string question = (argument ?? string.Empty).Trim();
            if (question.Length == 0) return Done("ask", false, "Dùng: /ask <câu hỏi / nhiệm vụ>", stopwatch);

            ConsultStart start = _consult.Start(question, DefaultMode.ToString(), null);
            if (start.Package == null || start.Snapshot == null)
                return Done("ask", false, $"[ASK] Lỗi: {start.Error}", stopwatch);

            DispatchPackage package = start.Package;
            System.Threading.Tasks.Task<ConsultOutcome> aiTask =
                System.Threading.Tasks.Task.Run(() => _consult.CompleteAsync(package, question));
            stopwatch.Stop();

            string message =
                $"[ASK #{package.DispatchId}] {package.SquadId} {package.SquadName} → {package.PrimaryAgent.Name} | " +
                $"THINKING... Calling Gemini ({(GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : _config.Current.GeminiModel)})" +
                (start.FileError == null ? string.Empty : $" | latest_dispatch.json LỖI: {start.FileError}");
            return new CockpitCommandResult("ask", true, message, package, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), null, start.Snapshot, AiTask: aiTask);
        }

        private NexusMode ModeFromConfig() =>
            SquadDispatcher.TryParseMode(_config.Current.DefaultNexusMode, out NexusMode mode) ? mode : NexusMode.Micro;

        /// <summary>ADR-20: re-read nexus_config.json; a broken file never stops the Cockpit (last valid config stays).</summary>
        private CockpitCommandResult ExecuteReload(Stopwatch stopwatch)
        {
            NexusConfigLoadResult load = _config.Reload();
            DefaultMode = ModeFromConfig();
            NexusConfig c = _config.Current;
            string applied =
                $"defaultNexusMode={c.DefaultNexusMode}, memoryTrimFloorMb={c.MemoryTrimFloorMb}, watcherDebounceMs={c.WatcherDebounceMs}, " +
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
