using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NEXUSHomeHub.Core
{
    public sealed record CockpitCommandResult(
        string Kind,
        bool Ok,
        string Message,
        DispatchPackage? Dispatch,
        BoostResult? Boost,
        double ElapsedMs);

    /// <summary>
    /// TASK-04 §3.A: UI-independent command handling for the Cockpit command box (also driven by the pipe and
    /// HostService --selftest, so it can be verified without a window).
    ///   /boost [process] | /boost off   -> GameBoost (ADR-06) / restore Background Yield
    ///   /status | /telemetry            -> HostService RAM, foreground window, agents, mode
    ///   /mode micro|sprint|full         -> default NEXUS mode for later dispatches
    ///   anything else                   -> SquadDispatcher (in-process) + latest_dispatch.json
    /// </summary>
    public sealed class CockpitCommandProcessor
    {
        private const int InstructionPreviewChars = 600;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly SquadDispatcher _dispatcher;
        private readonly SqliteMemoryStore _store;
        private readonly AgentRegistry _registry;

        public CockpitCommandProcessor(SquadDispatcher dispatcher, SqliteMemoryStore store, AgentRegistry registry, string handoffDirectory)
        {
            _dispatcher = dispatcher;
            _store = store;
            _registry = registry;
            LatestDispatchPath = Path.Combine(handoffDirectory, "latest_dispatch.json");
        }

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
                    case "/mode":
                        if (!string.IsNullOrWhiteSpace(argument) && SquadDispatcher.TryParseMode(argument, out NexusMode mode))
                        {
                            DefaultMode = mode;
                            return Done("mode", true, $"[MODE] Chế độ NEXUS mặc định: {mode}.", stopwatch);
                        }
                        return Done("mode", false, "Dùng: /mode micro | sprint | full", stopwatch);
                    default:
                        return Done("unknown", false, $"Lệnh không hỗ trợ: {command}. Dùng /boost, /status, /mode hoặc gõ nhiệm vụ.", stopwatch);
                }
            }

            DispatchOutcome outcome = _dispatcher.Dispatch(text, DefaultMode.ToString(), null);
            if (outcome.Package == null)
            {
                return Done("dispatch", false, $"[DISPATCH] Lỗi: {outcome.Error}", stopwatch);
            }

            DispatchPackage package = outcome.Package;
            string? writeError = WriteLatestDispatch(package, text);
            _store.WriteAuditLog("cockpit_dispatch", ActionRiskLevel.Green_SafeRead,
                JsonSerializer.Serialize(new { dispatchId = package.DispatchId, squad = package.SquadId }), "DispatchedFromCockpit");

            string message =
                $"[DISPATCH #{package.DispatchId}] {package.SquadId} {package.SquadName} → {package.PrimaryAgent.Name} | " +
                $"QA {package.QaAgent.Name} | {package.NexusMode}" +
                (writeError == null ? string.Empty : $" | latest_dispatch.json LỖI: {writeError}");

            stopwatch.Stop();
            return new CockpitCommandResult("dispatch", writeError == null, message, package, null,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));
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

        /// <summary>Writes handoff/latest_dispatch.json atomically (UTF-8 without BOM). Returns an error message or null.</summary>
        private string? WriteLatestDispatch(DispatchPackage package, string task)
        {
            try
            {
                string preview = package.SystemInstruction.Length > InstructionPreviewChars
                    ? package.SystemInstruction.Substring(0, InstructionPreviewChars) + "…"
                    : package.SystemInstruction;

                var payload = new
                {
                    dispatchId = package.DispatchId,
                    timestampUtc = DateTime.UtcNow.ToString("o"),
                    source = "CockpitUI",
                    taskSummary = task.Length > 200 ? task.Substring(0, 200) + "…" : task,
                    nexusMode = package.NexusMode,
                    squadId = package.SquadId,
                    squadName = package.SquadName,
                    primaryAgent = new { id = package.PrimaryAgent.Id, name = package.PrimaryAgent.Name },
                    qaAgent = new { id = package.QaAgent.Id, name = package.QaAgent.Name },
                    realityChecker = SquadDispatcher.RealityCheckerId,
                    matchedKeywords = package.MatchedKeywords.ToArray(),
                    elapsedMs = package.ElapsedMs,
                    systemInstructionPreview = preview
                };

                Directory.CreateDirectory(Path.GetDirectoryName(LatestDispatchPath)!);
                string temp = LatestDispatchPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(payload, Json), Utf8NoBom);
                File.Move(temp, LatestDispatchPath, overwrite: true);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static CockpitCommandResult Done(string kind, bool ok, string message, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            return new CockpitCommandResult(kind, ok, message, null, null, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));
        }
    }
}
