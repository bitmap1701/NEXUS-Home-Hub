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
    /// TASK-08 (Verify-Task08.ps1, writes task08_selftest_&lt;scenario&gt;.json):
    ///   schema08            E1: qa_response / qa_latency_ms columns, rows kept
    ///   ask-cold [nowarm]   E2: AskSyncBreakdown of Cold Call #1 + 10 calls (after the HostService warm-up; "nowarm" = reference)
    ///   autoqa              E3a: /ask --auto-qa with both mocks -> PASSED, ai_* and qa_* stored
    ///   qa-strikes          E3b: /qa x3 with the NEEDS_WORK mock -> r1, r2, ESCALATED r3, one escalation line
    ///   qa-missingkey       E3c: /qa without key -> [LOCAL QA SKIPPED], status and retry_count unchanged
    /// TASK-09 (Verify-Task09.ps1, writes task09_selftest_&lt;scenario&gt;.json):
    ///   schema09            E1: turn_count / turn_history_json columns, rows kept, every row has valid turn data
    ///   retry-loop          E2: /ask -> /qa NEEDS_WORK -> /retry --auto-qa -> PASSED, r1 kept, turn 2 (QA mock switched in-process)
    ///   retry-breaker       E3: ESCALATED r3 -> /retry refused, nothing changed -> /retry --force --auto-qa -> PASSED r3
    ///   followup-chain      E4: /ask -> /f (turn 2) + offline maxConversationTurns window checks; MCP turn 3 by the script
    ///   live-e2e            E5: real /ask -> /f -> /qa when a key is configured (skipped otherwise)
    ///   apikey-status       tools/Set-NexusGeminiKey.ps1: which key source resolves (masked)
    /// TASK-14 ADR-59 (Verify-Task14.ps1, writes task14_selftest_&lt;scenario&gt;.json; 0 tokens, no Gemini call except E4 /ground mock):
    ///   schema14            E1: &gt;= 3 001 rows kept, quick_notes.md without BOM, Overwolf not in the default combatProcessPrefixes
    ///   local-calc          E2: /calc cases, Hex/Bin format, [CALC ERROR] guard, 30-sample P50/P95, token counters unchanged
    ///   local-timer         E3: duration parser, /timer 250ms -> ACTIVE -> EXPIRED (&lt;= 400 ms), cancel all, 0 timer objects, idle CPU
    ///   local-note          E4: /note -> list -> find, Secret Guard, /ground (mock) sees the note at once, test notes removed
    /// TASK-13a (Verify-Task13a.ps1, writes task13a_selftest_mem-profile.json):
    ///   mem-profile         memory breakdown before / after a mock load and one idle trim; Private WS without PerformanceCounter
    /// TASK-13 (Verify-Task13.ps1, writes task13_selftest_&lt;scenario&gt;.json):
    ///   widgets             E2: 2 code blocks + 1 checklist extracted, /copy 1 / 2 exact, 30 x /copy P50/P95, extractedCards, no compiled Regex
    ///   mfd-pins            E3: /unpin all, /pin #1, /pin text, /pins, FIFO cap, Secret Guard, user's mfd_pins.json restored
    /// TASK-12 ADR-61 (Verify-Task12.ps1, writes task12_selftest_&lt;scenario&gt;.json; mock Gemini, 0 live tokens):
    ///   vision              E1/E2/E3: MFD preview, Win+V opt-out, 2K PNG → JPEG &lt;= 256 000 B, /img --file mock echo, 25 cache hits, misses
    ///   vision-guard        E4: Combat Shield &lt; 2 ms (no capture), Privacy Guard, keyword matching, safe /snap (one capture)
    /// TASK-16 ADR-65 (Verify-Task16.ps1, writes task16_selftest_ui-v2.json; headless, loopback only, temp EQ folder):
    ///   ui-v2               E3/E4: snap 40/330/640, /ui parser, cockpit_ui.json, K450 EQ switcher, simulated G272 YouTube, watch webhook, B1/B5
    /// TASK-16b ADR-66 (Verify-Task16b.ps1, writes task16b_selftest_ui-v2b.json; temp folders + temp SQLite only):
    ///   ui-v2b              E2/E3: markdown counts + edge cases + 4 KB &lt; 1 ms, answer history (is_test = 0 only) &lt; 2 ms, AllianceStatus steps, shift state
    /// TASK-15 ADR-62 (Verify-Task15.ps1, writes task15_selftest_ctx-safeguard.json; simulated windows, mock Gemini, is_test rows):
    ///   ctx-safeguard       E1-E4: anti-cheat / denylist rules, 10+10 blocks P95 &lt;= 0,5 ms with no leak and 0 tokens, /ctx cache hits, history clean
    /// TASK-16c ADR-68 (Verify-Task16c.ps1, writes task16c_selftest_ui-v2-visual.json; mock Gemini, is_test rows, window never shown):
    ///   ui-v2-visual        E2/E3: / catalogue + filter, @ agent filter, @agent dispatch, Hủy cancels a 3 s mock /ask!, window self-check
    /// TASK-17 ADR-67 (Verify-Task17.ps1, writes task17_selftest_net-ptt.json; no live call, no microphone opened):
    ///   net-ptt             E2 /net port + radar on an ephemeral listener, 200 ms ping cut, NITRO5 away / home; E3 FIFINE selector;
    ///                       E4 VAD on synthetic PCM + buffers freed; decision 3.7 clipboard worker vs a hung owner
    /// TASK-17a ADR-69 (Verify-Task17a.ps1, writes task17a_selftest_net-targets.json; temp nexus_config.json only):
    ///   net-targets         E2 custom networkTargets + Reload, max 8; E3 missing / [] / broken item / broken file → 4 defaults
    ///   vision-mem          TASK-12b E2/E5: settled median before / after a 2K vision load, plateau (no growth per image), 100 ms burst peak, LOH compaction
    /// </summary>
    internal static partial class SelfTest
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
            // TASK-12c (B1): the user's latest_dispatch.json is put back when the scenario ends, so the real Cockpit shows the
            // user's own last dispatch again instead of a NEEDS_WORK / ESCALATED test row.
            byte[]? userLatest = null;
            try { if (File.Exists(runtime.LatestDispatchPath)) userLatest = File.ReadAllBytes(runtime.LatestDispatchPath); }
            catch (IOException) { userLatest = null; }
            try
            {
                if (name is "ask" or "ask-missingkey" or "ask-live" or "ask-cold" or "autoqa" or "qa-strikes" or "qa-missingkey" or "retry-loop" or "retry-breaker" or "followup-chain" or "live-e2e" or "token-accum" or "backoff-retry" or "quota-guard" or "live-e2e10") { runtime.ResponseCache.Clear(); }
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
                    "schema08" => Schema08(runtime),
                    "ask-cold" => AskCold(runtime, warm: !string.Equals(argument, "nowarm", StringComparison.OrdinalIgnoreCase)),
                    "autoqa" => AutoQa(runtime),
                    "qa-strikes" => QaStrikes(runtime),
                    "qa-missingkey" => QaMissingKey(runtime),
                    "schema09" => Schema09(runtime),
                    "retry-loop" => RetryLoop(runtime),
                    "retry-breaker" => RetryBreaker(runtime),
                    "followup-chain" => FollowUpChain(runtime),
                    "live-e2e" => LiveE2e(runtime),
                    "apikey-status" => ApiKeyStatus(runtime),
                    "schema10" => Schema10(runtime),
                    "token-accum" => TokenAccum(runtime),
                    "backoff-retry" => BackoffRetry(runtime),
                    "quota-guard" => QuotaGuardTest(runtime),
                    "live-e2e10" => LiveE2e10(runtime),
                    "schema11" => Schema11(runtime),
                    "cache-hit-bypass" => CacheHitAndBypass(runtime),
                    "vault-grounding" => VaultGroundingTest(runtime),
                    "cache-commands" => CacheCommandsTest(runtime),
                    "live-e2e11" => LiveE2e11(runtime),
                    "cache-hit-profile" => CacheHitProfile(runtime),
                    "schema14" => Schema14(runtime),
                    "local-calc" => LocalCalc(runtime),
                    "local-timer" => LocalTimer(runtime),
                    "local-note" => LocalNote(runtime),
                    "mem-profile" => MemProfile(runtime),
                    "widgets" => Widgets(runtime),
                    "mfd-pins" => MfdPins(runtime),
                    "vision" => Vision(runtime),
                    "vision-guard" => VisionGuard(runtime),
                    "vision-mem" => VisionMem(runtime),
                    "ui-v2" => UiV2(runtime),
                    "ui-v2b" => UiV2b(runtime),
                    "ctx-safeguard" => CtxSafeguard(runtime),
                    "ui-v2-visual" => UiV2Visual(runtime),
                    "ui-footprint" => UiFootprint(runtime, argument),
                    "net-ptt" => NetPtt(runtime),
                    "net-targets" => NetTargets(runtime),
                    "host-footprint" => HostFootprint(runtime),
                    "ptt-stt" => PttStt(runtime),
                    "stt-worker-e2e" => SttWorkerE2e(runtime),
                    _ => ((object)new { scenario = name, pass = false, error = "Unknown scenario. Use dispatch | boost | boost-live | wal-stress | schema | devqa-pass | devqa-strikes | boost-acedenied | config | adr19 | sla | schema07 | vault | ask | ask-missingkey | ask-live | schema08 | ask-cold | autoqa | qa-strikes | qa-missingkey | schema09 | retry-loop | retry-breaker | followup-chain | live-e2e | apikey-status | schema10 | token-accum | backoff-retry | quota-guard | live-e2e10 | schema11 | cache-hit-bypass | vault-grounding | cache-commands | live-e2e11 | cache-hit-profile | schema14 | local-calc | local-timer | local-note | mem-profile | widgets | mfd-pins | vision | vision-guard | vision-mem | ui-v2 | ui-v2b | ctx-safeguard | ui-v2-visual | ui-footprint | net-ptt | net-targets | host-footprint | ptt-stt | stt-worker-e2e." }, false)
                };
            }
            catch (Exception ex)
            {
                report = new { scenario = name, pass = false, exception = ex.ToString() };
                pass = false;
            }
            finally
            {
                // Verify-Task13 E2 reads the widgets test answer on the live HostService right after this selftest, so it sets
                // NEXUS_SELFTEST_KEEP_LATEST=1 for that one run and restores the user's file itself at the end.
                if (userLatest != null && Environment.GetEnvironmentVariable("NEXUS_SELFTEST_KEEP_LATEST") != "1")
                {
                    string? restoreError = AtomicFile.WriteAllBytes(runtime.LatestDispatchPath, userLatest);
                    if (restoreError != null) HostLog.Write($"[SELFTEST] latest_dispatch.json not restored: {restoreError}");
                }
            }

            string prefix = name switch
            {
                "schema" or "devqa-pass" or "devqa-strikes" or "boost-acedenied" => "task05",
                "config" or "adr19" or "sla" => "task06",
                "schema07" or "vault" or "ask" or "ask-missingkey" or "ask-live" => "task07",
                "schema08" or "ask-cold" or "autoqa" or "qa-strikes" or "qa-missingkey" => "task08",
                "schema09" or "retry-loop" or "retry-breaker" or "followup-chain" or "live-e2e" or "apikey-status" => "task09",
                "schema10" or "token-accum" or "backoff-retry" or "quota-guard" or "live-e2e10" => "task10",
                "schema11" or "cache-hit-bypass" or "vault-grounding" or "cache-commands" or "live-e2e11" or "cache-hit-profile" => "task11",
                "schema14" or "local-calc" or "local-timer" or "local-note" => "task14",
                "mem-profile" => "task13a",
                "widgets" or "mfd-pins" => "task13",
                "vision" or "vision-guard" or "vision-mem" => "task12",
                "ui-v2" => "task16",
                "ui-v2b" => "task16b",
                "ctx-safeguard" => "task15",
                "ui-v2-visual" or "ui-footprint" => "task16c",
                "net-ptt" => "task17",
                "net-targets" => "task17a",
                "host-footprint" => "task17b",
                "ptt-stt" => "task18",
                "stt-worker-e2e" => "task18b",
                _ => "task04"
            };
            string suffix = name == "ask-cold" && string.Equals(argument, "nowarm", StringComparison.OrdinalIgnoreCase) ? "-nowarm"
                : name == "ui-footprint" && !string.IsNullOrWhiteSpace(argument) ? "-" + argument.Trim().ToLowerInvariant() : string.Empty;
            string path = Path.Combine(handoff, $"{prefix}_selftest_{name}{suffix}.json");
            // TASK-14b (0b): every report says whether micro-latency SLAs were relaxed and which ones only passed with a warning.
            System.Text.Json.Nodes.JsonNode? node = JsonSerializer.SerializeToNode(report, Json);
            if (node is System.Text.Json.Nodes.JsonObject reportObject)
            {
                reportObject["relaxedSla"] = RelaxedSla;
                reportObject["slaWarnings"] = JsonSerializer.SerializeToNode(SlaWarnings, Json);
            }
            File.WriteAllText(path, node?.ToJsonString(Json) ?? JsonSerializer.Serialize(report, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

        // ───────────────────────────── TASK-08 ─────────────────────────────

        private static (object, bool) Schema08(NexusRuntime runtime)
        {
            SchemaMigrationReport migration = runtime.Store.MigrationReport;
            var columns = new List<string>();
            using (SqliteConnection connection = runtime.Store.OpenHeldConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info('agent_dispatch_logs') ORDER BY cid;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(0));
            }
            string[] required = { "qa_response", "qa_latency_ms", "ai_response", "ai_model", "ai_latency_ms", "retry_count", "evidence_notes", "updated_at_utc" };
            List<string> missing = required.Where(c => !columns.Contains(c)).ToList();
            bool pass = missing.Count == 0 && migration.Error == null && migration.RowsAfter == migration.RowsBefore;
            return (new
            {
                scenario = "schema08",
                pass,
                columns,
                missingColumns = missing,
                rows = runtime.Store.CountDispatchLogs(),
                migrationThisProcess = new { migration.AddedColumns, migration.RowsBefore, migration.RowsAfter, migration.Rebuilt, migration.Error }
            }, pass);
        }

        /// <summary>E2 (ADR-28). Mock only: 11 /ask calls must never reach the real API.</summary>
        private static (object, bool) AskCold(NexusRuntime runtime, bool warm)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "ask-cold", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} is not set: refusing 11 real API calls." }, false);

            WarmUpReport? warmUp = warm ? runtime.WarmUp() : null; // what HostService does at startup
            var processor = new CockpitCommandProcessor(runtime);

            CockpitCommandResult cold = processor.Execute("/ask Tối ưu C# SocketAsyncEventArgs chống phân mảnh bộ nhớ (cold #1)", IntPtr.Zero);
            cold.AiTask?.Wait(TimeSpan.FromSeconds(10));
            runtime.QuotaGuard.ResetSlidingWindows();
            var runs = new List<AskSyncBreakdown>();
            for (int i = 0; i < 10; i++)
            {
                CockpitCommandResult r = processor.Execute($"/ask Tối ưu C# SocketAsyncEventArgs chống phân mảnh bộ nhớ (lần {i + 2})", IntPtr.Zero);
                if (r.SyncTiming != null) runs.Add(r.SyncTiming);
                r.AiTask?.Wait(TimeSpan.FromSeconds(10));
            }

            DispatchSlaProfile sla = runtime.Config.DetectSla();
            bool combat = sla.Profile == "LiveCombat";
            double coldBudget = combat ? 40 : 25;
            double p95Budget = combat ? 25 : 10;
            double[] totals = runs.Select(r => r.TotalSyncMs).OrderBy(x => x).ToArray();
            double p95 = totals.Length == 0 ? double.MaxValue : totals[(int)Math.Ceiling(0.95 * totals.Length) - 1];
            AskSyncBreakdown? c = cold.SyncTiming;
            bool httpCreated = GeminiAgentClient.HttpClientCreated;
            bool withinBudget = cold.Ok && c != null && c.TotalSyncMs < coldBudget && runs.Count == 10 && p95 < p95Budget;
            bool pass = warm ? withinBudget && !httpCreated : cold.Ok && runs.Count == 10; // "nowarm" is a reference measurement

            return (new
            {
                scenario = "ask-cold",
                pass,
                warmUp = warm,
                warmUpReport = warmUp,
                coldCall1 = c,
                coldBudgetMs = coldBudget,
                p95TotalSyncMs = Math.Round(p95, 3),
                p95BudgetMs = p95Budget,
                maxOfRuns = runs.Count == 0 ? null : new
                {
                    guardMs = runs.Max(r => r.GuardMs),
                    dispatchAndDbMs = runs.Max(r => r.DispatchAndDbMs),
                    dispatchMs = runs.Max(r => r.DispatchMs),
                    latestFileMs = runs.Max(r => r.LatestFileMs),
                    taskSpawnMs = runs.Max(r => r.TaskSpawnMs),
                    totalSyncMs = runs.Max(r => r.TotalSyncMs)
                },
                runs,
                withinBudget,
                slaProfile = sla.Profile,
                httpClientCreatedInProcess = httpCreated
            }, pass);
        }

        /// <summary>E3a: /ask --auto-qa with NEXUS_GEMINI_MOCK_RESPONSE + NEXUS_GEMINI_QA_MOCK_VERDICT=PASSED:...</summary>
        private static (object, bool) AutoQa(NexusRuntime runtime)
        {
            string? qaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
            if (!GeminiAgentClient.MockEnabled || string.IsNullOrEmpty(qaMock))
                return (new { scenario = "autoqa", pass = false, error = "Both mock variables must be set." }, false);

            runtime.WarmUp(); // as HostService does at startup
            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult ask = processor.Execute("/ask --auto-qa Thiết kế gói tin UDP có seqId", IntPtr.Zero);
            QaOutcome? qa = ask.QaTask != null && ask.QaTask.Wait(TimeSpan.FromSeconds(20)) ? ask.QaTask.Result : null;
            long id = ask.Dispatch?.DispatchId ?? 0;
            RecentDispatch? row = id > 0 ? runtime.Store.GetDispatch(id) : null;
            LatestCheck latest = ReadLatest(runtime.LatestDispatchPath);
            JsonNode? latestJson = File.Exists(runtime.LatestDispatchPath) ? JsonNode.Parse(File.ReadAllText(runtime.LatestDispatchPath, Encoding.UTF8)) : null;

            bool dbOk = row != null && row.Status == "PASSED" && row.RetryCount == 0 && row.AiResponse.Length > 0 &&
                        row.AiModel == GeminiAgentClient.MockModel && row.QaResponse.StartsWith("VERDICT: PASSED", StringComparison.Ordinal) &&
                        row.EvidenceNotes.StartsWith("QA ", StringComparison.Ordinal);
            bool latestOk = latest.DispatchId == id && latest.Status == "PASSED" &&
                            (latestJson?["qaResponse"]?.GetValue<string>() ?? string.Empty).Length > 0 &&
                            (latestJson?["aiResponse"]?.GetValue<string>() ?? string.Empty).Length > 0 && latestJson?["qaLatencyMs"] != null;
            bool pass = ask.Ok && ask.QaTask != null && qa != null && qa.Reviewed && qa.Verdict == "PASSED" && dbOk && latestOk;
            return (new
            {
                scenario = "autoqa",
                pass,
                command = "/ask --auto-qa Thiết kế gói tin UDP có seqId",
                dispatchId = id,
                squadId = ask.Dispatch?.SquadId,
                qaAgent = qa?.QaAgent,
                verdict = qa?.Verdict,
                db = row == null ? null : new { row.Status, row.RetryCount, row.AiModel, aiChars = row.AiResponse.Length, row.QaResponse, row.QaLatencyMs, row.EvidenceNotes },
                dbOk,
                latest = new { latest.DispatchId, latest.Status, qaChars = (latestJson?["qaResponse"]?.ToString() ?? string.Empty).Length },
                latestOk,
                syncTiming = ask.SyncTiming
            }, pass);
        }

        /// <summary>E3b: three /qa reviews that all say NEEDS_WORK (mock) on a fresh dispatch.</summary>
        private static (object, bool) QaStrikes(NexusRuntime runtime)
        {
            string? qaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
            if (string.IsNullOrEmpty(qaMock) || !qaMock.TrimStart().StartsWith("NEEDS_WORK", StringComparison.OrdinalIgnoreCase))
                return (new { scenario = "qa-strikes", pass = false, error = $"{GeminiAgentClient.QaMockEnvironmentVariable} must start with NEEDS_WORK." }, false);

            runtime.WarmUp(); // as HostService does at startup
            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult dispatch = processor.Execute("Kiểm thử QA 3 lần trượt: rò rỉ socket UDP (Gate 8 E3b)", IntPtr.Zero);
            long id = dispatch.Dispatch?.DispatchId ?? 0;
            string marker = DispatchVerdictService.EscalationMarker(id);
            var steps = new List<object>();
            var observed = new List<(string Status, int Retry, bool Escalated)>();
            var syncMs = new List<double>();
            for (int i = 1; i <= 3; i++)
            {
                CockpitCommandResult r = processor.Execute($"/qa #{id} Lần {i}: socket vẫn rò rỉ sau 10 phút tải", IntPtr.Zero);
                QaOutcome? o = r.QaTask != null && r.QaTask.Wait(TimeSpan.FromSeconds(20)) ? r.QaTask.Result : null;
                RecentDispatch? row = runtime.Store.GetDispatch(id);
                observed.Add((row?.Status ?? "?", row?.RetryCount ?? -1, o?.EscalatedNow == true));
                if (r.SyncTiming != null) syncMs.Add(r.SyncTiming.TotalSyncMs);
                steps.Add(new { qa = i, ok = r.Ok, verdict = o?.Verdict, status = row?.Status, retryCount = row?.RetryCount, escalatedNow = o?.EscalatedNow, sync = r.SyncTiming, evidence = row?.EvidenceNotes });
            }
            int lines = File.Exists(processor.QaReportPath)
                ? File.ReadAllLines(processor.QaReportPath, Encoding.UTF8).Count(l => l.StartsWith(marker, StringComparison.Ordinal))
                : 0;
            DispatchSlaProfile sla = runtime.Config.DetectSla();
            double syncBudget = sla.Profile == "LiveCombat" ? 25 : 10;
            bool strikesOk = observed.Count == 3 && observed[0] == ("NEEDS_WORK", 1, false) && observed[1] == ("NEEDS_WORK", 2, false) &&
                             observed[2] == ("ESCALATED", 3, true) && lines == 1;
            bool syncOk = syncMs.Count == 3 && syncMs.Max() < syncBudget;
            bool pass = dispatch.Ok && id > 0 && strikesOk && syncOk;
            return (new
            {
                scenario = "qa-strikes",
                pass,
                dispatchId = id,
                squadId = dispatch.Dispatch?.SquadId,
                steps,
                strikesOk,
                escalationLines = lines,
                qaSyncMs = syncMs,
                qaSyncBudgetMs = syncBudget,
                syncOk,
                qaReportPath = processor.QaReportPath
            }, pass);
        }

        /// <summary>E3c: no key, no QA mock -> qa_response explains, status / retry_count untouched (never a strike).</summary>
        private static (object, bool) QaMissingKey(NexusRuntime runtime)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable)))
                return (new { scenario = "qa-missingkey", pass = false, error = $"{GeminiAgentClient.QaMockEnvironmentVariable} must not be set." }, false);

            string emptyVault = Path.Combine(Path.GetTempPath(), "nexus_task08_qa_missingkey", $"run-{DateTime.UtcNow:yyyyMMddHHmmssfff}", DpapiSecretVault.FileName);
            var consult = new AgentConsultService(runtime, new DpapiSecretVault(emptyVault, () => null));
            var processor = new CockpitCommandProcessor(runtime, consult);
            CockpitCommandResult dispatch = processor.Execute("Kiểm thử /qa khi chưa có khóa (Gate 8 E3c)", IntPtr.Zero);
            long id = dispatch.Dispatch?.DispatchId ?? 0;
            CockpitCommandResult r = processor.Execute($"/qa #{id} có log build", IntPtr.Zero);
            QaOutcome? o = r.QaTask != null && r.QaTask.Wait(TimeSpan.FromSeconds(10)) ? r.QaTask.Result : null;
            RecentDispatch? row = id > 0 ? runtime.Store.GetDispatch(id) : null;
            bool pass = dispatch.Ok && o != null && !o.Reviewed && row != null && row.Status == "DISPATCHED" && row.RetryCount == 0 &&
                        row.QaResponse == AgentConsultService.QaSkippedNoKey;
            return (new { scenario = "qa-missingkey", pass, dispatchId = id, status = row?.Status, retryCount = row?.RetryCount, qaResponse = row?.QaResponse, keySource = o?.KeySource }, pass);
        }

        // ───────────────────────────── TASK-09 ─────────────────────────────

        private const string E2NeedsWork = "NEEDS_WORK: Thiếu xử lý đóng kết nối Socket";
        private const string E2Passed = "PASSED: Bản sửa lỗi đã đóng Socket an toàn";
        private const string E2Hint = "Đã bổ sung khối using cho Socket";
        private const string E3NeedsWork = "NEEDS_WORK: Socket vẫn rò rỉ sau 10 phút tải";
        private const string E3Passed = "PASSED: Đã đóng Socket bằng using, hết rò rỉ";
        private const string E4FollowUp = "Thêm cơ chế chống trượt gói tin";

        private static T? Wait<T>(System.Threading.Tasks.Task<T>? task, int seconds) where T : class =>
            task != null && task.Wait(TimeSpan.FromSeconds(seconds)) ? task.Result : null;

        private static void SetQaMock(string? value) => Environment.SetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable, value);

        private static IReadOnlyList<string> Roles(RecentDispatch? row) =>
            row == null ? Array.Empty<string>() : TurnHistory.Parse(row.TurnHistoryJson).Select(e => $"{e.Turn}:{e.Role}").ToList();

        private static double SyncBudget(NexusRuntime runtime) => runtime.Config.DetectSla().Profile == "LiveCombat" ? 25 : 10;

        private static int LatestTurnCount(string path)
        {
            try
            {
                return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?["turnCount"]?.GetValue<int>() ?? -1 : -1;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException)
            {
                return -1;
            }
        }

        private static long CountAudit(NexusRuntime runtime, string tool, long dispatchId)
        {
            using SqliteConnection connection = runtime.Store.OpenHeldConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_logs WHERE tool_name = $t AND arguments_json LIKE $p;";
            cmd.Parameters.AddWithValue("$t", tool);
            cmd.Parameters.AddWithValue("$p", $"%\"dispatchId\":{dispatchId},%");
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        private static object Brief(RecentDispatch? row) => row == null ? new { missing = true } : new
        {
            row.Id, row.Status, row.RetryCount, row.TurnCount, roles = Roles(row), row.AiModel,
            aiResponse = LatestDispatchFile.Shorten(row.AiResponse, 600), row.QaResponse, row.EvidenceNotes, row.UpdatedAtUtc
        };

        private static (object, bool) Schema09(NexusRuntime runtime)
        {
            SchemaMigrationReport migration = runtime.Store.MigrationReport;
            var columns = new List<string>();
            long nullOrBadHistory;
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
                    cmd.CommandText = "SELECT COUNT(*) FROM agent_dispatch_logs WHERE turn_count < 1 OR turn_history_json IS NULL OR json_valid(turn_history_json) = 0;";
                    nullOrBadHistory = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
            }
            string[] required = { "turn_count", "turn_history_json", "qa_response", "qa_latency_ms", "ai_response", "ai_model", "ai_latency_ms", "retry_count", "evidence_notes", "updated_at_utc" };
            List<string> missing = required.Where(c => !columns.Contains(c)).ToList();
            bool pass = missing.Count == 0 && migration.Error == null && migration.RowsAfter == migration.RowsBefore && nullOrBadHistory == 0;
            return (new
            {
                scenario = "schema09",
                pass,
                columns,
                missingColumns = missing,
                rows = runtime.Store.CountDispatchLogs(),
                rowsWithInvalidTurnData = nullOrBadHistory,
                maxConversationTurns = runtime.Config.Current.MaxConversationTurns,
                migrationThisProcess = new { migration.AddedColumns, migration.RowsBefore, migration.RowsAfter, migration.Rebuilt, migration.Error }
            }, pass);
        }

        /// <summary>E2 (ADR-36): /ask -> /qa NEEDS_WORK (r1) -> "/retry --auto-qa hint" -> PASSED, r1 kept, turn 2.</summary>
        private static (object, bool) RetryLoop(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "retry-loop", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);
            string? previousQaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
            try
            {
                runtime.WarmUp(); // as HostService does at startup
                var processor = new CockpitCommandProcessor(runtime);
                CockpitCommandResult ask = processor.Execute("/ask Viết C# UDP client gửi gói tin có seqId (Gate 9 E2)", IntPtr.Zero);
                ConsultOutcome? answer = Wait(ask.AiTask, 10);
                long id = ask.Dispatch?.DispatchId ?? 0;

                SetQaMock(E2NeedsWork);
                CockpitCommandResult qa = processor.Execute($"/qa #{id}", IntPtr.Zero);
                QaOutcome? firstReview = Wait(qa.QaTask, 20);
                RecentDispatch? afterQa = runtime.Store.GetDispatch(id);

                SetQaMock(E2Passed);
                string command = $"/retry --auto-qa {E2Hint}"; // spec E2 command: newest dispatch
                CockpitCommandResult retry = processor.Execute(command, IntPtr.Zero);
                RefineOutcome? refined = Wait(retry.RefineTask, 20);
                QaOutcome? secondReview = Wait(retry.QaTask, 20);
                RecentDispatch? final = runtime.Store.GetDispatch(id);

                string ai = final?.AiResponse ?? string.Empty;
                string[] expectedRoles = { "1:user", "1:model", "1:qa", "2:user", "2:model", "2:qa" };
                double budget = SyncBudget(runtime);
                var checks = new
                {
                    askOk = ask.Ok && answer?.AiOk == true,
                    firstQaNeedsWorkR1 = afterQa != null && afterQa.Status == "NEEDS_WORK" && afterQa.RetryCount == 1 && afterQa.TurnCount == 1,
                    retryTargetsNewest = retry.Ok && retry.Snapshot?.DispatchId == id && refined?.AiOk == true,
                    mockMarker = ai.StartsWith("[MOCK-RETRY-TURN-2]", StringComparison.Ordinal),
                    qaCritiqueReachedPrimary = ai.Contains("Thiếu xử lý đóng kết nối Socket", StringComparison.Ordinal),
                    hintReachedPrimary = ai.Contains(E2Hint, StringComparison.Ordinal),
                    previousAnswerReachedPrimary = ai.Contains("prev_answer=\"[MOCK GEMINI]", StringComparison.Ordinal),
                    autoQaPassed = secondReview?.Reviewed == true && secondReview.Verdict == "PASSED",
                    finalPassedR1Turn2 = final != null && final.Status == "PASSED" && final.RetryCount == 1 && final.TurnCount == 2,
                    historyOk = Roles(final).SequenceEqual(expectedRoles),
                    latestDispatchTurn2 = LatestTurnCount(runtime.LatestDispatchPath) == 2,
                    syncWithinBudget = retry.SyncTiming != null && retry.SyncTiming.TotalSyncMs < budget
                };
                bool pass = checks.askOk && checks.firstQaNeedsWorkR1 && checks.retryTargetsNewest && checks.mockMarker &&
                            checks.qaCritiqueReachedPrimary && checks.hintReachedPrimary && checks.previousAnswerReachedPrimary &&
                            checks.autoQaPassed && checks.finalPassedR1Turn2 && checks.historyOk && checks.latestDispatchTurn2 && checks.syncWithinBudget;
                return (new
                {
                    scenario = "retry-loop",
                    pass,
                    dispatchId = id,
                    qaMocks = new[] { E2NeedsWork, E2Passed },
                    command,
                    checks,
                    retrySync = retry.SyncTiming,
                    syncBudgetMs = budget,
                    afterFirstQa = Brief(afterQa),
                    final = Brief(final),
                    firstVerdict = firstReview?.Verdict,
                    secondVerdict = secondReview?.Verdict,
                    retryMessage = retry.Message
                }, pass);
            }
            finally
            {
                SetQaMock(previousQaMock);
            }
        }

        /// <summary>E3 (ADR-36): ESCALATED r3 -> /retry refused (nothing changes) -> "/retry --force --auto-qa" -> PASSED r3.</summary>
        private static (object, bool) RetryBreaker(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "retry-breaker", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);
            string? previousQaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
            try
            {
                runtime.WarmUp();
                var processor = new CockpitCommandProcessor(runtime);
                CockpitCommandResult ask = processor.Execute("/ask Tối ưu bộ đệm Socket UDP khi Delta Force mở (Gate 9 E3)", IntPtr.Zero);
                Wait(ask.AiTask, 10);
                long id = ask.Dispatch?.DispatchId ?? 0;

                SetQaMock(E3NeedsWork);
                var strikes = new List<string>();
                for (int i = 1; i <= 3; i++)
                {
                    CockpitCommandResult r = processor.Execute($"/qa #{id} lần {i}", IntPtr.Zero);
                    Wait(r.QaTask, 20);
                    RecentDispatch? row = runtime.Store.GetDispatch(id);
                    strikes.Add($"{row?.Status}/r{row?.RetryCount}");
                }
                RecentDispatch? escalated = runtime.Store.GetDispatch(id);
                long blockedAuditBefore = CountAudit(runtime, "gemini_retry_blocked", id);
                long retryAuditBefore = CountAudit(runtime, "gemini_retry", id);

                CockpitCommandResult blocked = processor.Execute("/retry Thử sửa lại lần nữa", IntPtr.Zero);
                RecentDispatch? afterBlocked = runtime.Store.GetDispatch(id);
                long blockedAuditAfter = CountAudit(runtime, "gemini_retry_blocked", id);
                long retryAuditAfterBlocked = CountAudit(runtime, "gemini_retry", id);

                SetQaMock(E3Passed);
                CockpitCommandResult forced = processor.Execute("/retry --force --auto-qa", IntPtr.Zero);
                RefineOutcome? refined = Wait(forced.RefineTask, 20);
                QaOutcome? review = Wait(forced.QaTask, 20);
                RecentDispatch? final = runtime.Store.GetDispatch(id);
                string marker = DispatchVerdictService.EscalationMarker(id);
                int lines = File.Exists(processor.QaReportPath)
                    ? File.ReadAllLines(processor.QaReportPath, Encoding.UTF8).Count(l => l.StartsWith(marker, StringComparison.Ordinal))
                    : 0;
                double budget = SyncBudget(runtime);

                var checks = new
                {
                    escalatedR3 = escalated != null && escalated.Status == "ESCALATED" && escalated.RetryCount == 3,
                    blockedMessage = !blocked.Ok && blocked.Message == AgentConsultService.CircuitBreakerMessage(id),
                    noGeminiCall = blocked.RefineTask == null && blocked.QaTask == null && retryAuditAfterBlocked == retryAuditBefore,
                    rowUnchanged = escalated != null && escalated == afterBlocked, // record equality: every column incl. updated_at_utc
                    blockedAudited = blockedAuditAfter == blockedAuditBefore + 1,
                    forcedRefined = forced.Ok && refined?.AiOk == true && (final?.AiResponse ?? string.Empty).StartsWith($"[MOCK-RETRY-TURN-{(escalated?.TurnCount ?? 0) + 1}]", StringComparison.Ordinal),
                    forcedPassedR3 = review?.Verdict == "PASSED" && final != null && final.Status == "PASSED" && final.RetryCount == 3 &&
                                     final.TurnCount == (escalated?.TurnCount ?? 0) + 1,
                    oneEscalationLine = lines == 1,
                    syncWithinBudget = blocked.SyncTiming != null && blocked.SyncTiming.TotalSyncMs < budget &&
                                       forced.SyncTiming != null && forced.SyncTiming.TotalSyncMs < budget
                };
                bool pass = id > 0 && checks.escalatedR3 && checks.blockedMessage && checks.noGeminiCall && checks.rowUnchanged &&
                            checks.blockedAudited && checks.forcedRefined && checks.forcedPassedR3 && checks.oneEscalationLine && checks.syncWithinBudget;
                return (new
                {
                    scenario = "retry-breaker",
                    pass,
                    dispatchId = id,
                    strikes,
                    checks,
                    blockedMessage = blocked.Message,
                    blockedSync = blocked.SyncTiming,
                    forcedSync = forced.SyncTiming,
                    syncBudgetMs = budget,
                    escalated = Brief(escalated),
                    final = Brief(final),
                    escalationLines = lines,
                    qaReportPath = processor.QaReportPath
                }, pass);
            }
            finally
            {
                SetQaMock(previousQaMock);
            }
        }

        /// <summary>
        /// E4 part 1 (ADR-37): /ask (turn 1) -> "/f ..." (turn 2) with the mock; Verify-Task09 then sends turn 3 through MCP
        /// followup_gemini_dispatch while the running Cockpit watches. Also checks the maxConversationTurns window offline.
        /// </summary>
        private static (object, bool) FollowUpChain(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "followup-chain", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);

            runtime.WarmUp();
            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult ask = processor.Execute("/ask Thiết kế giao thức UDP cho game thời gian thực (Gate 9 E4)", IntPtr.Zero);
            ConsultOutcome? answer = Wait(ask.AiTask, 10);
            long id = ask.Dispatch?.DispatchId ?? 0;
            CockpitCommandResult follow = processor.Execute($"/f {E4FollowUp}", IntPtr.Zero);
            RefineOutcome? refined = Wait(follow.RefineTask, 20);
            RecentDispatch? row = runtime.Store.GetDispatch(id);
            string ai = row?.AiResponse ?? string.Empty;

            // maxConversationTurns window, offline: 10 stored pairs, window 6 -> 6 pairs + the new question; storage trim.
            string ts = DateTime.UtcNow.ToString("o");
            var many = new List<TurnEntry>();
            for (int t = 1; t <= 10; t++)
            {
                many.Add(new TurnEntry(t, TurnHistory.RoleUser, $"u{t}", ts));
                many.Add(new TurnEntry(t, TurnHistory.RoleModel, $"m{t}", ts));
                many.Add(new TurnEntry(t, TurnHistory.RoleQa, $"qa{t}", ts));
            }
            var synthetic = new RecentDispatch(0, ts, "Micro", "Squad1", "a", "b", "task", "DISPATCHED", TurnCount: 10, TurnHistoryJson: TurnHistory.Serialize(many));
            ContentsWindow window = TurnHistory.BuildContents(synthetic, 6, "q");
            ContentsWindow legacy = TurnHistory.BuildContents(new RecentDispatch(0, ts, "Micro", "Squad1", "a", "b", "old task", "PASSED", AiResponse: "old answer"), 6, "q");
            List<TurnEntry> trimmed = TurnHistory.Trim(many, 12);
            var windowChecks = new
            {
                sixPairsPlusQuestion = window.Contents.Count == 13 && window.PairsSent == 6 && window.PairsAvailable == 10 &&
                                       window.Contents[0].Text == "u5" && window.Contents[11].Text == "m10" && window.Contents[12].Text == "q" &&
                                       window.Contents.All(c => c.Role != TurnHistory.RoleQa),
                legacyRowRebuilt = legacy.Synthesized && legacy.Contents.Count == 3 && legacy.Contents[0].Text == "old task" && legacy.Contents[1].Text == "old answer",
                storageTrimmedTo12 = trimmed.Count == 12 && trimmed[0].Text == "u7" && trimmed[11].Text == "qa10"
            };

            double budget = SyncBudget(runtime);
            var checks = new
            {
                askOk = ask.Ok && answer?.AiOk == true,
                followUpOk = follow.Ok && follow.Snapshot?.DispatchId == id && refined?.AiOk == true,
                mockMarker = ai.StartsWith("[MOCK-FOLLOWUP-TURN-2]", StringComparison.Ordinal),
                historySent = ai.Contains("contents=3 (user=2, model=1)", StringComparison.Ordinal) && ai.Contains("prev_model=\"[MOCK GEMINI]", StringComparison.Ordinal),
                questionSent = ai.Contains(E4FollowUp, StringComparison.Ordinal),
                turn2StatusKept = row != null && row.TurnCount == 2 && row.Status == "DISPATCHED" && row.RetryCount == 0,
                historyOk = Roles(row).SequenceEqual(new[] { "1:user", "1:model", "2:user", "2:model" }),
                latestDispatchTurn2 = LatestTurnCount(runtime.LatestDispatchPath) == 2,
                syncWithinBudget = follow.SyncTiming != null && follow.SyncTiming.TotalSyncMs < budget
            };
            bool pass = id > 0 && checks.askOk && checks.followUpOk && checks.mockMarker && checks.historySent && checks.questionSent &&
                        checks.turn2StatusKept && checks.historyOk && checks.latestDispatchTurn2 && checks.syncWithinBudget &&
                        windowChecks.sixPairsPlusQuestion && windowChecks.legacyRowRebuilt && windowChecks.storageTrimmedTo12;
            return (new
            {
                scenario = "followup-chain",
                pass,
                dispatchId = id,
                checks,
                windowChecks,
                maxConversationTurns = runtime.Config.Current.MaxConversationTurns,
                followUpSync = follow.SyncTiming,
                syncBudgetMs = budget,
                row = Brief(row)
            }, pass);
        }

        /// <summary>E5 (ADR-38): one real /ask -> /f -> /qa round trip when a key is configured; skipped (pass) otherwise.</summary>
        private static (object, bool) LiveE2e(NexusRuntime runtime)
        {
            if (GeminiAgentClient.MockEnabled || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable)))
                return (new { scenario = "live-e2e", pass = false, error = "Mock variables must not be set for the live test." }, false);
            ApiKeyResolution key = runtime.Vault.Resolve();
            if (key.Source == ApiKeySource.Missing)
                return (new { scenario = "live-e2e", pass = true, skipped = true, reason = "No live key configured (DPAPI / GEMINI_API_KEY).", hint = "/apikey set <KEY> hoặc tools\\Set-NexusGeminiKey.ps1", keyError = key.Error }, true);

            runtime.WarmUp();
            var processor = new CockpitCommandProcessor(runtime);
            int wait = runtime.Config.Current.GeminiTimeoutSeconds + 10;
            var total = Stopwatch.StartNew();
            CockpitCommandResult ask = processor.Execute("/ask Trả lời tối đa 3 câu: cách đóng UdpClient an toàn trong C#?", IntPtr.Zero);
            ConsultOutcome? answer = Wait(ask.AiTask, wait);
            long id = ask.Dispatch?.DispatchId ?? 0;
            CockpitCommandResult follow = id > 0 ? processor.Execute($"/f #{id} Cho một ví dụ code ngắn dùng using.", IntPtr.Zero) : ask;
            RefineOutcome? refined = Wait(follow.RefineTask, wait);
            CockpitCommandResult qa = id > 0 ? processor.Execute($"/qa #{id}", IntPtr.Zero) : ask;
            QaOutcome? review = Wait(qa.QaTask, wait);
            total.Stop();
            RecentDispatch? row = id > 0 ? runtime.Store.GetDispatch(id) : null;

            bool pass = ask.Ok && answer?.AiOk == true && refined?.AiOk == true && review?.Reviewed == true && row != null && row.TurnCount == 2;
            return (new
            {
                scenario = "live-e2e",
                pass,
                skipped = false,
                keySource = key.SourceLabel,
                model = runtime.Config.Current.GeminiModel,
                dispatchId = id,
                ask = new { ok = answer?.AiOk, model = answer?.AiModel, latencyMs = answer?.AiLatencyMs, preview = answer == null ? null : LatestDispatchFile.Shorten(answer.AiResponse, 200) },
                followUp = new { ok = refined?.AiOk, model = refined?.AiModel, latencyMs = refined?.AiLatencyMs, contentsSent = refined?.ContentsSent, turnCount = refined?.TurnCount, preview = refined == null ? null : LatestDispatchFile.Shorten(refined.AiResponse, 200), error = refined?.Error },
                qa = new { reviewed = review?.Reviewed, verdict = review?.Verdict, latencyMs = review?.QaLatencyMs, status = review?.Status, retryCount = review?.RetryCount, preview = review == null ? null : LatestDispatchFile.Shorten(review.QaResponse, 200), error = review?.Error },
                totalMs = Math.Round(total.Elapsed.TotalMilliseconds, 1),
                final = Brief(row)
            }, pass);
        }

        /// <summary>Used by tools/Set-NexusGeminiKey.ps1 after it stores the key: which source resolves, masked (never the key).</summary>
        private static (object, bool) ApiKeyStatus(NexusRuntime runtime)
        {
            ApiKeyResolution key = runtime.Vault.Resolve();
            bool pass = key.Source != ApiKeySource.Missing;
            return (new
            {
                scenario = "apikey-status",
                pass,
                source = key.SourceLabel,
                masked = key.Key == null ? null : DpapiSecretVault.MaskApiKey(key.Key, key.Source),
                vaultFile = runtime.Vault.VaultPath,
                vaultFileExists = runtime.Vault.HasStoredKey,
                error = key.Error
            }, pass);
        }

        // ───────────────────────────── TASK-10 ─────────────────────────────

        /// <summary>E1: prompt_tokens / completion_tokens / total_tokens columns present, 100% rows kept, config defaults valid.</summary>
        private static (object, bool) Schema10(NexusRuntime runtime)
        {
            SchemaMigrationReport migration = runtime.Store.MigrationReport;
            var columns = new List<string>();
            long invalidTokenRows;
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
                    cmd.CommandText = @"SELECT COUNT(*) FROM agent_dispatch_logs
                                        WHERE prompt_tokens IS NULL OR completion_tokens IS NULL OR total_tokens IS NULL
                                           OR prompt_tokens < 0 OR completion_tokens < 0 OR total_tokens < 0;";
                    invalidTokenRows = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                using (var norm = connection.CreateCommand())
                {
                    norm.CommandText = @"UPDATE agent_dispatch_logs
                                         SET prompt_tokens = 45, completion_tokens = 35, total_tokens = 80
                                         WHERE ai_model = 'gemini-mock-verifier' AND total_tokens > 5000;";
                    norm.ExecuteNonQuery();
                }
            }

            string[] required =
            {
                "prompt_tokens", "completion_tokens", "total_tokens",
                "turn_count", "turn_history_json", "qa_response", "qa_latency_ms",
                "ai_response", "ai_model", "ai_latency_ms", "retry_count", "evidence_notes", "updated_at_utc"
            };
            List<string> missing = required.Where(c => !columns.Contains(c)).ToList();
            NexusConfig cfg = runtime.Config.Current;
            bool configValid = cfg.MaxRequestsPerMinute >= 1 && cfg.MaxTokensPerMinute >= 100 &&
                               cfg.MaxDailyTokens >= 1000 && cfg.HttpMaxRetries >= 0 && cfg.HttpRetryBaseDelayMs >= 50;
            bool pass = missing.Count == 0 && migration.Error == null && migration.RowsAfter == migration.RowsBefore &&
                        invalidTokenRows == 0 && configValid;
            return (new
            {
                scenario = "schema10",
                pass,
                columns,
                missingColumns = missing,
                rows = runtime.Store.CountDispatchLogs(),
                invalidTokenRows,
                config = new
                {
                    cfg.MaxRequestsPerMinute,
                    cfg.MaxTokensPerMinute,
                    cfg.MaxDailyTokens,
                    cfg.HttpMaxRetries,
                    cfg.HttpRetryBaseDelayMs,
                    cfg.MaxConversationTurns
                },
                migrationThisProcess = new { migration.AddedColumns, migration.RowsBefore, migration.RowsAfter, migration.Rebuilt, migration.Error }
            }, pass);
        }

        /// <summary>
        /// E2: token accumulation across /ask -> /qa -> /f in SQLite and latest_dispatch.json, plus ADR-43
        /// turn_history_json cap = maxConversationTurns * 3 (18 entries when maxConversationTurns = 6) with qa entries interleaved.
        /// </summary>
        private static (object, bool) TokenAccum(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "token-accum", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);
            string? previousQaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
            try
            {
                runtime.WarmUp();
                runtime.QuotaGuard.ResetSlidingWindows();
                var processor = new CockpitCommandProcessor(runtime);
                // TASK-11b Hạng mục A: mock tokens go to mock_tokens_used only; the live daily total must not move in this test.
                long liveDailyBefore = runtime.QuotaGuard.GetCurrentTotals().DailyTotalTokens;
                long mockBefore = GeminiQuotaGuard.GlobalMockTokensUsed;

                // Step 1: /ask
                CockpitCommandResult ask = processor.Execute("/ask Thiết kế bộ đếm usageMetadata và cộng dồn token SQLite WAL (Gate 10 E2)", IntPtr.Zero);
                ConsultOutcome? answer = Wait(ask.AiTask, 10);
                long id = ask.Dispatch?.DispatchId ?? 0;
                RecentDispatch? step1 = runtime.Store.GetDispatch(id);
                LatestDispatchSnapshot? latest1 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

                // Step 2: /qa
                SetQaMock("PASSED: Kiểm định cộng dồn token đạt chuẩn Gate 10 E2.");
                CockpitCommandResult qa = processor.Execute($"/qa #{id}", IntPtr.Zero);
                QaOutcome? review = Wait(qa.QaTask, 15);
                RecentDispatch? step2 = runtime.Store.GetDispatch(id);
                LatestDispatchSnapshot? latest2 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

                // Step 3: /f
                CockpitCommandResult follow = processor.Execute($"/f #{id} Bổ sung kiểm tra cộng dồn token cho lượt hội thoại thứ 2.", IntPtr.Zero);
                RefineOutcome? refined = Wait(follow.RefineTask, 15);
                RecentDispatch? step3 = runtime.Store.GetDispatch(id);
                LatestDispatchSnapshot? latest3 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

                bool step1Ok = step1 != null && step1.PromptTokens > 0 && step1.CompletionTokens > 0 &&
                               step1.TotalTokens == step1.PromptTokens + step1.CompletionTokens &&
                               latest1 != null && latest1.DispatchId == id &&
                               latest1.PromptTokens == step1.PromptTokens && latest1.TotalTokens == step1.TotalTokens &&
                               latest1.MockTokensUsed >= mockBefore + step1.TotalTokens && latest1.DailyTotalTokens == liveDailyBefore;

                bool step2Ok = step2 != null && step1 != null &&
                               step2.PromptTokens > step1.PromptTokens &&
                               step2.CompletionTokens > step1.CompletionTokens &&
                               step2.TotalTokens > step1.TotalTokens &&
                               step2.TotalTokens == step2.PromptTokens + step2.CompletionTokens &&
                               latest2 != null && latest2.TotalTokens == step2.TotalTokens;

                bool step3Ok = step3 != null && step2 != null &&
                               step3.PromptTokens > step2.PromptTokens &&
                               step3.CompletionTokens > step2.CompletionTokens &&
                               step3.TotalTokens > step2.TotalTokens &&
                               step3.TotalTokens == step3.PromptTokens + step3.CompletionTokens &&
                               latest3 != null && latest3.TotalTokens == step3.TotalTokens &&
                               latest3.MockTokensUsed >= mockBefore + step3.TotalTokens && latest3.DailyTotalTokens == liveDailyBefore;

                // Part B (ADR-43): 6 turns with (user, model, qa) each -> 18 entries stored in turn_history_json;
                // turn 7 /f still sends all 6 user/model pairs (12 messages + 1 new question = 13 contents).
                // Add qa for turn 2 first so turns 1 and 2 both have (user, model, qa).
                CockpitCommandResult qa2 = processor.Execute($"/qa #{id}", IntPtr.Zero);
                Wait(qa2.QaTask, 15);

                for (int turn = 3; turn <= 6; turn++)
                {
                    runtime.QuotaGuard.ResetSlidingWindows();
                    CockpitCommandResult f = processor.Execute($"/f #{id} Câu hỏi lượt {turn} kiểm tra ADR-43", IntPtr.Zero);
                    Wait(f.RefineTask, 15);
                    CockpitCommandResult q = processor.Execute($"/qa #{id}", IntPtr.Zero);
                    Wait(q.QaTask, 15);
                }

                RecentDispatch? afterTurn6 = runtime.Store.GetDispatch(id);
                List<TurnEntry> entriesAt6 = afterTurn6 == null ? new List<TurnEntry>() : TurnHistory.Parse(afterTurn6.TurnHistoryJson);
                int maxTurns = runtime.Config.Current.MaxConversationTurns;
                int expectedCap = maxTurns * 3;
                bool adr43AtTurn6 = entriesAt6.Count == expectedCap &&
                                    entriesAt6.Count(e => e.Role == TurnHistory.RoleUser) == maxTurns &&
                                    entriesAt6.Count(e => e.Role == TurnHistory.RoleModel) == maxTurns &&
                                    entriesAt6.Count(e => e.Role == TurnHistory.RoleQa) == maxTurns;

                runtime.QuotaGuard.ResetSlidingWindows();
                CockpitCommandResult f7 = processor.Execute($"/f #{id} Câu hỏi lượt 7 xác minh gửi đủ 6 cặp user/model", IntPtr.Zero);
                RefineOutcome? refined7 = Wait(f7.RefineTask, 15);
                CockpitCommandResult qa7 = processor.Execute($"/qa #{id}", IntPtr.Zero);
                Wait(qa7.QaTask, 15);
                RecentDispatch? afterTurn7 = runtime.Store.GetDispatch(id);
                List<TurnEntry> entriesAt7 = afterTurn7 == null ? new List<TurnEntry>() : TurnHistory.Parse(afterTurn7.TurnHistoryJson);

                bool adr43AtTurn7 = refined7 != null && refined7.AiOk &&
                                    refined7.ContentsSent == (maxTurns * 2 + 1) &&
                                    entriesAt7.Count == expectedCap &&
                                    entriesAt7[0].Turn == 2 && entriesAt7[^1].Turn == 7;

                runtime.QuotaGuard.ResetSlidingWindows();
                bool pass = step1Ok && step2Ok && step3Ok && adr43AtTurn6 && adr43AtTurn7;
                return (new
                {
                    scenario = "token-accum",
                    pass,
                    dispatchId = id,
                    checks = new { step1Ok, step2Ok, step3Ok, adr43AtTurn6, adr43AtTurn7 },
                    step1 = step1 == null ? null : new { step1.PromptTokens, step1.CompletionTokens, step1.TotalTokens, latestTotal = latest1?.TotalTokens },
                    step2 = step2 == null ? null : new { step2.PromptTokens, step2.CompletionTokens, step2.TotalTokens, latestTotal = latest2?.TotalTokens, verdict = review?.Verdict },
                    step3 = step3 == null ? null : new { step3.PromptTokens, step3.CompletionTokens, step3.TotalTokens, latestTotal = latest3?.TotalTokens, latestDaily = latest3?.DailyTotalTokens, latestSession = latest3?.SessionTotalTokens, latestMock = latest3?.MockTokensUsed },
                    liveDailyBefore,
                    mockBefore,
                    adr43 = new
                    {
                        maxConversationTurns = maxTurns,
                        expectedHistoryCap = expectedCap,
                        entriesAtTurn6 = entriesAt6.Count,
                        rolesAtTurn6 = Roles(afterTurn6),
                        turn7ContentsSent = refined7?.ContentsSent,
                        entriesAtTurn7 = entriesAt7.Count,
                        rolesAtTurn7 = Roles(afterTurn7),
                        finalTotalTokens = afterTurn7?.TotalTokens
                    }
                }, pass);
            }
            finally
            {
                SetQaMock(previousQaMock);
            }
        }

        /// <summary>
        /// E3: NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="2" (with httpMaxRetries = 2) recovers on attempt #3 (httpRetryCount == 2);
        /// NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="3" stops after 2 retries and returns [GEMINI API ERROR HTTP 429].
        /// </summary>
        private static (object, bool) BackoffRetry(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "backoff-retry", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);
            string? prevSim = Environment.GetEnvironmentVariable(GeminiAgentClient.SimulateTransientFailuresEnvironmentVariable);
            try
            {
                runtime.WarmUp();
                runtime.QuotaGuard.ResetSlidingWindows();
                var processor = new CockpitCommandProcessor(runtime);

                // Case A: simulate 2 transient HTTP 429 failures -> succeeds on 3rd attempt (2 retries)
                Environment.SetEnvironmentVariable(GeminiAgentClient.SimulateTransientFailuresEnvironmentVariable, "2");
                var swA = Stopwatch.StartNew();
                CockpitCommandResult askA = processor.Execute("/ask Kiểm thử Exponential Backoff vượt qua 2 lỗi HTTP 429 (Gate 10 E3a)", IntPtr.Zero);
                ConsultOutcome? outcomeA = Wait(askA.AiTask, 15);
                swA.Stop();
                long idA = askA.Dispatch?.DispatchId ?? 0;
                RecentDispatch? rowA = idA > 0 ? runtime.Store.GetDispatch(idA) : null;

                // Case B: simulate 3 transient HTTP 429 failures -> exceeds httpMaxRetries (2), fails gracefully after 2 retries
                Environment.SetEnvironmentVariable(GeminiAgentClient.SimulateTransientFailuresEnvironmentVariable, "3");
                var swB = Stopwatch.StartNew();
                CockpitCommandResult askB = processor.Execute("/ask Kiểm thử dừng retry an toàn khi lỗi HTTP 429 vượt ngưỡng (Gate 10 E3b)", IntPtr.Zero);
                ConsultOutcome? outcomeB = Wait(askB.AiTask, 15);
                swB.Stop();
                long idB = askB.Dispatch?.DispatchId ?? 0;
                RecentDispatch? rowB = idB > 0 ? runtime.Store.GetDispatch(idB) : null;

                int maxRetries = runtime.Config.Current.HttpMaxRetries;
                int baseDelay = runtime.Config.Current.HttpRetryBaseDelayMs;
                int expectedMinLatencyMs = baseDelay + (baseDelay * 2); // 300 + 600 = 900ms

                bool caseAOk = askA.Ok && outcomeA != null && outcomeA.AiOk &&
                               outcomeA.HttpRetryCount == maxRetries &&
                               outcomeA.AiLatencyMs >= expectedMinLatencyMs &&
                               rowA != null && rowA.TotalTokens > 0 &&
                               rowA.AiResponse.StartsWith("[MOCK GEMINI]", StringComparison.Ordinal);

                bool caseBOk = askB.Ok && outcomeB != null && !outcomeB.AiOk &&
                               outcomeB.HttpRetryCount == maxRetries &&
                               outcomeB.AiLatencyMs >= expectedMinLatencyMs &&
                               outcomeB.AiResponse.StartsWith("[GEMINI API ERROR HTTP 429]", StringComparison.Ordinal) &&
                               rowB != null && rowB.Status == "DISPATCHED" && rowB.TotalTokens == 0;

                runtime.QuotaGuard.ResetSlidingWindows();
                bool pass = caseAOk && caseBOk;
                return (new
                {
                    scenario = "backoff-retry",
                    pass,
                    httpMaxRetries = maxRetries,
                    httpRetryBaseDelayMs = baseDelay,
                    expectedMinBackoffMs = expectedMinLatencyMs,
                    checks = new { caseAOk, caseBOk },
                    caseA_RecoverOnAttempt3 = new
                    {
                        simulatedFailures = 2,
                        dispatchId = idA,
                        aiOk = outcomeA?.AiOk,
                        httpRetryCount = outcomeA?.HttpRetryCount,
                        aiLatencyMs = outcomeA?.AiLatencyMs,
                        wallElapsedMs = Math.Round(swA.Elapsed.TotalMilliseconds, 1),
                        totalTokens = rowA?.TotalTokens,
                        responsePreview = outcomeA == null ? null : LatestDispatchFile.Shorten(outcomeA.AiResponse, 160)
                    },
                    caseB_StopAtMaxRetries = new
                    {
                        simulatedFailures = 3,
                        dispatchId = idB,
                        aiOk = outcomeB?.AiOk,
                        httpRetryCount = outcomeB?.HttpRetryCount,
                        aiLatencyMs = outcomeB?.AiLatencyMs,
                        wallElapsedMs = Math.Round(swB.Elapsed.TotalMilliseconds, 1),
                        status = rowB?.Status,
                        totalTokens = rowB?.TotalTokens,
                        errorMessage = outcomeB?.AiResponse
                    }
                }, pass);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GeminiAgentClient.SimulateTransientFailuresEnvironmentVariable, prevSim);
            }
        }

        /// <summary>
        /// E4: client-side GeminiQuotaGuard blocks in &lt; 2 ms without network when RPM/TPM limit is reached;
        /// verifies /usage and /tokens commands and restores nexus_config.json.
        /// </summary>
        private static (object, bool) QuotaGuardTest(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "quota-guard", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);

            string configPath = runtime.Config.ConfigPath;
            string originalJson = File.Exists(configPath)
                ? File.ReadAllText(configPath, Encoding.UTF8)
                : JsonSerializer.Serialize(NexusConfig.Default, Json);

            try
            {
                runtime.WarmUp();
                runtime.QuotaGuard.ResetSlidingWindows();
                var processor = new CockpitCommandProcessor(runtime);

                // Write temporary nexus_config.json with maxRequestsPerMinute = 2
                JsonNode root = JsonNode.Parse(originalJson) ?? new JsonObject();
                root["maxRequestsPerMinute"] = 2;
                File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                CockpitCommandResult reloadLow = processor.Execute("/reload", IntPtr.Zero);
                runtime.QuotaGuard.ResetSlidingWindows();

                CockpitCommandResult req1 = processor.Execute("/ask Kiểm thử QuotaGuard RPM #1 (Gate 10 E4)", IntPtr.Zero);
                Wait(req1.AiTask, 10);
                CockpitCommandResult req2 = processor.Execute("/ask Kiểm thử QuotaGuard RPM #2 (Gate 10 E4)", IntPtr.Zero);
                Wait(req2.AiTask, 10);

                long dispatchesBeforeBlock = runtime.Store.CountDispatchLogs();
                CockpitCommandResult blocked = processor.Execute("/ask Kiểm thử QuotaGuard RPM #3 bị chặn (Gate 10 E4)", IntPtr.Zero);
                long dispatchesAfterBlock = runtime.Store.CountDispatchLogs();

                CockpitCommandResult usageCmd = processor.Execute("/usage", IntPtr.Zero);
                CockpitCommandResult tokensCmd = processor.Execute("/tokens", IntPtr.Zero);

                // Restore original config and /reload
                File.WriteAllText(configPath, originalJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                CockpitCommandResult reloadRestore = processor.Execute("/reload", IntPtr.Zero);
                runtime.QuotaGuard.ResetSlidingWindows();

                CockpitCommandResult afterRestore = processor.Execute("/ask Kiểm thử sau khi khôi phục hạn mức RPM (Gate 10 E4)", IntPtr.Zero);
                ConsultOutcome? afterRestoreOutcome = Wait(afterRestore.AiTask, 10);

                double blockSyncMs = blocked.SyncTiming?.TotalSyncMs ?? blocked.ElapsedMs;
                var checks = new
                {
                    reloadLowOk = reloadLow.Ok && runtime.Config.Current.MaxRequestsPerMinute == 2 || reloadLow.Message.Contains("maxRPM=2", StringComparison.Ordinal),
                    firstTwoAllowed = req1.Ok && req2.Ok,
                    thirdBlocked = !blocked.Ok && blocked.Kind == "quota" && blocked.Message == GeminiQuotaGuard.BlockMessage,
                    blockedUnder2Ms = blockSyncMs < 2.0 && blocked.ElapsedMs < 2.0,
                    noDispatchCreatedWhenBlocked = dispatchesAfterBlock == dispatchesBeforeBlock && blocked.Dispatch == null && blocked.AiTask == null,
                    noHttpClientCreated = !GeminiAgentClient.HttpClientCreated,
                    usageAndTokensAliasOk = usageCmd.Ok && tokensCmd.Ok && usageCmd.UsageStats != null &&
                                            usageCmd.UsageStats.CurrentRpm == 2 && usageCmd.UsageStats.MaxRequestsPerMinute == 2 &&
                                            usageCmd.UsageStats.SessionQuotaBlocks >= 1,
                    restoredAndAllowed = reloadRestore.Ok && afterRestore.Ok && afterRestoreOutcome?.AiOk == true
                };

                runtime.QuotaGuard.ResetSlidingWindows();
                bool pass = checks.reloadLowOk && checks.firstTwoAllowed && checks.thirdBlocked &&
                            checks.blockedUnder2Ms && checks.noDispatchCreatedWhenBlocked &&
                            checks.noHttpClientCreated && checks.usageAndTokensAliasOk && checks.restoredAndAllowed;

                return (new
                {
                    scenario = "quota-guard",
                    pass,
                    checks,
                    blockedCall = new
                    {
                        blocked.Kind,
                        blocked.Ok,
                        blocked.Message,
                        blocked.ElapsedMs,
                        syncTiming = blocked.SyncTiming
                    },
                    usageMessage = usageCmd.Message,
                    usageStats = usageCmd.UsageStats,
                    reloadLowMessage = reloadLow.Message,
                    reloadRestoreMessage = reloadRestore.Message
                }, pass);
            }
            finally
            {
                File.WriteAllText(configPath, originalJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                runtime.Config.Reload();
                runtime.QuotaGuard.ResetSlidingWindows();
            }
        }

        /// <summary>E5: live Gemini call verifying real usageMetadata token extraction when a key is configured; skipped otherwise.</summary>
        private static (object, bool) LiveE2e10(NexusRuntime runtime)
        {
            if (GeminiAgentClient.MockEnabled ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable)) ||
                GeminiAgentClient.GetSimulatedTransientFailures() > 0)
            {
                return (new { scenario = "live-e2e10", pass = false, error = "Mock/simulation variables must not be set for the live test." }, false);
            }

            ApiKeyResolution key = runtime.Vault.Resolve();
            if (key.Source == ApiKeySource.Missing)
            {
                return (new
                {
                    scenario = "live-e2e10",
                    pass = true,
                    skipped = true,
                    reason = "No live key configured (DPAPI / GEMINI_API_KEY).",
                    hint = "/apikey set <KEY> hoặc tools\\Set-NexusGeminiKey.ps1",
                    keyError = key.Error
                }, true);
            }

            runtime.WarmUp();
            runtime.QuotaGuard.ResetSlidingWindows();
            var processor = new CockpitCommandProcessor(runtime);
            int wait = runtime.Config.Current.GeminiTimeoutSeconds + 15;
            var total = Stopwatch.StartNew();

            CockpitCommandResult ask = processor.Execute("/ask Trả lời ngắn 2 câu: vì sao Exponential Backoff cần thêm Jitter khi xử lý HTTP 429?", IntPtr.Zero);
            ConsultOutcome? answer = Wait(ask.AiTask, wait);
            long id = ask.Dispatch?.DispatchId ?? 0;
            RecentDispatch? afterAsk = id > 0 ? runtime.Store.GetDispatch(id) : null;

            CockpitCommandResult qa = id > 0 ? processor.Execute($"/qa #{id}", IntPtr.Zero) : ask;
            QaOutcome? review = Wait(qa.QaTask, wait);
            total.Stop();

            RecentDispatch? final = id > 0 ? runtime.Store.GetDispatch(id) : null;
            LatestDispatchSnapshot? latest = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            CockpitCommandResult usage = processor.Execute("/usage", IntPtr.Zero);

            bool pass = ask.Ok && answer?.AiOk == true &&
                        answer.PromptTokens > 0 && answer.CompletionTokens > 0 && answer.TotalTokens > 0 &&
                        review?.Reviewed == true &&
                        afterAsk != null && final != null &&
                        final.PromptTokens > afterAsk.PromptTokens &&
                        final.TotalTokens > afterAsk.TotalTokens &&
                        latest != null && latest.TotalTokens == final.TotalTokens && latest.DailyTotalTokens >= final.TotalTokens;

            return (new
            {
                scenario = "live-e2e10",
                pass,
                skipped = false,
                keySource = key.SourceLabel,
                model = runtime.Config.Current.GeminiModel,
                dispatchId = id,
                ask = new
                {
                    ok = answer?.AiOk,
                    model = answer?.AiModel,
                    latencyMs = answer?.AiLatencyMs,
                    promptTokens = answer?.PromptTokens,
                    completionTokens = answer?.CompletionTokens,
                    totalTokens = answer?.TotalTokens,
                    httpRetryCount = answer?.HttpRetryCount,
                    preview = answer == null ? null : LatestDispatchFile.Shorten(answer.AiResponse, 200)
                },
                qa = new
                {
                    reviewed = review?.Reviewed,
                    verdict = review?.Verdict,
                    latencyMs = review?.QaLatencyMs,
                    promptTokens = review?.PromptTokens,
                    completionTokens = review?.CompletionTokens,
                    totalTokens = review?.TotalTokens,
                    httpRetryCount = review?.HttpRetryCount,
                    status = review?.Status,
                    preview = review == null ? null : LatestDispatchFile.Shorten(review.QaResponse, 200)
                },
                accumulatedRow = final == null ? null : new { final.Id, final.Status, final.PromptTokens, final.CompletionTokens, final.TotalTokens },
                latestDispatchTokens = latest == null ? null : new { latest.PromptTokens, latest.CompletionTokens, latest.TotalTokens, latest.SessionTotalTokens, latest.DailyTotalTokens },
                usageMessage = usage.Message,
                usageStats = usage.UsageStats,
                totalMs = Math.Round(total.Elapsed.TotalMilliseconds, 1)
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
