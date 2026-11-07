using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    /// <summary>
    /// Headless selftests for Verify-Task04.ps1 (TASK-04 §5). Each scenario writes
    /// 03_Local_Data_Vault/handoff/task04_selftest_&lt;scenario&gt;.json and exits 0 (pass) or 1 (fail).
    ///   dispatch            E2: 20 in-process Cockpit dispatches, P95, SQLite row, latest_dispatch.json
    ///   boost               E3: GameBoost plan (dry run) with the current foreground window
    ///   boost-live [proc]   E3: real GameBoost, then Background Yield is restored
    ///   wal-stress [sec]    E4: hold a WAL read transaction + write continuously while McpTools runs
    /// </summary>
    internal static class SelfTest
    {
        private const string E2Task = "Thiết kế C# Async TCP Server với Redis caching";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        public static int Run(string scenario, string? argument, SqliteMemoryStore store, AgentRegistry registry, string vaultPath)
        {
            string handoff = Path.Combine(vaultPath, "handoff");
            Directory.CreateDirectory(handoff);
            string name = scenario.ToLowerInvariant();

            object report;
            bool pass;
            try
            {
                (report, pass) = name switch
                {
                    "dispatch" => Dispatch(store, registry, handoff),
                    "boost" => Boost(store, registry, handoff, argument, live: false),
                    "boost-live" => Boost(store, registry, handoff, argument, live: true),
                    "wal-stress" => WalStress(store, handoff, int.TryParse(argument, out int s) ? Math.Clamp(s, 2, 60) : 10),
                    _ => ((object)new { scenario = name, pass = false, error = "Unknown scenario. Use dispatch | boost | boost-live | wal-stress." }, false)
                };
            }
            catch (Exception ex)
            {
                report = new { scenario = name, pass = false, exception = ex.ToString() };
                pass = false;
            }

            string path = Path.Combine(handoff, $"task04_selftest_{name}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            HostLog.Write($"[SELFTEST] {name} pass={pass} -> {path}");
            return pass ? 0 : 1;
        }

        private static (object, bool) Dispatch(SqliteMemoryStore store, AgentRegistry registry, string handoff)
        {
            var processor = new CockpitCommandProcessor(new SquadDispatcher(registry, store), store, registry, handoff);

            // Slash commands (functional check, not timed)
            bool statusOk = processor.Execute("/status", IntPtr.Zero).Ok;
            bool modeOk = processor.Execute("/mode sprint", IntPtr.Zero).Ok && processor.DefaultMode == NexusMode.Sprint;
            bool modeResetOk = processor.Execute("/mode micro", IntPtr.Zero).Ok;
            bool unknownRejected = !processor.Execute("/doesnotexist", IntPtr.Zero).Ok;

            const int Runs = 20;
            long rowsBefore = store.CountDispatchLogs();
            var latencies = new List<double>();
            CockpitCommandResult? last = null;
            int failures = 0;
            for (int i = 0; i < Runs; i++)
            {
                CockpitCommandResult result = processor.Execute(E2Task, IntPtr.Zero);
                if (!result.Ok || result.Dispatch == null) failures++;
                latencies.Add(result.ElapsedMs);
                last = result;
            }
            long rowsAfter = store.CountDispatchLogs();

            long lastId = last?.Dispatch?.DispatchId ?? 0;
            IReadOnlyList<RecentDispatch> recent = store.GetRecentDispatches(1);
            bool rowOk = rowsAfter - rowsBefore == Runs && recent.Count == 1 && recent[0].Id == lastId && recent[0].Status == "DISPATCHED";

            // latest_dispatch.json: exists, no BOM, valid JSON, points at the last dispatch, has the spec fields
            string latestPath = processor.LatestDispatchPath;
            bool latestExists = File.Exists(latestPath);
            bool noBom = false;
            bool latestValid = false;
            var missingFields = new List<string>();
            if (latestExists)
            {
                byte[] bytes = File.ReadAllBytes(latestPath);
                noBom = !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                using JsonDocument doc = JsonDocument.Parse(bytes);
                foreach (string field in new[] { "dispatchId", "timestampUtc", "taskSummary", "nexusMode", "squadId", "primaryAgent", "qaAgent", "systemInstructionPreview" })
                {
                    if (!doc.RootElement.TryGetProperty(field, out _)) missingFields.Add(field);
                }
                latestValid = missingFields.Count == 0 &&
                              doc.RootElement.GetProperty("dispatchId").GetInt64() == lastId;
            }

            double[] sorted = latencies.OrderBy(x => x).ToArray();
            double p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];

            bool pass = failures == 0 && p95 < 10.0 && rowOk && latestExists && noBom && latestValid &&
                        statusOk && modeOk && modeResetOk && unknownRejected;

            return (new
            {
                scenario = "dispatch",
                pass,
                task = E2Task,
                runs = Runs,
                failures,
                p95Ms = Math.Round(p95, 3),
                maxMs = Math.Round(sorted[^1], 3),
                budgetMs = 10.0,
                squadId = last?.Dispatch?.SquadId,
                primaryAgent = last?.Dispatch?.PrimaryAgent.Id,
                qaAgent = last?.Dispatch?.QaAgent.Id,
                rowsAdded = rowsAfter - rowsBefore,
                lastDispatchId = lastId,
                sqliteRowOk = rowOk,
                latestDispatchPath = latestPath,
                latestDispatchExists = latestExists,
                latestDispatchNoBom = noBom,
                latestDispatchValid = latestValid,
                latestDispatchMissingFields = missingFields,
                slashCommands = new { statusOk, modeOk, modeResetOk, unknownRejected },
                agents = registry.Count
            }, pass);
        }

        private static (object, bool) Boost(SqliteMemoryStore store, AgentRegistry registry, string handoff, string? target, bool live)
        {
            var processor = new CockpitCommandProcessor(new SquadDispatcher(registry, store), store, registry, handoff);
            IntPtr foreground = ForegroundWindowInfo.Current();
            int foregroundPid = ForegroundWindowInfo.ProcessIdOf(foreground);
            double fgBeforeMb = WorkingSetMb(foregroundPid);

            string command = string.IsNullOrWhiteSpace(target) ? "/boost" : $"/boost {target}";
            CockpitCommandResult result = processor.Execute(command, foreground, dryRunBoost: !live);
            BoostResult? boost = result.Boost;

            double fgAfterMb = WorkingSetMb(foregroundPid);
            int restored = live ? GameBoost.RestoreYield() : 0;

            var touched = boost == null ? new List<BoostEntry>() : boost.Trimmed.Concat(boost.Yielded).ToList();
            bool foregroundShielded = boost != null && boost.Shielded.Any(e => e.Pid == foregroundPid);
            bool foregroundTouched = touched.Any(e => e.Pid == foregroundPid);
            List<string> protectedTouched = touched.Where(e => GameBoost.IsProtectedName(e.Name)).Select(e => $"{e.Name}({e.Pid})").Distinct().ToList();
            List<string> protectedRunning = boost == null
                ? new List<string>()
                : boost.Shielded.Where(e => GameBoost.IsProtectedName(e.Name)).Select(e => $"{e.Name}({e.Pid})").Distinct().ToList();

            bool pass = result.Ok && boost != null && boost.TargetStatus != "NotRunning" &&
                        foregroundPid != 0 && foregroundShielded && !foregroundTouched && protectedTouched.Count == 0;

            return (new
            {
                scenario = live ? "boost-live" : "boost",
                pass,
                dryRun = !live,
                command,
                message = result.Message,
                targetStatus = boost?.TargetStatus,
                targetProcess = boost?.TargetProcess,
                foregroundPid,
                foregroundProcess = boost?.ForegroundProcess,
                foregroundTitle = ForegroundWindowInfo.TitleOf(foreground),
                foregroundShielded,
                foregroundTouched,
                foregroundWorkingSetBeforeMb = Math.Round(fgBeforeMb, 1),
                foregroundWorkingSetAfterMb = Math.Round(fgAfterMb, 1),
                protectedProcessesRunning = protectedRunning,
                protectedProcessesTouched = protectedTouched,
                trimmedCount = boost?.TrimmedCount,
                freedMb = boost?.FreedMb,
                yieldedCount = boost?.YieldedCount,
                yieldRestored = restored,
                skippedErrors = boost?.SkippedErrors,
                exceptionEscaped = false
            }, pass);
        }

        private static (object, bool) WalStress(SqliteMemoryStore store, string handoff, int seconds)
        {
            string readyMarker = Path.Combine(handoff, "task04_selftest_wal-stress.ready");
            var errors = new List<string>();
            long writes = 0;
            long reads = 0;

            // Hold one connection with an open read transaction for the whole run (the "HostService keeps a WAL
            // connection open" case), while writing from other pooled connections.
            using var held = store.OpenHeldConnection();
            using var readTx = held.BeginTransaction(deferred: true);
            using (var snapshot = held.CreateCommand())
            {
                snapshot.Transaction = readTx;
                snapshot.CommandText = "SELECT COUNT(*) FROM agent_dispatch_logs;";
                snapshot.ExecuteScalar();
            }

            File.WriteAllText(readyMarker, DateTime.UtcNow.ToString("o"));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                while (stopwatch.Elapsed.TotalSeconds < seconds)
                {
                    try
                    {
                        store.WriteAuditLog("selftest_wal_stress", ActionRiskLevel.Green_SafeRead, "{}", "Write");
                        writes++;
                        store.GetRecentDispatches(5);
                        reads++;
                    }
                    catch (Exception ex)
                    {
                        if (errors.Count < 20) errors.Add(ex.Message);
                        else errors[^1] = $"... {ex.Message}";
                    }
                    System.Threading.Thread.Sleep(5);
                }
            }
            finally
            {
                readTx.Commit();
                try { File.Delete(readyMarker); } catch (IOException) { }
            }

            bool pass = errors.Count == 0 && writes > 0 && reads > 0;
            return (new
            {
                scenario = "wal-stress",
                pass,
                seconds,
                writes,
                reads,
                heldReadTransaction = true,
                errors,
                journalMode = store.GetWalTelemetrySummary().JournalMode
            }, pass);
        }

        private static double WorkingSetMb(int pid)
        {
            if (pid <= 0) return 0;
            try
            {
                using Process p = Process.GetProcessById(pid);
                return p.WorkingSet64 / (1024.0 * 1024.0);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
