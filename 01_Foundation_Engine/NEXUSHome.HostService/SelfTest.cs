using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// TASK-06 (Verify-Task06.ps1, writes task06_selftest_&lt;scenario&gt;.json):
    ///   config              E2: nexus_config.json auto-create / reload / broken JSON / empty fields, in a temp folder
    ///   adr19               E3: post-ESCALATED and post-PASSED state machine, one escalation line per dispatch
    ///   sla                 E1/E4: effective config of this process + ADR-17 SLA profile (compared with McpTools)
    /// TASK-07 (Verify-Task07.ps1, writes task07_selftest_&lt;scenario&gt;.json; the test key is never written to the report):
    ///   schema07            E1: ai_response / ai_model / ai_latency_ms columns, rows kept
    ///   vault               E2: DPAPI round trip, no plaintext on disk, mask, /apikey set|status|clear, pasted-key guard
    ///   ask                 E3: /ask with NEXUS_GEMINI_MOCK_RESPONSE: Squad1, ai_* in SQLite + latest_dispatch.json
    ///   ask-missingkey      E4: /ask with no key (isolated vault, no env key): DISPATCHED + [LOCAL DISPATCH ONLY]
    ///   ask-live            E5: one real /ask when a key is configured (skipped otherwise)
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

        public static int Run(string scenario, string? argument, NexusRuntime runtime)
        {
            SqliteMemoryStore store = runtime.Store;
            string handoff = runtime.HandoffDirectory;
            string name = scenario.ToLowerInvariant();

            object report;
            bool pass;
            try
            {
                (report, pass) = name switch
                {
                    "dispatch" => Dispatch(runtime),
                    "boost" => Boost(runtime, argument, live: false),
                    "boost-live" => Boost(runtime, argument, live: true),
                    "wal-stress" => WalStress(store, handoff, int.TryParse(argument, out int s) ? Math.Clamp(s, 2, 60) : 10),
                    "schema" => Schema(store),
                    "devqa-pass" => DevQaPass(runtime),
                    "devqa-strikes" => DevQaStrikes(runtime),
                    "boost-acedenied" => BoostAceDenied(),
                    "config" => ConfigResilience(),
                    "adr19" => Adr19StateMachine(runtime),
                    "sla" => Sla(runtime),
                    "schema07" => Schema07(runtime),
                    "vault" => Vault(runtime),
                    "ask" => AskMock(runtime),
                    "ask-missingkey" => AskMissingKey(runtime),
                    "ask-live" => AskLive(runtime),
                    _ => ((object)new { scenario = name, pass = false, error = "Unknown scenario. Use dispatch | boost | boost-live | wal-stress | schema | devqa-pass | devqa-strikes | boost-acedenied | config | adr19 | sla | schema07 | vault | ask | ask-missingkey | ask-live." }, false)
                };
            }
            catch (Exception ex)
            {
                report = new { scenario = name, pass = false, exception = ex.ToString() };
                pass = false;
            }

            string prefix = name switch
            {
                "schema" or "devqa-pass" or "devqa-strikes" or "boost-acedenied" => "task05",
                "config" or "adr19" or "sla" => "task06",
                "schema07" or "vault" or "ask" or "ask-missingkey" or "ask-live" => "task07",
                _ => "task04"
            };
            string path = Path.Combine(handoff, $"{prefix}_selftest_{name}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            HostLog.Write($"[SELFTEST] {name} pass={pass} -> {path}");
            return pass ? 0 : 1;
        }

        private static (object, bool) Dispatch(NexusRuntime runtime)
        {
            SqliteMemoryStore store = runtime.Store;
            AgentRegistry registry = runtime.Registry;
            var processor = new CockpitCommandProcessor(runtime);

            // Slash commands (functional check, not timed)
            bool statusOk = processor.Execute("/status", IntPtr.Zero).Ok;
            bool modeOk = processor.Execute("/mode sprint", IntPtr.Zero).Ok && processor.DefaultMode == NexusMode.Sprint;
            bool modeResetOk = processor.Execute("/mode micro", IntPtr.Zero).Ok;
            bool unknownRejected = !processor.Execute("/doesnotexist", IntPtr.Zero).Ok;

            const int Runs = 20;
            long rowsBefore = store.CountDispatchLogs();
            var latencies = new List<double>();
            var phases = new List<DispatchPhaseTimings>();
            CockpitCommandResult? last = null;
            int failures = 0;
            for (int i = 0; i < Runs; i++)
            {
                CockpitCommandResult result = processor.Execute(E2Task, IntPtr.Zero);
                if (!result.Ok || result.Dispatch == null) failures++;
                latencies.Add(result.ElapsedMs);
                if (result.Phases != null) phases.Add(result.Phases);
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

            // TASK-06 ADR-17: 10 ms on the desktop, 25 ms while a game / anti-cheat (combatProcessPrefixes) runs.
            DispatchSlaProfile sla = runtime.Config.DetectSla();
            bool pass = failures == 0 && p95 < sla.BudgetMs && rowOk && latestExists && noBom && latestValid &&
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
                budgetMs = sla.BudgetMs,
                slaProfile = sla.Profile,
                slaMatchedProcesses = sla.MatchedProcesses,
                latenciesMs = latencies.Select(x => Math.Round(x, 3)),
                phaseMaxMs = phases.Count == 0 ? null : new
                {
                    routeAndInsert = phases.Max(x => x.RouteAndInsertMs),
                    latestFile = phases.Max(x => x.LatestFileMs),
                    audit = phases.Max(x => x.AuditMs)
                },
                phasesMs = phases,
                gameOrAntiCheatRunning = RunningProtectedProcesses(),
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

        private static (object, bool) Boost(NexusRuntime runtime, string? target, bool live)
        {
            var processor = new CockpitCommandProcessor(runtime);
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
        private static (object, bool) DevQaPass(NexusRuntime runtime)
        {
            SqliteMemoryStore store = runtime.Store;
            var processor = new CockpitCommandProcessor(runtime);
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
        private static (object, bool) DevQaStrikes(NexusRuntime runtime)
        {
            SqliteMemoryStore store = runtime.Store;
            var processor = new CockpitCommandProcessor(runtime);
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
            // A running process that is neither protected (nexus_config.json, which also lists "NEXUSHome") nor a system
            // process: exercises the catch path, not the name shield. Dry run: nothing is changed.
            string? target = PickUnprotectedProcess();
            if (target == null)
            {
                return (new { scenario = "boost-acedenied", pass = false, error = "No unprotected user process found to target." }, false);
            }
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
                        string n = p.ProcessName;
                        if (aceName == null && GameBoost.IsProtectedName(n) && !GameBoost.IsSystemName(n) &&
                            !n.StartsWith("NEXUSHome", StringComparison.OrdinalIgnoreCase)) aceName = n;
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

        // ───────────────────────────── TASK-06 ─────────────────────────────

        private static readonly string[] RequiredConfigFields =
        {
            "defaultNexusMode", "preferredMonitorKeywords", "passiveHudHotkey", "commandModeHotkey", "memoryTrimFloorMb",
            "watcherDebounceMs", "dispatchSlaDesktopMs", "dispatchSlaCombatMs", "protectedProcessPrefixes"
        };

        /// <summary>E2 (service level, isolated): a fresh folder under %TEMP% per run, the real config is not touched.</summary>
        private static (object, bool) ConfigResilience()
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            string folder = Path.Combine(Path.GetTempPath(), "nexus_task06_config_test", $"run-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}");
            string path = Path.Combine(folder, "config", NexusConfigService.FileName);

            // 1. Missing folder + file -> created with defaults, no BOM, all required fields.
            var service = new NexusConfigService(path);
            byte[] created = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            bool createdNoBom = created.Length > 0 && !(created[0] == 0xEF && created[1] == 0xBB && created[2] == 0xBF);
            List<string> missingFields = RequiredConfigFields.ToList();
            if (created.Length > 0)
            {
                using JsonDocument doc = JsonDocument.Parse(created);
                missingFields = RequiredConfigFields.Where(f => !doc.RootElement.TryGetProperty(f, out _)).ToList();
            }
            bool createOk = service.LastResult.Created && createdNoBom && missingFields.Count == 0 &&
                            NexusConfigService.Serialize(service.Current) == NexusConfigService.Serialize(NexusConfig.Default);

            // 2. Edit memoryTrimFloorMb 50 -> 64 and defaultNexusMode -> Sprint, then reload.
            JsonObject edited = JsonNode.Parse(File.ReadAllText(path, utf8))!.AsObject();
            edited["memoryTrimFloorMb"] = 64;
            edited["defaultNexusMode"] = "Sprint";
            File.WriteAllText(path, edited.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), utf8);
            NexusConfigLoadResult reload = service.Reload();
            bool reloadOk = reload.Ok && service.Current.MemoryTrimFloorMb == 64 && service.Current.DefaultNexusMode == "Sprint" &&
                            GameBoost.MemoryTrimFloorMb == 64;

            // 3. Broken JSON -> no exception, last valid config kept, broken copy saved, file rewritten.
            const string Broken = "{ \"memoryTrimFloorMb\": 80, \"defaultNexusMode\": ";
            File.WriteAllText(path, Broken, utf8);
            NexusConfigLoadResult broken;
            string? escaped = null;
            try
            {
                broken = service.Reload();
            }
            catch (Exception ex)
            {
                escaped = ex.ToString();
                broken = new NexusConfigLoadResult(false, false, false, null, Array.Empty<string>(), ex.Message, string.Empty);
            }
            bool keptLastValid = service.Current.MemoryTrimFloorMb == 64 && service.Current.DefaultNexusMode == "Sprint";
            bool backupOk = broken.BackupPath != null && File.Exists(broken.BackupPath) && File.ReadAllText(broken.BackupPath, utf8) == Broken;
            int? restoredFloor = null;
            try
            {
                using JsonDocument restored = JsonDocument.Parse(File.ReadAllText(path, utf8));
                restoredFloor = restored.RootElement.GetProperty("memoryTrimFloorMb").GetInt32();
            }
            catch (Exception)
            {
                // stays null -> fail
            }
            bool brokenOk = escaped == null && !broken.Ok && broken.Restored && keptLastValid && backupOk && restoredFloor == 64;

            // 4. Empty / missing fields -> those keep their last valid value, the valid ones apply.
            JsonObject partial = JsonNode.Parse(NexusConfigService.Serialize(service.Current))!.AsObject();
            partial["memoryTrimFloorMb"] = "";
            partial["defaultNexusMode"] = "";
            partial.Remove("watcherDebounceMs");
            partial["dispatchSlaCombatMs"] = 30;
            File.WriteAllText(path, partial.ToJsonString(), utf8);
            NexusConfigLoadResult partialLoad = service.Reload();
            bool partialOk = partialLoad.Ok && partialLoad.Warnings.Count >= 3 && service.Current.MemoryTrimFloorMb == 64 &&
                             service.Current.DefaultNexusMode == "Sprint" && service.Current.WatcherDebounceMs == 150 &&
                             service.Current.DispatchSlaCombatMs == 30;

            // 5. File saved with a BOM (old Notepad) -> accepted and rewritten without BOM.
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8.GetBytes(NexusConfigService.Serialize(service.Current))).ToArray();
            File.WriteAllBytes(path, withBom);
            NexusConfigLoadResult bomLoad = service.Reload();
            byte[] afterBom = File.ReadAllBytes(path);
            bool bomOk = bomLoad.Ok && !(afterBom.Length >= 3 && afterBom[0] == 0xEF && afterBom[1] == 0xBB && afterBom[2] == 0xBF);

            // 6. Hotkey grammar.
            var hotkeys = new Dictionary<string, bool>
            {
                ["Alt+Space"] = HotkeyChord.TryParse("Alt+Space", out _),
                ["Ctrl+Shift+Space"] = HotkeyChord.TryParse("Ctrl+Shift+Space", out _),
                ["Win+F9"] = HotkeyChord.TryParse("Win+F9", out _),
                ["Shift+A (rejected)"] = !HotkeyChord.TryParse("Shift+A", out _),
                ["Space (rejected)"] = !HotkeyChord.TryParse("Space", out _)
            };
            bool hotkeysOk = hotkeys.Values.All(v => v);

            bool pass = createOk && reloadOk && brokenOk && partialOk && bomOk && hotkeysOk;
            return (new
            {
                scenario = "config",
                pass,
                testFolder = folder,
                create = new { ok = createOk, created = service.LastResult.Created, noBom = createdNoBom, missingFields },
                reload = new { ok = reloadOk, memoryTrimFloorMb = 64, defaultNexusMode = "Sprint", gameBoostFloor = GameBoost.MemoryTrimFloorMb, reload.Warnings },
                brokenJson = new
                {
                    ok = brokenOk,
                    exceptionEscaped = escaped,
                    loadOk = broken.Ok,
                    broken.Error,
                    broken.Restored,
                    backup = broken.BackupPath,
                    backupOk,
                    keptLastValid,
                    restoredFileMemoryTrimFloorMb = restoredFloor
                },
                emptyFields = new { ok = partialOk, partialLoad.Warnings, applied = new { service.Current.DispatchSlaCombatMs, service.Current.WatcherDebounceMs } },
                bomFile = new { ok = bomOk, bomLoad.Warnings },
                hotkeys,
                finalFingerprint = service.Fingerprint
            }, pass);
        }

        /// <summary>E3: ADR-19 state machine through the Cockpit command processor (real DB and qa_report.md).</summary>
        private static (object, bool) Adr19StateMachine(NexusRuntime runtime)
        {
            SqliteMemoryStore store = runtime.Store;
            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult dispatch = processor.Execute("Kiểm thử máy trạng thái ADR-19 sau ESCALATED (Gate 6 E3)", IntPtr.Zero);
            long id = dispatch.Dispatch?.DispatchId ?? 0;

            string marker = DispatchVerdictService.EscalationMarker(id);
            int Lines() => File.Exists(processor.QaReportPath)
                ? File.ReadAllLines(processor.QaReportPath, Encoding.UTF8).Count(l => l.StartsWith(marker, StringComparison.Ordinal))
                : 0;
            object Step(string label, CockpitCommandResult r)
            {
                RecentDispatch? row = store.GetDispatch(id);
                return new { step = label, ok = r.Ok, status = row?.Status, retryCount = row?.RetryCount, evidence = row?.EvidenceNotes, escalatedNow = r.Verdict?.EscalatedNow, escalationLines = Lines() };
            }

            var steps = new List<object>();
            CockpitCommandResult f1 = processor.Execute($"/fail #{id} Strike 1", IntPtr.Zero); steps.Add(Step("fail 1", f1));
            CockpitCommandResult f2 = processor.Execute($"/fail #{id} Strike 2", IntPtr.Zero); steps.Add(Step("fail 2", f2));
            CockpitCommandResult f3 = processor.Execute($"/fail #{id} Strike 3", IntPtr.Zero); steps.Add(Step("fail 3", f3));
            RecentDispatch? r3 = store.GetDispatch(id);
            int linesAfter3 = Lines();

            CockpitCommandResult f4 = processor.Execute($"/fail #{id} Strike 4 vẫn lỗi", IntPtr.Zero); steps.Add(Step("fail 4", f4));
            RecentDispatch? r4 = store.GetDispatch(id);
            int linesAfter4 = Lines();

            CockpitCommandResult pass1 = processor.Execute($"/pass #{id} Đã khắc phục xong", IntPtr.Zero); steps.Add(Step("pass", pass1));
            RecentDispatch? rp = store.GetDispatch(id);

            // Reopen a PASSED dispatch that already has >= 3 strikes -> ESCALATED again, still a single report line.
            CockpitCommandResult reopen = processor.Execute($"/fail #{id} Tái phát sau khi PASSED", IntPtr.Zero); steps.Add(Step("reopen (fail after pass)", reopen));
            RecentDispatch? rr = store.GetDispatch(id);
            int linesAfterReopen = Lines();

            bool spec3 = r3 != null && r3.Status == "ESCALATED" && r3.RetryCount == 3 && f3.Verdict?.EscalatedNow == true && linesAfter3 == 1;
            bool spec4 = r4 != null && r4.Status == "ESCALATED" && r4.RetryCount == 4 && r4.EvidenceNotes == "Strike 4 vẫn lỗi" &&
                         f4.Verdict?.EscalatedNow == false && linesAfter4 == 1;
            bool specPass = rp != null && rp.Status == "PASSED" && rp.RetryCount == 4 && rp.EvidenceNotes == "Đã khắc phục xong";
            bool reopenOk = rr != null && rr.Status == "ESCALATED" && rr.RetryCount == 5 && reopen.Verdict?.EscalatedNow == false && linesAfterReopen == 1;

            // Reopen below the strike limit: PASSED (1 strike) -> /fail -> NEEDS_WORK (2).
            CockpitCommandResult d2 = processor.Execute("Kiểm thử mở lại PASSED khi chưa đủ 3 lần (Gate 6 E3)", IntPtr.Zero);
            long id2 = d2.Dispatch?.DispatchId ?? 0;
            processor.Execute($"/fail #{id2} thiếu test", IntPtr.Zero);
            processor.Execute($"/pass #{id2} đã thêm test", IntPtr.Zero);
            processor.Execute($"/fail #{id2} test chập chờn", IntPtr.Zero);
            RecentDispatch? r2 = store.GetDispatch(id2);
            bool reopenBelowLimitOk = r2 != null && r2.Status == "NEEDS_WORK" && r2.RetryCount == 2;

            bool pass = dispatch.Ok && id > 0 && spec3 && spec4 && specPass && reopenOk && reopenBelowLimitOk;
            return (new
            {
                scenario = "adr19",
                pass,
                dispatchId = id,
                steps,
                checks = new { escalatedAt3 = spec3, fourthFailStaysEscalated = spec4, passFromEscalated = specPass, reopenPassedAt5 = reopenOk, reopenBelowLimit = reopenBelowLimitOk },
                escalationLinesForDispatch = linesAfterReopen,
                secondDispatch = new { dispatchId = id2, status = r2?.Status, retryCount = r2?.RetryCount },
                qaReportPath = processor.QaReportPath
            }, pass);
        }

        /// <summary>E1/E4: config + SLA profile as this process sees them (the script compares with McpTools get_nexus_config).</summary>
        private static (object, bool) Sla(NexusRuntime runtime)
        {
            NexusConfigService config = runtime.Config;
            DispatchSlaProfile sla = config.DetectSla();
            double expected = sla.MatchedProcesses.Count > 0 ? config.Current.DispatchSlaCombatMs : config.Current.DispatchSlaDesktopMs;
            bool pass = File.Exists(config.ConfigPath) && sla.BudgetMs == expected &&
                        (sla.Profile == "LiveCombat") == (sla.MatchedProcesses.Count > 0);
            return (new
            {
                scenario = "sla",
                pass,
                configPath = config.ConfigPath,
                configLoad = config.LastResult,
                fingerprint = config.Fingerprint,
                sla,
                config = config.Current
            }, pass);
        }

        // ───────────────────────────── TASK-07 ─────────────────────────────

        private const string TestApiKey = "AIzaSyTestKey_NEXUS_DPAPI_Verify_123456"; // 39 chars, never written to a report
        private const string AskQuestion = "Tối ưu C# SocketAsyncEventArgs chống phân mảnh bộ nhớ";

        private static (object, bool) Schema07(NexusRuntime runtime)
        {
            SchemaMigrationReport migration = runtime.Store.MigrationReport;
            var columns = new List<string>();
            long rows;
            using (SqliteConnection connection = runtime.Store.OpenHeldConnection())
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM pragma_table_info('agent_dispatch_logs') ORDER BY cid;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) columns.Add(reader.GetString(0));
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM agent_dispatch_logs;";
                    rows = Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            string[] required = { "ai_response", "ai_model", "ai_latency_ms", "retry_count", "evidence_notes", "updated_at_utc" };
            List<string> missing = required.Where(c => !columns.Contains(c)).ToList();
            bool pass = missing.Count == 0 && migration.Error == null && migration.RowsAfter == migration.RowsBefore;
            return (new
            {
                scenario = "schema07",
                pass,
                columns,
                missingColumns = missing,
                rows,
                migrationThisProcess = new { migration.AddedColumns, migration.RowsBefore, migration.RowsAfter, migration.Rebuilt, migration.Error }
            }, pass);
        }

        /// <summary>E2. The real vault file is backed up first and restored byte for byte at the end.</summary>
        private static (object, bool) Vault(NexusRuntime runtime)
        {
            DpapiSecretVault vault = runtime.Vault;
            byte[]? original = File.Exists(vault.VaultPath) ? File.ReadAllBytes(vault.VaultPath) : null;
            var processor = new CockpitCommandProcessor(runtime);
            try
            {
                // 1. Direct round trip.
                string? storeError = vault.Store(TestApiKey);
                bool fileExists = File.Exists(vault.VaultPath);
                byte[] onDisk = fileExists ? File.ReadAllBytes(vault.VaultPath) : Array.Empty<byte>();
                bool noPlainUtf8 = !Contains(onDisk, Encoding.UTF8.GetBytes("AIzaSyTestKey"));
                bool noPlainUtf16 = !Contains(onDisk, Encoding.Unicode.GetBytes("AIzaSyTestKey"));
                bool decrypted = vault.TryReadStored(out string? readBack, out string? readError) && readBack == TestApiKey;
                string mask = DpapiSecretVault.MaskApiKey(TestApiKey, ApiKeySource.Dpapi);
                bool maskOk = mask == "AIza...456 (39 chars, Source: DPAPI)";
                ApiKeyResolution resolved = vault.Resolve();
                bool resolvedFromDpapi = resolved.Source == ApiKeySource.Dpapi && resolved.Key == TestApiKey;

                // 2. Through the Cockpit commands.
                CockpitCommandResult set = processor.Execute($"/apikey set {TestApiKey}", IntPtr.Zero);
                CockpitCommandResult status = processor.Execute("/apikey status", IntPtr.Zero);
                CockpitCommandResult pasted = processor.Execute($"tối ưu Gemini với khóa {TestApiKey}", IntPtr.Zero);
                string echo = CockpitCommandProcessor.RedactForDisplay($"/apikey set {TestApiKey}");
                bool commandsOk = set.Ok && set.Message.Contains("AIza...456", StringComparison.Ordinal) &&
                                  status.Ok && status.Message.Contains("Configured via DPAPI", StringComparison.Ordinal) &&
                                  !pasted.Ok && pasted.Kind == "blocked" && pasted.Dispatch == null;
                bool messagesClean = !(set.Message + status.Message + pasted.Message + echo).Contains("AIzaSyTestKey", StringComparison.Ordinal);

                // 3. Clear.
                CockpitCommandResult clear = processor.Execute("/apikey clear", IntPtr.Zero);
                bool cleared = clear.Ok && !File.Exists(vault.VaultPath) && vault.Resolve().Source != ApiKeySource.Dpapi;

                bool pass = storeError == null && fileExists && noPlainUtf8 && noPlainUtf16 && decrypted && maskOk && resolvedFromDpapi &&
                            commandsOk && messagesClean && cleared;
                return (new
                {
                    scenario = "vault",
                    pass,
                    vaultPath = vault.VaultPath,
                    testKeyLength = TestApiKey.Length,
                    storeError,
                    fileExists,
                    blobBytes = onDisk.Length,
                    noPlaintextUtf8 = noPlainUtf8,
                    noPlaintextUtf16 = noPlainUtf16,
                    decryptedMatches = decrypted,
                    readError,
                    mask,
                    maskOk,
                    resolvedFromDpapi,
                    commands = new { setMessage = set.Message, statusMessage = status.Message, pastedKeyBlocked = pasted.Kind == "blocked", echo },
                    messagesClean,
                    clearMessage = clear.Message,
                    cleared,
                    realKeyExistedBefore = original != null
                }, pass);
            }
            finally
            {
                if (original != null) File.WriteAllBytes(vault.VaultPath, original); // restore the real key exactly
                else vault.Clear(out _);                                          // or leave no test key behind
            }
        }

        /// <summary>E3 with the deterministic mock (the script sets NEXUS_GEMINI_MOCK_RESPONSE for this process only).</summary>
        private static (object, bool) AskMock(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "ask", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} is not set: refusing to call the real API in this test." }, false);

            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult first = processor.Execute($"/ask {AskQuestion}", IntPtr.Zero);
            ConsultOutcome? outcome = first.AiTask != null && first.AiTask.Wait(TimeSpan.FromSeconds(10)) ? first.AiTask.Result : null;
            long id = first.Dispatch?.DispatchId ?? 0;
            RecentDispatch? row = id > 0 ? runtime.Store.GetDispatch(id) : null;
            LatestCheck latest = ReadLatest(runtime.LatestDispatchPath);
            string latestText = File.Exists(runtime.LatestDispatchPath) ? File.ReadAllText(runtime.LatestDispatchPath, Encoding.UTF8) : string.Empty;
            JsonNode? latestJson = latestText.Length > 0 ? JsonNode.Parse(latestText) : null;

            string primary = first.Dispatch?.PrimaryAgent.Name ?? "?";
            bool routed = first.Ok && first.Kind == "ask" && first.Dispatch?.SquadId == "Squad1" && first.Snapshot?.Event == "ask";
            bool aiOk = outcome != null && outcome.AiOk && outcome.AiModel == GeminiAgentClient.MockModel &&
                        outcome.AiResponse.Contains(primary, StringComparison.Ordinal) && outcome.AiResponse.Contains("Squad1", StringComparison.Ordinal);
            bool dbOk = row != null && row.Status == "DISPATCHED" && row.AiModel == GeminiAgentClient.MockModel &&
                        row.AiResponse == outcome?.AiResponse && row.AiLatencyMs >= 0;
            bool latestOk = latest.DispatchId == id && latest.NoBom &&
                            latestJson?["aiModel"]?.GetValue<string>() == GeminiAgentClient.MockModel &&
                            latestJson?["aiResponse"]?.GetValue<string>() == outcome?.AiResponse &&
                            latestJson?["aiLatencyMs"] != null && latestJson?["event"]?.GetValue<string>() == "ai_response";

            // Synchronous part of /ask (what blocks the Cockpit before focus goes back): 5 warm runs.
            var syncMs = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                CockpitCommandResult r = processor.Execute($"/ask {AskQuestion} (lần {i + 2})", IntPtr.Zero);
                syncMs.Add(r.ElapsedMs);
                r.AiTask?.Wait(TimeSpan.FromSeconds(10));
            }
            DispatchSlaProfile sla = runtime.Config.DetectSla();
            double budget = sla.Profile == "LiveCombat" ? sla.BudgetMs : 5.0;
            bool syncOk = syncMs.Max() < budget;

            bool pass = routed && aiOk && dbOk && latestOk && syncOk;
            return (new
            {
                scenario = "ask",
                pass,
                question = AskQuestion,
                dispatchId = id,
                squadId = first.Dispatch?.SquadId,
                primaryAgent = primary,
                firstSyncMs = first.ElapsedMs,
                routed,
                ai = outcome == null ? null : new { outcome.AiOk, outcome.AiModel, outcome.AiLatencyMs, outcome.KeySource, outcome.AiResponse },
                aiOk,
                db = row == null ? null : new { row.Status, row.AiModel, row.AiLatencyMs, aiResponseChars = row.AiResponse.Length },
                dbOk,
                latest = new { latest.DispatchId, latest.NoBom, aiModel = latestJson?["aiModel"]?.ToString(), aiLatencyMs = latestJson?["aiLatencyMs"]?.ToString(), evt = latestJson?["event"]?.ToString() },
                latestOk,
                warmSyncMs = syncMs,
                syncBudgetMs = budget,
                slaProfile = sla.Profile,
                syncOk
            }, pass);
        }

        /// <summary>E4: isolated empty vault + no environment key, mock off.</summary>
        private static (object, bool) AskMissingKey(NexusRuntime runtime)
        {
            if (GeminiAgentClient.MockEnabled)
                return (new { scenario = "ask-missingkey", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must not be set for this test." }, false);

            string emptyVault = Path.Combine(Path.GetTempPath(), "nexus_task07_missingkey", $"run-{DateTime.UtcNow:yyyyMMddHHmmssfff}", DpapiSecretVault.FileName);
            var consult = new AgentConsultService(runtime, new DpapiSecretVault(emptyVault, () => null));
            var processor = new CockpitCommandProcessor(runtime, consult);

            CockpitCommandResult status = processor.Execute("/apikey status", IntPtr.Zero);
            string? escaped = null;
            CockpitCommandResult? ask = null;
            ConsultOutcome? outcome = null;
            try
            {
                ask = processor.Execute($"/ask {AskQuestion} (không có khóa)", IntPtr.Zero);
                outcome = ask.AiTask != null && ask.AiTask.Wait(TimeSpan.FromSeconds(10)) ? ask.AiTask.Result : null;
            }
            catch (Exception ex)
            {
                escaped = ex.ToString();
            }
            long id = ask?.Dispatch?.DispatchId ?? 0;
            RecentDispatch? row = id > 0 ? runtime.Store.GetDispatch(id) : null;

            bool pass = escaped == null && ask != null && ask.Ok && row != null && row.Status == "DISPATCHED" &&
                        status.Message.Contains("Not Configured", StringComparison.Ordinal) &&
                        outcome != null && !outcome.AiOk && outcome.AiResponse == AgentConsultService.MissingKeyMessage &&
                        row.AiResponse == AgentConsultService.MissingKeyMessage;
            return (new
            {
                scenario = "ask-missingkey",
                pass,
                statusMessage = status.Message,
                exceptionEscaped = escaped,
                dispatchId = id,
                syncMs = ask?.ElapsedMs,
                status = row?.Status,
                aiResponse = row?.AiResponse,
                keySource = outcome?.KeySource
            }, pass);
        }

        /// <summary>E5: one real call when a key is configured (DPAPI or GEMINI_API_KEY); otherwise reported as skipped.</summary>
        private static (object, bool) AskLive(NexusRuntime runtime)
        {
            if (GeminiAgentClient.MockEnabled)
                return (new { scenario = "ask-live", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must not be set for the live test." }, false);
            ApiKeyResolution key = runtime.Vault.Resolve();
            if (key.Source == ApiKeySource.Missing)
                return (new { scenario = "ask-live", pass = true, skipped = true, reason = "No Gemini key configured (DPAPI / GEMINI_API_KEY).", keyError = key.Error }, true);

            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult ask = processor.Execute("/ask Trả lời trong 1 câu: NEXUS Home Hub đã kết nối Gemini thành công chưa?", IntPtr.Zero);
            int wait = runtime.Config.Current.GeminiTimeoutSeconds + 10;
            ConsultOutcome? outcome = ask.AiTask != null && ask.AiTask.Wait(TimeSpan.FromSeconds(wait)) ? ask.AiTask.Result : null;
            RecentDispatch? row = ask.Dispatch != null ? runtime.Store.GetDispatch(ask.Dispatch.DispatchId) : null;

            bool pass = ask.Ok && outcome != null && outcome.AiOk && row != null && row.AiResponse.Length > 0;
            return (new
            {
                scenario = "ask-live",
                pass,
                skipped = false,
                keySource = key.SourceLabel,
                dispatchId = ask.Dispatch?.DispatchId,
                squadId = ask.Dispatch?.SquadId,
                model = outcome?.AiModel,
                aiLatencyMs = outcome?.AiLatencyMs,
                aiOk = outcome?.AiOk,
                responsePreview = outcome == null ? null : (outcome.AiResponse.Length > 300 ? outcome.AiResponse.Substring(0, 300) + "…" : outcome.AiResponse)
            }, pass);
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
            }
            return false;
        }

        private static string? PickUnprotectedProcess()
        {
            int selfPid = Environment.ProcessId;
            int session;
            using (Process self = Process.GetCurrentProcess()) session = self.SessionId;

            var candidates = new List<(int Pid, string Name)>();
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (p.Id != selfPid && p.SessionId == session && !GameBoost.IsProtectedName(name) && !GameBoost.IsSystemName(name))
                            candidates.Add((p.Id, name));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // exited meanwhile / not queryable
                    }
                }
            }
            string[] preferred = { "powershell", "pwsh", "WindowsTerminal", "conhost" };
            foreach (string name in preferred)
            {
                if (candidates.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
            }
            return candidates.OrderBy(c => c.Pid).Select(c => c.Name).FirstOrDefault();
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

        /// <summary>Names of running game / anti-cheat processes (context for timing results: SGuard loads CPU and disk).</summary>
        private static List<string> RunningProtectedProcesses()
        {
            var names = new List<string>();
            foreach (Process proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (GameBoost.IsProtectedName(proc.ProcessName)) names.Add(proc.ProcessName);
                    }
                    catch (InvalidOperationException)
                    {
                        // exited meanwhile
                    }
                }
            }
            return names.Distinct().ToList();
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
