using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-14 (ADR-59) ─────────────────────────────

        private const int CalcSamples = 30;
        private const double CalcP50MaxMs = 1.5;  // Desktop mode, whole synchronous path incl. the ADR-14 write
        private const double CalcP95MaxMs = 2.5;
        private const int Task14BaselineRows = 3001; // Gate 11b run 5

        /// <summary>Gate 14 E4 note (spec text) and the dummy key of the Secret Guard check (built at run time, never a literal).</summary>
        private const string GateNoteText = "Mã cổng thử nghiệm của Gate 14 là 49152";
        private const string GateGroundQuery = "Mã cổng thử nghiệm của Gate 14 là bao nhiêu?";
        private static string DummyGoogleKey => "AIza" + "SyDummySecretKeyForTest123456789";

        /// <summary>Every token counter the user can see: live daily (SQLite), mock daily, session, RPM / TPM windows, dispatch rows.</summary>
        private sealed record TokenLedger(long LiveDailyTotal, long MockDaily, long SessionTotal, long SessionRequests,
            int CurrentRpm, long CurrentTpm, long GlobalMock, long DispatchRows)
        {
            public static TokenLedger Read(NexusRuntime runtime)
            {
                GeminiUsageStats s = runtime.QuotaGuard.GetStats(runtime.Config.Current, runtime.Store);
                return new TokenLedger(runtime.Store.GetDailyTokenUsage().TotalTokens, runtime.Store.GetDailyMockTokens(),
                    s.SessionTotalTokens, s.SessionRequests, s.CurrentRpm, s.CurrentTpm, GeminiQuotaGuard.GlobalMockTokensUsed,
                    runtime.Store.CountDispatchLogs());
            }

            public bool SameTokens(TokenLedger other) =>
                LiveDailyTotal == other.LiveDailyTotal && MockDaily == other.MockDaily && SessionTotal == other.SessionTotal &&
                SessionRequests == other.SessionRequests && CurrentRpm == other.CurrentRpm && CurrentTpm == other.CurrentTpm &&
                GlobalMock == other.GlobalMock;
        }

        private static bool NoBom(string path)
        {
            if (!File.Exists(path)) return true;
            byte[] b = File.ReadAllBytes(path);
            return !(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF);
        }

        /// <summary>latest_dispatch.json written by a local utility: no BOM, readable, local event, 0 tokens, local-utility model.</summary>
        private static object CheckLocalLatest(NexusRuntime runtime, string expectedEvent, string expectedUtility, string expectedStatus, out bool ok)
        {
            string path = runtime.LatestDispatchPath;
            LatestDispatchSnapshot? s = LatestDispatchFile.TryRead(path);
            bool noBom = NoBom(path);
            ok = s != null && noBom && s.Event == expectedEvent && s.LocalUtility == expectedUtility && s.Status == expectedStatus &&
                 s.AiModel == LatestDispatchFile.LocalUtilityModel && s.ModelUsed == LatestDispatchFile.LocalUtilityModel &&
                 s.PromptTokens == 0 && s.CompletionTokens == 0 && s.TotalTokens == 0 && s.DispatchId == 0;
            return new
            {
                ok,
                noBom,
                eventName = s?.Event,
                localUtility = s?.LocalUtility,
                status = s?.Status,
                aiModel = s?.AiModel,
                promptTokens = s?.PromptTokens,
                completionTokens = s?.CompletionTokens,
                totalTokens = s?.TotalTokens,
                dispatchId = s?.DispatchId,
                aiResponse = s == null ? null : LatestDispatchFile.Shorten(s.AiResponse, 160),
                source = s?.Source
            };
        }

        /// <summary>E1: SQLite baseline rows kept (&gt;= 3 001), migration clean, zettelkasten path, default combat list has Overwolf.</summary>
        private static (object, bool) Schema14(NexusRuntime runtime)
        {
            SchemaMigrationReport migration = runtime.Store.MigrationReport;
            long rows = runtime.Store.CountDispatchLogs();
            bool rowsPreserved = rows >= Task14BaselineRows && migration.Error == null && migration.RowsAfter >= migration.RowsBefore;
            string notesPath = runtime.LocalUtilities.Notes.NotePath;
            bool notesNoBom = NoBom(notesPath);
            bool overwolfDefault = NexusConfig.Default.CombatProcessPrefixes.Contains("Overwolf");
            IReadOnlyList<string> combatNow = runtime.Config.Current.CombatProcessPrefixes;
            bool pass = rowsPreserved && notesNoBom && overwolfDefault;
            return (new
            {
                scenario = "schema14",
                pass,
                rows,
                baselineMinRows = Task14BaselineRows,
                rowsPreserved,
                migrationThisProcess = new { migration.AddedColumns, migration.RowsBefore, migration.RowsAfter, migration.Rebuilt, migration.Error },
                quickNotes = new { path = notesPath, exists = File.Exists(notesPath), noBom = notesNoBom, relative = LocalNoteService.RelativePath },
                overwolfInDefaultCombatPrefixes = overwolfDefault,
                combatProcessPrefixesInConfig = combatNow,
                overwolfInConfigFile = combatNow.Contains("Overwolf", StringComparer.OrdinalIgnoreCase),
                dispatchSla = runtime.Config.DetectSla()
            }, pass);
        }

        /// <summary>E2: /calc precision, Hex/Bin format, error guard, 30-sample P50/P95, 0 tokens (live and mock).</summary>
        private static (object, bool) LocalCalc(NexusRuntime runtime)
        {
            runtime.WarmUp();
            var processor = new CockpitCommandProcessor(runtime);
            TokenLedger before = TokenLedger.Read(runtime);

            var cases = new (string Input, long? Int, double? Real, string? Contains)[]
            {
                ("/calc sqrt(144) + 2^8", 268, null, "= 268 (Hex: 0x10C | Bin: 0b100001100)"),
                ("/c 0xFF + 0b101", 260, null, "= 260 (Hex: 0x104 | Bin: 0b100000100)"),
                ("/= (1 + 2) * 3 - 4 / 2 % 3", 7, null, "= 7 (Hex: 0x7 | Bin: 0b111)"),
                ("/=2+3", 5, null, "= 5 (Hex: 0x5"),
                ("/calc 1 << 10 | 0x0F & 0b1010", 1034, null, "Hex: 0x40A"),
                ("/calc 0xC0A80001 >> 8 & 0xFF", 0, null, "= 0 (Hex: 0x0"),
                ("/calc -2^2", -4, null, "= -4 (Hex: -0x4 | Bin: -0b100)"),
                ("/calc 2^3^2", 512, null, "= 512"),
                ("/calc max(3, 7, 5) + min(2, 9)", 9, null, "= 9"),
                ("/calc 10 / 4", null, 2.5, "= 2.5"),
                ("/calc round(2.5) + floor(-1.5) + ceil(1.2)", 3, null, "= 3"),
                ("/calc log2(1024) + abs(-6)", 16, null, "= 16"),
                ("/calc 2 * pi", null, 2 * Math.PI, "= 6.28318530717959"),
                ("/calc 0.1 + 0.2", null, 0.3, "= 0.3")
            };
            var caseResults = new List<object>();
            bool casesOk = true;
            foreach (var c in cases)
            {
                CockpitCommandResult r = processor.Execute(c.Input, IntPtr.Zero);
                LocalCalcResult? calc = r.Local?.Data as LocalCalcResult;
                bool ok = r.Ok && r.Kind == LocalUtilityService.Calc && calc != null &&
                          (c.Int == null || calc.IntegerValue == c.Int) &&
                          (c.Real == null || (calc.Value.HasValue && Math.Abs(calc.Value.Value - c.Real.Value) < 1e-9)) &&
                          (c.Contains == null || r.Message.Contains(c.Contains, StringComparison.Ordinal)) &&
                          r.Message.StartsWith("[CALC] ", StringComparison.Ordinal);
                casesOk &= ok;
                caseResults.Add(new { input = c.Input, ok, message = r.Message, elapsedMs = r.ElapsedMs });
            }

            string deep = "/calc " + new string('(', 60) + "1" + new string(')', 60);
            string[] errorInputs =
            {
                "/calc 10 / 0", "/calc 7 % 0", "/calc (2 + 3", "/calc 2 +* 3", "/calc sqrt(-1)", "/calc foo(2)", "/calc 2pi",
                "/calc 1 << 64", "/calc 0x", "/calc 0x8000000000000000", "/calc 10^400", "/calc 1.5 & 1", "/calc", deep
            };
            var errorResults = new List<object>();
            bool errorsOk = true;
            foreach (string input in errorInputs)
            {
                bool threw = false;
                CockpitCommandResult? r = null;
                try { r = processor.Execute(input, IntPtr.Zero); }
                catch (Exception) { threw = true; }
                bool ok = !threw && r != null && !r.Ok && r.Message.StartsWith("[CALC ERROR] ", StringComparison.Ordinal);
                errorsOk &= ok;
                errorResults.Add(new { input = input.Length > 40 ? input.Substring(0, 40) + "…" : input, ok, threw, message = r?.Message });
            }
            bool divZeroMessage = processor.Execute("/calc 10 / 0", IntPtr.Zero).Message.Contains("Chia cho 0", StringComparison.Ordinal);

            // latest_dispatch.json after a successful /calc (ADR-14 file, 0 tokens, local-utility).
            processor.Execute("/calc sqrt(144) + 2^8", IntPtr.Zero);
            object latest = CheckLocalLatest(runtime, LatestDispatchFile.LocalUtilityEvent, LocalUtilityService.Calc, "PASSED", out bool latestOk);

            // 30 consecutive /calc after 1 warm-up call: whole Execute path (command parse + calc + atomic write).
            string[] expressions = { "sqrt(144) + 2^8", "0xFF + 0b101", "(1500 * 8) / 1024", "1 << 16 | 0x0F", "max(3, 7) ^ 2 - 1" };
            processor.Execute($"/calc {expressions[0]}", IntPtr.Zero);
            var totals = new List<double>(CalcSamples);
            var computes = new List<double>(CalcSamples);
            bool allSamplesOk = true;
            for (int i = 0; i < CalcSamples; i++)
            {
                CockpitCommandResult r = processor.Execute($"/calc {expressions[i % expressions.Length]}", IntPtr.Zero);
                allSamplesOk &= r.Ok && r.Local != null && r.Local.FileError == null;
                totals.Add(r.ElapsedMs);
                computes.Add(r.Local?.ComputeMs ?? -1);
            }
            LatencyStats total = LatencyStats.Of(totals);
            LatencyStats compute = LatencyStats.Of(computes);
            bool latencyOk = allSamplesOk && totals.Count == CalcSamples && total.P50 <= CalcP50MaxMs && total.P95 <= CalcP95MaxMs;

            TokenLedger after = TokenLedger.Read(runtime);
            bool zeroTokens = before.SameTokens(after);
            bool noDispatchRows = after.DispatchRows == before.DispatchRows;

            var checks = new { casesOk, errorsOk, divZeroMessage, latestOk, latencyOk, zeroTokens, noDispatchRows };
            bool pass = casesOk && errorsOk && divZeroMessage && latestOk && latencyOk && zeroTokens && noDispatchRows;
            return (new
            {
                scenario = "local-calc",
                pass,
                checks,
                thresholds = new { samples = CalcSamples, p50MaxMs = CalcP50MaxMs, p95MaxMs = CalcP95MaxMs, profile = runtime.Config.DetectSla().Profile },
                calcLatency = new { total, compute },
                tokens = new { before, after },
                latest,
                cases = caseResults,
                errors = errorResults
            }, pass);
        }

        /// <summary>
        /// E3 (headless part): duration parser, /timer 250ms -> list ACTIVE -> EXPIRED within ~400 ms (snapshot + list),
        /// /timer 60s ToCancel -> cancel all -> 0 active, no timer object left, idle CPU, 0 tokens. The Passive HUD /
        /// foreground check runs against the live HostService in Verify-Task14.ps1 (pipe TIMERALERT).
        /// </summary>
        private static (object, bool) LocalTimer(NexusRuntime runtime)
        {
            runtime.WarmUp();
            var processor = new CockpitCommandProcessor(runtime);
            LocalTimerManager timers = runtime.LocalUtilities.Timers;
            TokenLedger before = TokenLedger.Read(runtime);
            processor.Execute("/timer cancel all", IntPtr.Zero);
            int activeAtStart = timers.ActiveCount;

            var parse = new (string Text, long? Ms)[]
            {
                ("250ms", 250), ("90s", 90_000), ("5m", 300_000), ("1h30m", 5_400_000), ("2m30s", 150_000), ("60", 60_000),
                ("1h2m3s400ms", 3_723_400), ("", null), ("abc", null), ("5x", null), ("30m1h", null), ("0", null), ("25h", null), ("5m5m", null)
            };
            var parseResults = new List<object>();
            bool parseOk = true;
            foreach (var p in parse)
            {
                bool okParse = LocalTimerManager.TryParseDuration(p.Text, out long ms, out string? error);
                bool ok = p.Ms == null ? !okParse && error != null : okParse && ms == p.Ms;
                parseOk &= ok;
                parseResults.Add(new { text = p.Text, expectedMs = p.Ms, parsed = okParse ? ms : (long?)null, ok, error });
            }

            // 250 ms timer: ACTIVE in /timer list, then EXPIRED.
            const string Label = "Kiểm thử tự động Gate 14";
            LocalTimerInfo? fired = null;
            string? firedFileError = null;
            using var expired = new ManualResetEventSlim(false);
            void OnExpired(LocalTimerInfo t, LatestDispatchSnapshot _, string? fileError)
            {
                fired = t;
                firedFileError = fileError;
                expired.Set();
            }
            runtime.LocalUtilities.TimerExpired += OnExpired;
            IntPtr foregroundBefore = ForegroundWindowInfo.Current();
            var sw = Stopwatch.StartNew();
            CockpitCommandResult start = processor.Execute($"/timer 250ms {Label}", IntPtr.Zero);
            CockpitCommandResult listActive = processor.Execute("/timer list", IntPtr.Zero);
            int activeAfterStart = timers.ActiveCount;
            bool firedInTime = expired.Wait(TimeSpan.FromMilliseconds(400));
            double firedAfterMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            if (!firedInTime) expired.Wait(TimeSpan.FromSeconds(2)); // report how late it was instead of hanging
            runtime.LocalUtilities.TimerExpired -= OnExpired;
            IntPtr foregroundAfter = ForegroundWindowInfo.Current();
            Thread.Sleep(30); // let the EXPIRED write land before reading the file
            object latestExpired = CheckLocalLatest(runtime, LatestDispatchFile.TimerExpiredEvent, LocalUtilityService.Timer, "EXPIRED", out bool latestExpiredOk);
            LatestDispatchSnapshot? expiredSnapshot = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            CockpitCommandResult listAfter = processor.Execute("/timer list", IntPtr.Zero);
            var (activeNow, recent) = timers.List();
            string expectedMessage = fired == null ? "(not fired)" : LocalUtilityService.ExpiredMessage(fired);

            bool startOk = start.Ok && start.Kind == LocalUtilityService.Timer && start.Message.StartsWith("[TIMER #", StringComparison.Ordinal);
            bool listActiveOk = listActive.Ok && listActive.Message.Contains("1 đang chạy (ACTIVE)", StringComparison.Ordinal) &&
                                listActive.Message.Contains(Label, StringComparison.Ordinal) && activeAfterStart == 1;
            bool expiredOk = firedInTime && fired != null && fired.State == "EXPIRED" && fired.Label == Label && firedFileError == null &&
                             latestExpiredOk && expiredSnapshot?.AiResponse == expectedMessage &&
                             expectedMessage == $"[TIMER EXPIRED] #{fired.Id}: {Label} (250ms)";
            bool listAfterOk = listAfter.Ok && activeNow.Count == 0 && recent.Count > 0 && recent[0].State == "EXPIRED" &&
                               listAfter.Message.Contains("EXPIRED", StringComparison.Ordinal);
            bool foregroundUnchanged = foregroundBefore == foregroundAfter;

            // 60 s timer -> cancel all -> empty list; aliases and errors.
            CockpitCommandResult toCancel = processor.Execute("/timer 60s ToCancel", IntPtr.Zero);
            int activeBeforeCancel = timers.ActiveCount;
            CockpitCommandResult cancelAll = processor.Execute("/timer cancel all", IntPtr.Zero);
            CockpitCommandResult listEmpty = processor.Execute("/timer list", IntPtr.Zero);
            CockpitCommandResult aliasTimers = processor.Execute("/timers", IntPtr.Zero);
            processor.Execute("/timer 2m30s AliasStop", IntPtr.Zero);
            CockpitCommandResult stopAll = processor.Execute("/timer stop all", IntPtr.Zero);
            processor.Execute("/timer 60 AliasClear", IntPtr.Zero);
            CockpitCommandResult clear = processor.Execute("/timer clear", IntPtr.Zero);
            CockpitCommandResult badDuration = processor.Execute("/timer 5x Sai", IntPtr.Zero);
            CockpitCommandResult badCancel = processor.Execute("/timer cancel 99999", IntPtr.Zero);
            int activeAtEnd = timers.ActiveCount;

            bool cancelOk = toCancel.Ok && activeBeforeCancel == 1 && cancelAll.Ok && cancelAll.Message.Contains("Đã hủy 1", StringComparison.Ordinal) &&
                            listEmpty.Ok && listEmpty.Message.StartsWith("[TIMER] 0 bộ đếm đang chạy.", StringComparison.Ordinal) &&
                            aliasTimers.Ok && stopAll.Ok && stopAll.Message.Contains("Đã hủy 1", StringComparison.Ordinal) &&
                            clear.Ok && clear.Message.Contains("Đã hủy 1", StringComparison.Ordinal) &&
                            !badDuration.Ok && !badCancel.Ok && activeAtEnd == 0;

            // 0 % idle: no timer object exists, so this process does no work for 1 s.
            using Process self = Process.GetCurrentProcess();
            self.Refresh();
            TimeSpan cpu0 = self.TotalProcessorTime;
            var cpuWatch = Stopwatch.StartNew();
            Thread.Sleep(1000);
            self.Refresh();
            double idleCpuPct = Math.Round((self.TotalProcessorTime - cpu0).TotalMilliseconds / (cpuWatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0, 3);
            bool idleOk = activeAtEnd == 0 && idleCpuPct < 1.0;

            TokenLedger after = TokenLedger.Read(runtime);
            bool zeroTokens = before.SameTokens(after) && after.DispatchRows == before.DispatchRows;

            var checks = new { parseOk, startOk, listActiveOk, expiredOk, listAfterOk, foregroundUnchanged, cancelOk, idleOk, zeroTokens, activeAtStartZero = activeAtStart == 0 };
            bool pass = parseOk && startOk && listActiveOk && expiredOk && listAfterOk && foregroundUnchanged && cancelOk && idleOk && zeroTokens && activeAtStart == 0;
            return (new
            {
                scenario = "local-timer",
                pass,
                checks,
                timer250 = new
                {
                    start = start.Message,
                    listWhileActive = listActive.Message,
                    firedInTime,
                    firedAfterMs,
                    waitBudgetMs = 400,
                    fired,
                    expectedMessage,
                    latestExpired,
                    listAfter = listAfter.Message,
                    foregroundBefore = $"0x{foregroundBefore.ToInt64():X}",
                    foregroundAfter = $"0x{foregroundAfter.ToInt64():X}"
                },
                cancel = new
                {
                    toCancel = toCancel.Message,
                    cancelAll = cancelAll.Message,
                    listEmpty = listEmpty.Message,
                    aliasTimers = aliasTimers.Message,
                    stopAll = stopAll.Message,
                    clear = clear.Message,
                    badDuration = badDuration.Message,
                    badCancel = badCancel.Message,
                    activeAtEnd
                },
                idle = new { activeTimerObjects = activeAtEnd, idleCpuPct, sampleMs = 1000 },
                tokens = new { before, after },
                parse = parseResults
            }, pass);
        }

        /// <summary>
        /// E4: /note write (UTF-8 no BOM) -> /note list -> /note find 49152; Secret Guard (dummy key never reaches the file,
        /// SQLite or latest_dispatch.json); /ground (mock) right after the note returns the note without a restart.
        /// The test notes are removed again at the end (the file is git-ignored either way).
        /// </summary>
        private static (object, bool) LocalNote(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "local-note", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set (E4 /ground runs in mock mode)." }, false);

            runtime.WarmUp();
            runtime.QuotaGuard.ResetSlidingWindows();
            var processor = new CockpitCommandProcessor(runtime);
            LocalNoteService notes = runtime.LocalUtilities.Notes;
            string path = notes.NotePath;
            bool existedBefore = File.Exists(path);
            int countBefore = notes.Count();
            int gateNotesBefore = notes.Find("49152", out int matchesBefore).Count(e => e.Text == GateNoteText);
            var createdIds = new List<int>();

            // Warm the grounding memo with the same query first: the note must still show up (memo invalidated by /note).
            VaultGroundingResult beforeNote = runtime.Grounding.Search(GateGroundQuery);

            CockpitCommandResult write = processor.Execute($"/note {GateNoteText}", IntPtr.Zero);
            LocalNoteEntry? newest = notes.List(1).FirstOrDefault();
            if (write.Ok && newest != null && newest.Text == GateNoteText) createdIds.Add(newest.Id);
            byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            string content = Encoding.UTF8.GetString(bytes);
            bool fileNoBom = bytes.Length > 0 && NoBom(path);
            bool lineWritten = newest != null && content.Contains($"- [{newest.TimestampUtc}] {GateNoteText}\n", StringComparison.Ordinal) &&
                               newest.TimestampUtc.EndsWith(" UTC", StringComparison.Ordinal) && newest.TimestampUtc.Length == 23;
            object latestNote = CheckLocalLatest(runtime, LatestDispatchFile.LocalUtilityEvent, LocalUtilityService.Note, "PASSED", out bool latestNoteOk);

            CockpitCommandResult list = processor.Execute("/note list", IntPtr.Zero);
            CockpitCommandResult find = processor.Execute("/note find 49152", IntPtr.Zero);
            CockpitCommandResult search = processor.Execute("/note search 49152", IntPtr.Zero);
            bool writeOk = write.Ok && write.Kind == LocalUtilityService.Note && createdIds.Count == 1 &&
                           write.Message.StartsWith($"[NOTE #{createdIds[0]}]", StringComparison.Ordinal);
            bool listOk = list.Ok && list.Message.Contains(GateNoteText, StringComparison.Ordinal);
            bool findOk = find.Ok && find.Message.Contains(GateNoteText, StringComparison.Ordinal) &&
                          search.Ok && search.Message.Contains(GateNoteText, StringComparison.Ordinal);

            // Secret Guard: Cockpit path (top-level key guard) and the service path MCP uses; plus a non-Google secret shape.
            string dummy = DummyGoogleKey;
            CockpitCommandResult blocked = processor.Execute($"/note Khóa thử nghiệm {dummy}", IntPtr.Zero);
            LocalUtilityResult blockedService = runtime.LocalUtilities.Execute(LocalUtilityService.Note, $"Khóa thử nghiệm {dummy}");
            string otherSecret = "gh" + "p_" + new string('A', 36);
            CockpitCommandResult blockedOther = processor.Execute($"/note token {otherSecret}", IntPtr.Zero);
            string afterBlock = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            string latestRaw = File.Exists(runtime.LatestDispatchPath) ? File.ReadAllText(runtime.LatestDispatchPath, Encoding.UTF8) : string.Empty;
            long sqliteHits = CountSqliteMentions(runtime, "SyDummySecretKeyForTest") + CountSqliteMentions(runtime, otherSecret);
            bool secretBlocked = !blocked.Ok && blocked.Kind == "blocked" && blocked.Message.Contains("Secret Guard", StringComparison.Ordinal) &&
                                 !blockedService.Ok && blockedService.Blocked && blockedService.Snapshot == null &&
                                 !blockedOther.Ok && blockedOther.Local?.Blocked == true &&
                                 !afterBlock.Contains("SyDummySecretKeyForTest", StringComparison.Ordinal) &&
                                 !afterBlock.Contains(otherSecret, StringComparison.Ordinal) &&
                                 !latestRaw.Contains("SyDummySecretKeyForTest", StringComparison.Ordinal) &&
                                 sqliteHits == 0 && notes.Count() == countBefore + 1;

            // /ground right after the note, same process, mock AI.
            CockpitCommandResult ground = processor.Execute($"/ground {GateGroundQuery}", IntPtr.Zero);
            ConsultOutcome? groundOutcome = Wait(ground.AiTask, 10);
            VaultGroundingResult? g = ground.GroundingResult;
            string? noteSource = g?.GroundingSources.FirstOrDefault(src => src.StartsWith($"[{LocalNoteService.RelativePath}#", StringComparison.Ordinal));
            bool groundOk = ground.Ok && ground.Kind == "ground" && g != null && noteSource != null &&
                            g.GroundedContextBlock.Contains("49152", StringComparison.Ordinal) &&
                            g.Sections.Count <= VaultGroundingEngine.MaxTopSections && g.TotalChars <= VaultGroundingEngine.MaxTotalAttachedChars &&
                            groundOutcome?.AiOk == true;
            VaultGroundingResult direct = runtime.Grounding.Search(GateGroundQuery); // what MCP search_local_vault returns in this process
            bool directOk = direct.GroundingSources.Any(src => src.StartsWith($"[{LocalNoteService.RelativePath}#", StringComparison.Ordinal));

            // Clean up the notes this test created.
            int removed = notes.RemoveNotes(createdIds);
            int countAfter = notes.Count();
            int gateNotesAfter = notes.Find("49152", out _).Count(e => e.Text == GateNoteText);
            bool cleanedUp = removed == createdIds.Count && countAfter == countBefore && gateNotesAfter == gateNotesBefore && NoBom(path);
            runtime.QuotaGuard.ResetSlidingWindows();

            var checks = new { writeOk, fileNoBom, lineWritten, latestNoteOk, listOk, findOk, secretBlocked, groundOk, directOk, cleanedUp };
            bool pass = writeOk && fileNoBom && lineWritten && latestNoteOk && listOk && findOk && secretBlocked && groundOk && directOk && cleanedUp;
            return (new
            {
                scenario = "local-note",
                pass,
                checks,
                file = new { relative = LocalNoteService.RelativePath, existedBefore, countBefore, countAfter, removed, noBom = fileNoBom, matchesBefore },
                write = new { write.Message, write.ElapsedMs, entry = newest },
                latestNote,
                list = list.Message,
                find = find.Message,
                secretGuard = new
                {
                    cockpit = blocked.Message,
                    service = blockedService.Message,
                    otherSecretShape = blockedOther.Message,
                    keyInNotesFile = afterBlock.Contains("SyDummySecretKeyForTest", StringComparison.Ordinal),
                    keyInLatestDispatch = latestRaw.Contains("SyDummySecretKeyForTest", StringComparison.Ordinal),
                    sqliteMentions = sqliteHits
                },
                ground = new
                {
                    ground.Message,
                    dispatchId = ground.Dispatch?.DispatchId,
                    cacheHit = ground.CacheHit,
                    sources = g?.GroundingSources,
                    noteSource,
                    totalChars = g?.TotalChars,
                    contextHasNote = g?.GroundedContextBlock.Contains("49152", StringComparison.Ordinal),
                    aiOk = groundOutcome?.AiOk,
                    sourcesBeforeNote = beforeNote.GroundingSources,
                    directSearchSources = direct.GroundingSources
                }
            }, pass);
        }

        /// <summary>Rows in audit_logs, agent_dispatch_logs and agent_memories whose text columns contain <paramref name="needle"/>.</summary>
        private static long CountSqliteMentions(NexusRuntime runtime, string needle)
        {
            using SqliteConnection connection = runtime.Store.OpenHeldConnection();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT (SELECT COUNT(*) FROM audit_logs WHERE instr(COALESCE(arguments_json,''), $n) > 0 OR instr(outcome, $n) > 0) + " +
                "(SELECT COUNT(*) FROM agent_dispatch_logs WHERE instr(COALESCE(task_summary,''), $n) > 0) + " +
                "(SELECT COUNT(*) FROM agent_memories WHERE instr(COALESCE(content,''), $n) > 0);";
            cmd.Parameters.AddWithValue("$n", needle);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
}
