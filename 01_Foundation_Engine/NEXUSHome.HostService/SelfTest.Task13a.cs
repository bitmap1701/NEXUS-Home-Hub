using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-13a (memory profiling & slimming) ─────────────────────────────

        /// <summary>
        /// Gate 13a selftest (in-process, no window; the authoritative Private WS of the real HostService with WPF is
        /// measured by Verify-Task13a E2 over the pipe). Load = /ask (mock, 10 distinct prompts), /g x10, /calc x30,
        /// /note list + find, /status, /mem; then one MemorySlimming.Trim. Checks: Private WS readable without
        /// PerformanceCounter, /status and /mem show "Private WS:", SQLite cache_size cap applied (when slimming is on),
        /// the trim does not grow memory, every load command succeeded.
        /// </summary>
        private static (object, bool) MemProfile(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "mem-profile", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set (the /ask load runs in mock mode)." }, false);

            runtime.WarmUp();
            runtime.QuotaGuard.ResetSlidingWindows();
            var processor = new CockpitCommandProcessor(runtime);
            MemorySnapshot start = MemoryDiagnostics.Capture(runtime.Registry, runtime.Grounding);

            var failures = new List<string>();
            void Run(string command)
            {
                CockpitCommandResult r = processor.Execute(command, IntPtr.Zero);
                if (!r.Ok) failures.Add($"{command}: {LatestDispatchFile.Shorten(r.Message, 120)}");
                if (r.AiTask != null) Wait(r.AiTask, 10);
            }

            for (int i = 0; i < 10; i++)
            {
                Run($"/ask Gate 13a memory load {i}: tóm tắt ADR-09 Private Working Set");
                runtime.QuotaGuard.ResetSlidingWindows(); // 10 mock calls must not hit the RPM window
            }
            for (int i = 0; i < 10; i++) Run("/g ADR-09 Private Working Set Gate 13a");
            for (int i = 0; i < 30; i++) Run($"/calc sqrt({144 + i}) + 2^8");
            Run("/note list");
            Run("/note find gate");
            CockpitCommandResult status = processor.Execute("/status", IntPtr.Zero);
            CockpitCommandResult mem = processor.Execute("/mem", IntPtr.Zero);
            MemorySnapshot loaded = MemoryDiagnostics.Capture(runtime.Registry, runtime.Grounding);

            MemoryTrimResult trim = MemorySlimming.Trim("selftest");
            MemorySnapshot trimmed = MemoryDiagnostics.Capture(runtime.Registry, runtime.Grounding);

            long cacheSize;
            using (SqliteConnection connection = runtime.Store.OpenHeldConnection())
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA cache_size;";
                cacheSize = Convert.ToInt64(cmd.ExecuteScalar());
            }
            long expectedCache = MemorySlimming.Enabled ? -MemorySlimming.SqliteCacheKb : -2000;

            bool privateWsReadable = loaded.PrivateWorkingSetMb is > 0 && !loaded.PrivateWorkingSetSource.StartsWith("unavailable", StringComparison.Ordinal);
            bool hudLabelOk = loaded.HudLabel.StartsWith("Private WS: ", StringComparison.Ordinal) && loaded.HudLabel.EndsWith(" MB / 60 MB", StringComparison.Ordinal);
            bool statusOk = status.Ok && status.Message.Contains("Private WS: ", StringComparison.Ordinal);
            bool memOk = mem.Ok && mem.Kind == "mem" && mem.Message.StartsWith("[MEM] Private WS: ", StringComparison.Ordinal);
            bool cacheOk = cacheSize == expectedCache;
            bool trimOk = trim.PrivateWorkingSetAfterMb is double after && trim.PrivateWorkingSetBeforeMb is double before && after <= before + 0.5 &&
                          trim.ManagedAfterMb <= trim.ManagedBeforeMb + 0.25;
            bool loadOk = failures.Count == 0;
            bool agentsOk = loaded.AgentCount > 0 && loaded.AgentRegistryCacheMb > 0;

            var checks = new { privateWsReadable, hudLabelOk, statusOk, memOk, cacheOk, trimOk, loadOk, agentsOk };
            bool pass = privateWsReadable && hudLabelOk && statusOk && memOk && cacheOk && trimOk && loadOk && agentsOk;
            runtime.QuotaGuard.ResetSlidingWindows();
            return (new
            {
                scenario = "mem-profile",
                pass,
                checks,
                slimmingEnabled = MemorySlimming.Enabled,
                sqliteCacheSize = cacheSize,
                expectedSqliteCacheSize = expectedCache,
                start,
                loaded,
                trimmed,
                trim,
                status = status.Message,
                mem = mem.Message,
                failures
            }, pass);
        }
    }
}
