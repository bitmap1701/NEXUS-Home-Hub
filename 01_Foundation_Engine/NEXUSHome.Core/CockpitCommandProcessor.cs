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
        DispatchPhaseTimings? Phases = null);

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

        public CockpitCommandProcessor(SquadDispatcher dispatcher, SqliteMemoryStore store, AgentRegistry registry, string handoffDirectory)
        {
            _dispatcher = dispatcher;
            _store = store;
            _registry = registry;
            LatestDispatchPath = Path.Combine(handoffDirectory, LatestDispatchFile.FileName);
            _verdicts = new DispatchVerdictService(store, registry, handoffDirectory, "CockpitUI");
        }

        public string QaReportPath => _verdicts.QaReportPath;

        public NexusMode DefaultMode { get; private set; } = NexusMode.Micro;

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
                    case "/mode":
                        if (!string.IsNullOrWhiteSpace(argument) && SquadDispatcher.TryParseMode(argument, out NexusMode mode))
                        {
                            DefaultMode = mode;
                            return Done("mode", true, $"[MODE] Chế độ NEXUS mặc định: {mode}.", stopwatch);
                        }
                        return Done("mode", false, "Dùng: /mode micro | sprint | full", stopwatch);
                    default:
                        return Done("unknown", false, $"Lệnh không hỗ trợ: {command}. Dùng /boost, /status, /mode, /pass, /fail, /verdict hoặc gõ nhiệm vụ.", stopwatch);
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
