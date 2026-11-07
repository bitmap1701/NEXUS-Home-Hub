using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    /// TASK-05 (Verify-Task05.ps1, writes task05_selftest_&lt;scenario&gt;.json):
    ///   schema              E1: TASK-05 columns present, migration error-free, every row of the pre-task05 backup kept
    ///   devqa-pass          E2: Cockpit dispatch -> /pass -> SQLite + latest_dispatch.json = PASSED + evidence
    ///   devqa-strikes       E3: /fail x3 -> NEEDS_WORK 1, 2 -> ESCALATED 3 + one qa_report.md line; /needswork, /verdict
    ///   boost-acedenied     E5: GameBoost Access Denied (5) branch -> "ACE-Guard Protected (Background Yield Active)"
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
                    "schema" => Schema(store),
                    "devqa-pass" => DevQaPass(store, registry, handoff),
                    "devqa-strikes" => DevQaStrikes(store, registry, handoff),
                    "boost-acedenied" => BoostAceDenied(),
                    _ => ((object)new { scenario = name, pass = false, error = "Unknown scenario. Use dispatch | boost | boost-live | wal-stress | schema | devqa-pass | devqa-strikes | boost-acedenied." }, false)
                };
            }
            catch (Exception ex)
            {
                report = new { scenario = name, pass = false, exception = ex.ToString() };
                pass = false;
            }

            string prefix = name is "schema" or "devqa-pass" or "devqa-strikes" or "boost-acedenied" ? "task05" : "task04";
            string path = Path.Combine(handoff, $"{prefix}_selftest_{name}.json");
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

        // ───────────────────────────── TASK-05 ─────────────────────────────

        private const string E2Evidence = "Kiểm thử UDP đạt chuẩn";

        private static readonly string[] LatestDispatchRequiredFields =
        {
            "dispatchId", "status", "retryCount", "evidenceNotes", "updatedAtUtc",
            "squadId", "primaryAgent", "qaAgent", "taskSummary", "nexusMode"
        };

        /// <summary>E1: schema + "không mất dữ liệu cũ", independent of which process ran the migration first.</summary>
        private static (object, bool) Schema(SqliteMemoryStore store)
        {
            SchemaMigrationReport migration = store.MigrationReport;
            string dbPath = store.DatabasePath;
            string dir = Path.GetDirectoryName(dbPath)!;
            string? backup = Directory.GetFiles(dir, $"{Path.GetFileNameWithoutExtension(dbPath)}.pre-task05-*.db")
                .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault(); // oldest = the real TASK-04 snapshot

            var columns = new List<string>();
            string tableSql = string.Empty;
            long rows;
            long? backupRows = null;
            long? backupRowsMissing = null;

            using (SqliteConnection connection = store.OpenHeldConnection())
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM pragma_table_info('agent_dispatch_logs') ORDER BY cid;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) columns.Add(reader.GetString(0));
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_dispatch_logs';";
                    tableSql = Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM agent_dispatch_logs;";
                    rows = Convert.ToInt64(cmd.ExecuteScalar());
                }

                if (backup != null)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "ATTACH DATABASE $path AS bak;";
                        cmd.Parameters.AddWithValue("$path", backup);
                        cmd.ExecuteNonQuery();
                    }
                    try
                    {
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM bak.agent_dispatch_logs;";
                            backupRows = Convert.ToInt64(cmd.ExecuteScalar());
                        }
                        using (var cmd = connection.CreateCommand())
                        {
                            // Every pre-migration row must still exist with the same id, time, squad and task.
                            cmd.CommandText =
                                "SELECT COUNT(*) FROM bak.agent_dispatch_logs b LEFT JOIN main.agent_dispatch_logs m " +
                                "ON m.id = b.id AND m.timestamp_utc = b.timestamp_utc AND m.squad_id = b.squad_id AND m.task_summary = b.task_summary " +
                                "WHERE m.id IS NULL;";
                            backupRowsMissing = Convert.ToInt64(cmd.ExecuteScalar());
                        }
                    }
                    finally
                    {
                        using var detach = connection.CreateCommand();
                        detach.CommandText = "DETACH DATABASE bak;";
                        detach.ExecuteNonQuery();
                    }
                }
            }

            string[] required = { "retry_count", "evidence_notes", "updated_at_utc" };
            List<string> missingColumns = required.Where(c => !columns.Contains(c)).ToList();
            bool checkAllowsEscalated = tableSql.Contains("ESCALATED", StringComparison.Ordinal);
            bool dataKept = backup == null || backupRowsMissing == 0;

            bool pass = migration.Error == null && missingColumns.Count == 0 && checkAllowsEscalated && dataKept &&
                        migration.RowsAfter >= migration.RowsBefore;
            return (new
            {
                scenario = "schema",
                pass,
                columns,
                missingColumns,
                checkAllowsEscalated,
                rows,
                migrationThisProcess = new
                {
                    migration.Rebuilt,
                    migration.AddedColumns,
                    migration.RowsBefore,
                    migration.RowsAfter,
                    migration.BackupPath,
                    migration.Error
                },
                preTask05Backup = backup,
                backupRows,
                backupRowsMissing,
                dataKept,
                note = backup == null
                    ? "No pre-task05 backup: the table was created with the TASK-05 schema or migrated by ALTER ADD COLUMN only."
                    : "Rows of the oldest pre-task05 backup were matched by id/timestamp/squad/task against the live table."
            }, pass);
        }

        /// <summary>E2: dispatch from the Cockpit processor, then /pass with evidence.</summary>
        private static (object, bool) DevQaPass(SqliteMemoryStore store, AgentRegistry registry, string handoff)
        {
            var processor = new CockpitCommandProcessor(new SquadDispatcher(registry, store), store, registry, handoff);
            CockpitCommandResult dispatch = processor.Execute(E2Task, IntPtr.Zero);
            long id = dispatch.Dispatch?.DispatchId ?? 0;
            LatestCheck afterDispatch = ReadLatest(processor.LatestDispatchPath);

            CockpitCommandResult verdict = processor.Execute($"/pass {E2Evidence}", IntPtr.Zero);
            RecentDispatch? row = id > 0 ? store.GetDispatch(id) : null;
            LatestCheck latest = ReadLatest(processor.LatestDispatchPath);

            bool dbOk = row != null && row.Status == "PASSED" && row.EvidenceNotes == E2Evidence && row.RetryCount == 0 &&
                        row.UpdatedAtUtc.Length > 0;
            bool latestOk = latest.Valid && latest.DispatchId == id && latest.Status == "PASSED" && latest.EvidenceNotes == E2Evidence;
            bool dispatchFileOk = afterDispatch.Valid && afterDispatch.DispatchId == id && afterDispatch.Status == "DISPATCHED";

            // Parser guards: bad /verdict is rejected without touching the row; unknown id is an error.
            bool badVerdictRejected = !processor.Execute($"/verdict {id} MAYBE", IntPtr.Zero).Ok;
            bool unknownIdRejected = !processor.Execute("/pass #999999999 không tồn tại", IntPtr.Zero).Ok;
            bool rowUntouched = store.GetDispatch(id)?.Status == "PASSED";

            bool pass = dispatch.Ok && id > 0 && verdict.Ok && verdict.Verdict?.Dispatch?.Id == id && dbOk && latestOk &&
                        dispatchFileOk && badVerdictRejected && unknownIdRejected && rowUntouched;
            return (new
            {
                scenario = "devqa-pass",
                pass,
                command = $"/pass {E2Evidence}",
                dispatchId = id,
                squadId = dispatch.Dispatch?.SquadId,
                verdictMessage = verdict.Message,
                db = row == null ? null : new { row.Status, row.RetryCount, row.EvidenceNotes, row.UpdatedAtUtc },
                dbOk,
                latestAfterDispatch = afterDispatch,
                latestAfterPass = latest,
                latestOk,
                badVerdictRejected,
                unknownIdRejected,
                verdictElapsedMs = verdict.ElapsedMs
            }, pass);
        }

        /// <summary>E3: 3-strike circuit breaker, escalation line written exactly once; aliases on a second dispatch.</summary>
        private static (object, bool) DevQaStrikes(SqliteMemoryStore store, AgentRegistry registry, string handoff)
        {
            var processor = new CockpitCommandProcessor(new SquadDispatcher(registry, store), store, registry, handoff);
            CockpitCommandResult dispatch = processor.Execute("Tối ưu độ trễ UDP cho Delta Force overlay (E3 strike test)", IntPtr.Zero);
            long id = dispatch.Dispatch?.DispatchId ?? 0;

            var strikes = new List<object>();
            var observed = new List<(string Status, int Retry, bool EscalatedNow)>();
            for (int i = 1; i <= 3; i++)
            {
                // Explicit #id so a dispatch from another process in between cannot steal a strike.
                CockpitCommandResult r = processor.Execute($"/fail #{id} Strike {i}: packet loss còn {4 - i}%", IntPtr.Zero);
                RecentDispatch? row = store.GetDispatch(id);
                observed.Add((row?.Status ?? "?", row?.RetryCount ?? -1, r.Verdict?.EscalatedNow == true));
                strikes.Add(new { strike = i, ok = r.Ok, status = row?.Status, retryCount = row?.RetryCount, escalatedNow = r.Verdict?.EscalatedNow, message = r.Message });
            }
            LatestCheck latest = ReadLatest(processor.LatestDispatchPath);

            // A 4th NEEDS_WORK on an escalated dispatch must not write a second report line.
            CockpitCommandResult fourth = processor.Execute($"/fail #{id} Strike 4 (sau escalation)", IntPtr.Zero);

            string marker = $"### [3-STRIKE ESCALATION] Dispatch #{id} |";
            string[] reportLines = File.Exists(processor.QaReportPath)
                ? File.ReadAllLines(processor.QaReportPath, Encoding.UTF8).Where(l => l.StartsWith(marker, StringComparison.Ordinal)).ToArray()
                : Array.Empty<string>();
            string expectedSquad = $"| Squad: {dispatch.Dispatch?.SquadId} |";
            bool reportOk = reportLines.Length == 1 && reportLines[0].Contains(expectedSquad, StringComparison.Ordinal) &&
                            reportLines[0].Contains("| Task: ", StringComparison.Ordinal) &&
                            reportLines[0].Contains("| Evidence: Strike 3", StringComparison.Ordinal);

            bool strikesOk = observed.Count == 3 &&
                             observed[0] == ("NEEDS_WORK", 1, false) &&
                             observed[1] == ("NEEDS_WORK", 2, false) &&
                             observed[2] == ("ESCALATED", 3, true) &&
                             fourth.Verdict?.EscalatedNow == false && store.GetDispatch(id)?.Status == "ESCALATED";
            bool latestOk = latest.Valid && latest.DispatchId == id && latest.Status == "ESCALATED" && latest.RetryCount == 3;

            // Aliases: /needswork, then /verdict #id PASSED keeps the retry count ("PASSED (Retries: 1)").
            CockpitCommandResult second = processor.Execute("Viết test hồi quy cho MCP update_dispatch_verdict (E3 alias test)", IntPtr.Zero);
            long id2 = second.Dispatch?.DispatchId ?? 0;
            processor.Execute($"/needswork #{id2} thiếu log", IntPtr.Zero);
            CockpitCommandResult final = processor.Execute($"/verdict #{id2} PASSED đã bổ sung log", IntPtr.Zero);
            RecentDispatch? row2 = store.GetDispatch(id2);
            string label2 = row2 == null ? string.Empty : DispatchVerdictService.StatusLabel(row2.Status, row2.RetryCount);
            bool aliasOk = final.Ok && row2 != null && row2.Status == "PASSED" && row2.RetryCount == 1 && label2 == "PASSED (Retries: 1)";

            bool pass = dispatch.Ok && id > 0 && strikesOk && latestOk && reportOk && aliasOk;
            return (new
            {
                scenario = "devqa-strikes",
                pass,
                dispatchId = id,
                squadId = dispatch.Dispatch?.SquadId,
                strikes,
                strikesOk,
                fourthStrike = new { status = store.GetDispatch(id)?.Status, escalatedNow = fourth.Verdict?.EscalatedNow },
                latestAfterStrike3 = latest,
                latestOk,
                qaReportPath = processor.QaReportPath,
                escalationLines = reportLines,
                reportOk,
                aliasTest = new { dispatchId = id2, status = row2?.Status, retryCount = row2?.RetryCount, label = label2, aliasOk },
                labels = new
                {
                    dispatched = DispatchVerdictService.StatusLabel("DISPATCHED", 0),
                    strike2 = DispatchVerdictService.StatusLabel("NEEDS_WORK", 2),
                    escalated = DispatchVerdictService.StatusLabel("ESCALATED", 3)
                }
            }, pass);
        }

        /// <summary>
        /// E5 (ADR-11): deterministic Access Denied branch. Dry run + simulated Win32Exception(5): no process is changed,
        /// but the code path is the real one (catch -> ACE-Guard status -> Background Yield plan).
        /// </summary>
        private static (object, bool) BoostAceDenied()
        {
            using Process self = Process.GetCurrentProcess();
            string target = self.ProcessName; // always running, not anti-cheat named -> exercises the catch, not the name shield
            BoostResult? result = null;
            string? escaped = null;
            try
            {
                result = GameBoost.Run(target, IntPtr.Zero, dryRun: true, simulateAccessDenied: true);
            }
            catch (Exception ex)
            {
                escaped = ex.ToString();
            }

            const string Expected = "ACE-Guard Protected (Background Yield Active)";
            List<string> protectedPlanned = result == null
                ? new List<string>()
                : result.Yielded.Concat(result.Trimmed).Where(e => GameBoost.IsProtectedName(e.Name)).Select(e => $"{e.Name}({e.Pid})").ToList();
            int foregroundPid = result?.ForegroundPid ?? -1;
            bool foregroundPlanned = result != null && foregroundPid > 0 && result.Yielded.Concat(result.Trimmed).Any(e => e.Pid == foregroundPid);

            // Real anti-cheat process, if one is running: must be shielded by name without ever being opened.
            string? aceName = null;
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (aceName == null && GameBoost.IsProtectedName(p.ProcessName)) aceName = p.ProcessName;
                    }
                    catch (InvalidOperationException)
                    {
                        // exited meanwhile
                    }
                }
            }
            string? aceStatus = aceName == null ? null : GameBoost.Run(aceName, IntPtr.Zero, dryRun: true).TargetStatus;

            bool pass = escaped == null && result != null && result.TargetStatus == Expected && result.DeniedErrorCode == 5 &&
                        protectedPlanned.Count == 0 && !foregroundPlanned && (aceStatus == null || aceStatus == Expected);
            return (new
            {
                scenario = "boost-acedenied",
                pass,
                simulated = "Win32Exception(5) ERROR_ACCESS_DENIED on PriorityClass = High",
                target,
                targetStatus = result?.TargetStatus,
                expected = Expected,
                deniedErrorCode = result?.DeniedErrorCode,
                exceptionEscaped = escaped,
                dryRun = true,
                yieldPlanned = result?.YieldedCount,
                yieldPlan = result?.Yielded.Select(e => $"{e.Name}({e.Pid}) {e.WorkingSetMb} MB").Take(15),
                protectedProcessesPlanned = protectedPlanned,
                foregroundPlanned,
                realAntiCheatProcess = aceName,
                realAntiCheatStatus = aceStatus,
                message = result?.Message
            }, pass);
        }

        private sealed record LatestCheck(bool Exists, bool NoBom, bool Valid, long DispatchId, string? Status, int RetryCount,
            string? EvidenceNotes, string? Source, IReadOnlyList<string> MissingFields);

        private static LatestCheck ReadLatest(string path)
        {
            if (!File.Exists(path)) return new LatestCheck(false, false, false, 0, null, 0, null, null, LatestDispatchRequiredFields);
            byte[] bytes = File.ReadAllBytes(path);
            bool noBom = !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            try
            {
                using JsonDocument doc = JsonDocument.Parse(bytes);
                JsonElement root = doc.RootElement;
                List<string> missing = LatestDispatchRequiredFields.Where(f => !root.TryGetProperty(f, out _)).ToList();
                string? Str(string f) => root.TryGetProperty(f, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                long id = root.TryGetProperty("dispatchId", out JsonElement d) && d.TryGetInt64(out long l) ? l : 0;
                int retry = root.TryGetProperty("retryCount", out JsonElement rc) && rc.TryGetInt32(out int n) ? n : -1;
                return new LatestCheck(true, noBom, noBom && missing.Count == 0, id, Str("status"), retry, Str("evidenceNotes"), Str("source"), missing);
            }
            catch (JsonException)
            {
                return new LatestCheck(true, noBom, false, 0, null, 0, null, null, LatestDispatchRequiredFields);
            }
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
