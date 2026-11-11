using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-11 ─────────────────────────────

        // TASK-11b Hạng mục C: cache-hit latency is judged on N = 10 consecutive hits after 1 warm-up hit.
        private const int HitSamples = 10;
        private const double LookupP95MaxMs = 2.0;   // cache_lookup_ms P95
        private const double TotalP50MaxMs = 2.5;    // total incl. ADR-14 atomic write, median
        private const double TotalP95MaxMs = 4.0;    // total incl. ADR-14 atomic write, P95

        // TASK-14b decision (0b), ADR-55: under -FastRegression (NEXUS_RELAXED_SLA=1) a micro-latency (P50 / P95 in ms) above
        // its SLA but at or below RelaxedSlaFactor x SLA passes with an [SLA-WARN (FastRegression jitter)] entry. Every
        // functional, data-integrity, security and memory check stays hard. Default / -FullRegression: every SLA is hard.
        internal const string RelaxedSlaVariable = "NEXUS_RELAXED_SLA";
        private const double RelaxedSlaFactor = 2.5;
        private static readonly Dictionary<string, object> SlaWarningMap = new();

        private static bool RelaxedSla => Environment.GetEnvironmentVariable(RelaxedSlaVariable) == "1";

        /// <summary>Warnings collected in this selftest process (written into the report as slaWarnings).</summary>
        private static IReadOnlyList<object> SlaWarnings
        {
            get { lock (SlaWarningMap) return SlaWarningMap.Values.ToList(); }
        }

        /// <summary>measured &lt;= sla, or (relaxed mode) measured &lt;= RelaxedSlaFactor x sla with a warning. -1 (missing) always fails.</summary>
        private static bool WithinSla(string metric, double measuredMs, double slaMs)
        {
            if (measuredMs >= 0 && measuredMs <= slaMs) return true;
            double ceiling = Math.Round(slaMs * RelaxedSlaFactor, 3);
            if (!RelaxedSla || measuredMs < 0 || measuredMs > ceiling) return false;
            lock (SlaWarningMap)
            {
                SlaWarningMap[$"{metric}|{measuredMs}"] = new
                {
                    tag = "SLA-WARN (FastRegression jitter)",
                    metric,
                    measuredMs,
                    slaMs,
                    ceilingMs = ceiling
                };
            }
            return true;
        }

        /// <summary>
        /// Gate 16c decision 3.2 (2026-11-11): measured &lt;= warnMs passes; warnMs &lt; measured &lt;= hardMs passes with an
        /// [SLA-WARN] entry; above hardMs the usual rule applies to the hard limit (fails, or relaxed mode as <see cref="WithinSla"/>).
        /// </summary>
        private static bool WithinWarnSla(string metric, double measuredMs, double warnMs, double hardMs)
        {
            if (measuredMs >= 0 && measuredMs <= warnMs) return true;
            if (measuredMs >= 0 && measuredMs <= hardMs)
            {
                lock (SlaWarningMap)
                {
                    SlaWarningMap[$"{metric}|{measuredMs}"] = new
                    {
                        tag = "SLA-WARN (warn line, under the hard limit)",
                        metric,
                        measuredMs,
                        slaMs = warnMs,
                        ceilingMs = hardMs
                    };
                }
                return true;
            }
            return WithinSla(metric, measuredMs, hardMs);
        }

        /// <summary>Min / P50 / P95 / Max of a sample set; percentiles by linear interpolation between ranks (R-7, Excel PERCENTILE.INC).</summary>
        private sealed record LatencyStats(double[] Samples, double Min, double P50, double P95, double Max)
        {
            public static LatencyStats Of(IReadOnlyList<double> samples)
            {
                double[] raw = samples.Select(v => Math.Round(v, 3)).ToArray();
                if (raw.Length == 0) return new LatencyStats(raw, -1, -1, -1, -1);
                double[] sorted = raw.OrderBy(v => v).ToArray();
                double Pct(double p)
                {
                    double h = (sorted.Length - 1) * p;
                    int lo = (int)Math.Floor(h);
                    int hi = Math.Min(lo + 1, sorted.Length - 1);
                    return Math.Round(sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]), 3);
                }
                return new LatencyStats(raw, sorted[0], Pct(0.50), Pct(0.95), sorted[^1]);
            }
        }

        /// <summary>One cache-hit path measured on <see cref="HitSamples"/> hits: lookup (cache_lookup_ms) and total (SyncTiming).</summary>
        /// <remarks>TASK-11b profile: Steps = every step of the /ask or /ground hits (null for /qa); reporting only, not judged.</remarks>
        private sealed record HitLatency(LatencyStats Lookup, LatencyStats Total, int Samples, bool AllHits,
            Dictionary<string, LatencyStats>? Steps = null, string Path = "cache hit")
        {
            // Functional part (10 samples, every one a cache hit) is always hard; only the ms comparison may be relaxed.
            public bool LookupOk => Samples == HitSamples && AllHits && WithinSla($"{Path} cache_lookup_ms P95", Lookup.P95, LookupP95MaxMs);
            public bool TotalOk => Samples == HitSamples && AllHits &&
                                   WithinSla($"{Path} total P50", Total.P50, TotalP50MaxMs) &&
                                   WithinSla($"{Path} total P95", Total.P95, TotalP95MaxMs);
            public bool Ok => LookupOk && TotalOk;
        }

        /// <summary>Runs <paramref name="command"/> <see cref="HitSamples"/> times (index 0..9) and collects both timings of each cache hit.</summary>
        private static HitLatency MeasureHits(CockpitCommandProcessor processor, Func<int, string> command, List<CockpitCommandResult>? results = null)
        {
            string path = command(0).Split(' ', 2)[0];
            var lookup = new List<double>(HitSamples);
            var total = new List<double>(HitSamples);
            var hits = new List<CockpitCommandResult>(HitSamples);
            bool allHits = true;
            for (int i = 0; i < HitSamples; i++)
            {
                CockpitCommandResult r = processor.Execute(command(i), IntPtr.Zero);
                results?.Add(r);
                hits.Add(r);
                allHits &= r.Ok && r.CacheHit && r.Snapshot?.CacheLookupMs != null;
                lookup.Add(r.Snapshot?.CacheLookupMs ?? -1); // -1 = missing (JSON cannot hold NaN)
                total.Add(r.SyncTiming?.TotalSyncMs ?? r.ElapsedMs);
            }
            return new HitLatency(LatencyStats.Of(lookup), LatencyStats.Of(total), total.Count, allHits, CollectSteps(hits), path);
        }

        /// <summary>TASK-11b profile: per-step stats of /ask or /ground cache hits (null when no hit carries a profile).</summary>
        private static Dictionary<string, LatencyStats>? CollectSteps(IEnumerable<CockpitCommandResult> hits)
        {
            var steps = new SortedDictionary<string, List<double>>(StringComparer.Ordinal);
            void Add(string key, double v)
            {
                if (!steps.TryGetValue(key, out List<double>? list)) steps[key] = list = new List<double>(HitSamples);
                list.Add(v);
            }

            foreach (CockpitCommandResult r in hits)
            {
                ConsultCacheHitTimings? p = r.SyncTiming?.ConsultCache;
                if (p == null) continue;
                ConsultCacheTxTimings tx = p.Tx ?? new ConsultCacheTxTimings(-1, -1, -1, -1, -1, -1);
                Add("01_guard", p.GuardMs);
                Add("02_groundingSearch", p.GroundingSearchMs);
                Add("03_buildKeySha256", p.BuildKeyMs);
                Add("04_route", p.RouteMs);
                Add("05_tx_openAndBegin", tx.OpenAndBeginMs);
                Add("05_tx_cacheUpdateReturning", tx.CacheUpdateMs);
                Add("05_tx_savedSum", tx.SavedSumMs);
                Add("05_tx_historyJson", tx.HistoryJsonMs);
                Add("05_tx_insertDispatch", tx.InsertDispatchMs);
                Add("05_tx_commit", tx.CommitMs);
                Add("05_transactionTotal", p.TransactionMs);
                Add("06_snapshot", p.SnapshotMs);
                Add("07_serializeJson", p.SerializeMs);
                Add("08_tempFileWrite", p.TempWriteMs);
                Add("09_fileMove", p.FileMoveMs);
                double sum = p.GuardMs + p.GroundingSearchMs + p.BuildKeyMs + p.RouteMs + p.TransactionMs +
                             p.SnapshotMs + p.SerializeMs + p.TempWriteMs + p.FileMoveMs;
                Add("10_other", Math.Round(p.TotalSyncMs - sum, 3));
                Add("11_total", p.TotalSyncMs);
            }
            return steps.Count == 0 ? null : steps.ToDictionary(kv => kv.Key, kv => LatencyStats.Of(kv.Value));
        }

        /// <summary>
        /// TASK-11b Hạng mục B (ADR-14): a reader thread parses latest_dispatch.json in a loop while <paramref name="writes"/>
        /// cache hits replace it. With temp file + File.Move every read must parse (old or new file, never a partial one).
        /// Run apart from the latency samples so the reader does not compete with them.
        /// </summary>
        private static object CheckAtomicWrites(NexusRuntime runtime, CockpitCommandProcessor processor, string command, int writes, out bool ok)
        {
            string path = runtime.LatestDispatchPath;
            long reads = 0, failed = 0;
            using var stop = new System.Threading.CancellationTokenSource();
            var reader = new System.Threading.Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var doc = System.Text.Json.JsonDocument.Parse(stream);
                        if (doc.RootElement.GetProperty("dispatchId").GetInt64() <= 0) failed++;
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        failed++; // partial / truncated / invalid file
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // file briefly locked by the OS during the replace: not a torn read
                        continue;
                    }
                    reads++;
                }
            }) { IsBackground = true };
            reader.Start();
            int hits = 0;
            for (int i = 0; i < writes; i++)
            {
                if (processor.Execute(command, IntPtr.Zero).CacheHit) hits++;
            }
            stop.Cancel();
            reader.Join(2000);
            long totalReads = System.Threading.Interlocked.Read(ref reads);
            ok = hits == writes && totalReads > 0 && failed == 0;
            return new { writes, cacheHits = hits, concurrentReads = totalReads, tornOrInvalidReads = failed, pass = ok };
        }

        /// <summary>
        /// E1: gemini_response_cache table + columns present in SQLite WAL, 100% baseline rows preserved (&gt;= 1296).
        /// </summary>
        private static (object, bool) Schema11(NexusRuntime runtime)
        {
            SchemaMigrationReport migration = runtime.Store.MigrationReport;
            var cacheColumns = new List<string>();
            var dispatchColumns = new List<string>();
            string cacheTableSql = string.Empty;
            using (SqliteConnection connection = runtime.Store.OpenHeldConnection())
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM pragma_table_info('gemini_response_cache') ORDER BY cid;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) cacheColumns.Add(reader.GetString(0));
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'gemini_response_cache';";
                    cacheTableSql = Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM pragma_table_info('agent_dispatch_logs') ORDER BY cid;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) dispatchColumns.Add(reader.GetString(0));
                }
            }

            string[] requiredCacheCols =
            {
                "cache_key", "command_type", "model_id", "normalized_prompt",
                "grounding_fingerprint", "grounding_sources_json", "response_text",
                "base_prompt_tokens", "base_completion_tokens",
                "prompt_tokens_saved", "completion_tokens_saved", "hit_count",
                "created_utc", "last_hit_utc", "expires_utc"
            };
            List<string> missingCacheCols = requiredCacheCols.Where(c => !cacheColumns.Contains(c)).ToList();
            // TASK-11b Hạng mục A: live vs mock token columns on agent_dispatch_logs.
            string[] tokenSplitCols = { "live_prompt_tokens", "live_completion_tokens", "live_total_tokens", "mock_tokens_used" };
            List<string> missingTokenSplitCols = tokenSplitCols.Where(c => !dispatchColumns.Contains(c)).ToList();
            NexusConfig config = runtime.Config.Current;
            var (livePrompt, liveCompletion, liveTotal) = runtime.Store.GetDailyTokenUsage();
            long mockToday = runtime.Store.GetDailyMockTokens();
            long rows = runtime.Store.CountDispatchLogs();
            bool rowsPreserved = rows >= 1296 && migration.Error == null && migration.RowsAfter >= migration.RowsBefore;
            bool pass = missingCacheCols.Count == 0 && cacheTableSql.Length > 0 && rowsPreserved && missingTokenSplitCols.Count == 0;

            return (new
            {
                scenario = "schema11",
                pass,
                cacheTableExists = cacheTableSql.Length > 0,
                cacheColumns,
                missingCacheColumns = missingCacheCols,
                dispatchColumnsCount = dispatchColumns.Count,
                rows,
                baselineMinRows = 1296,
                rowsPreserved,
                defaultTtlSeconds = runtime.ResponseCache.DefaultTtlSeconds,
                missingTokenSplitColumns = missingTokenSplitCols,
                config = new { maxDailyTokens = config.MaxDailyTokens, geminiModel = config.GeminiModel, defaultModel = GeminiAgentClient.FallbackModel },
                tokensToday = new { livePrompt, liveCompletion, liveTotal, mock = mockToday },
                migrationThisProcess = new { migration.AddedColumns, migration.RowsBefore, migration.RowsAfter, migration.Rebuilt, migration.Error }
            }, pass);
        }

        /// <summary>
        /// E2 (ADR-44): Cache Miss -> Cache Hit (&lt; 2 ms, 0 additional tokens deducted, cumulative saved_total_tokens)
        /// and Cache Bypass (/ask!, /ask --fresh, /f).
        /// </summary>
        private static (object, bool) CacheHitAndBypass(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "cache-hit-bypass", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);

            string? previousQaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
            try
            {
                runtime.WarmUp();
                runtime.ResponseCache.Clear();
                runtime.QuotaGuard.ResetSlidingWindows();
                var processor = new CockpitCommandProcessor(runtime);
                NexusConfig cfg = runtime.Config.Current;

                const string Prompt = "Thiết kế bộ nhớ đệm phản hồi xác định trên SQLite WAL (Gate 11 E2)";

                // 1. First /ask -> Cache Miss (populates gemini_response_cache)
                CockpitCommandResult miss = processor.Execute($"/ask {Prompt}", IntPtr.Zero);
                ConsultOutcome? missOutcome = Wait(miss.AiTask, 10);
                long missId = miss.Dispatch?.DispatchId ?? 0;
                RecentDispatch? missRow = missId > 0 ? runtime.Store.GetDispatch(missId) : null;
                LatestDispatchSnapshot? latestMiss = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

                GeminiUsageStats quotaAfterMiss = runtime.QuotaGuard.GetStats(cfg, runtime.Store);
                int rpmAfterMiss = quotaAfterMiss.CurrentRpm;
                int tpmAfterMiss = quotaAfterMiss.CurrentTpm;
                long dailyTokensAfterMiss = quotaAfterMiss.DailyTotalTokens;

                // 2. Second /ask (identical prompt, whitespace variation to prove normalization) -> Cache Hit #1 (< 2 ms, 0 tokens deducted)
                CockpitCommandResult hit1 = processor.Execute($"/ask   {Prompt}  ", IntPtr.Zero);
                long hit1Id = hit1.Dispatch?.DispatchId ?? 0;
                RecentDispatch? hit1Row = hit1Id > 0 ? runtime.Store.GetDispatch(hit1Id) : null;
                LatestDispatchSnapshot? latestHit1 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

                GeminiUsageStats quotaAfterHit1 = runtime.QuotaGuard.GetStats(cfg, runtime.Store);
                int rpmAfterHit1 = quotaAfterHit1.CurrentRpm;
                int tpmAfterHit1 = quotaAfterHit1.CurrentTpm;
                long dailyTokensAfterHit1 = quotaAfterHit1.DailyTotalTokens;

                // 3. Third /ask (identical prompt) -> Cache Hit #2 (cumulative saved_total_tokens = 2x)
                CockpitCommandResult hit2 = processor.Execute($"/ask {Prompt}", IntPtr.Zero);
                long hit2Id = hit2.Dispatch?.DispatchId ?? 0;
                RecentDispatch? hit2Row = hit2Id > 0 ? runtime.Store.GetDispatch(hit2Id) : null;
                LatestDispatchSnapshot? latestHit2 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                GeminiCacheStats statsAfterAskHits = runtime.ResponseCache.GetStats();

                // TASK-11b Hạng mục C: hit #1 = warm-up, hit #2 = saved-token check; the next 10 hits are the latency samples.
                var askSamples = new List<CockpitCommandResult>(HitSamples);
                HitLatency askLatency = MeasureHits(processor, _ => $"/ask {Prompt}", askSamples);

                // 4. /qa cache Miss -> /qa cache Hit (< 2 ms)
                SetQaMock("PASSED: Kiểm định ADR-44 cache phản hồi đạt chuẩn Gate 11 E2.");
                CockpitCommandResult qaMiss = processor.Execute($"/qa #{missId}", IntPtr.Zero);
                QaOutcome? qaMissOutcome = Wait(qaMiss.QaTask, 10);
                long dailyAfterQaMiss = runtime.QuotaGuard.GetStats(cfg, runtime.Store).DailyTotalTokens;

                // Run /qa on hit1Id (same task & AI response -> /qa cache hit). This is the /qa warm-up hit; the 10 latency
                // samples then review the 10 sampled /ask dispatches (same content -> same QA cache key).
                CockpitCommandResult qaHit = processor.Execute($"/qa #{hit1Id}", IntPtr.Zero);
                RecentDispatch? hit1AfterQa = runtime.Store.GetDispatch(hit1Id);
                LatestDispatchSnapshot? latestQaHit = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                long dailyAfterQaHit = runtime.QuotaGuard.GetStats(cfg, runtime.Store).DailyTotalTokens;
                long[] qaSampleIds = askSamples.Select(r => r.Dispatch?.DispatchId ?? 0).ToArray();
                HitLatency qaLatency = MeasureHits(processor, i => $"/qa #{qaSampleIds[i]}");
                bool qaSamplesPassed = qaSampleIds.All(id => id > 0 && runtime.Store.GetDispatch(id)?.Status == "PASSED");

                // ADR-14 atomicity under a concurrent reader (not part of the latency samples).
                object atomicity = CheckAtomicWrites(runtime, processor, $"/ask {Prompt}", 40, out bool atomicOk);

                // 5. Cache Bypass via /ask! and /ask --fresh and /f
                CockpitCommandResult bypassBang = processor.Execute($"/ask! {Prompt}", IntPtr.Zero);
                ConsultOutcome? bypassBangOutcome = Wait(bypassBang.AiTask, 10);
                long bypassBangId = bypassBang.Dispatch?.DispatchId ?? 0;
                RecentDispatch? bypassBangRow = bypassBangId > 0 ? runtime.Store.GetDispatch(bypassBangId) : null;

                CockpitCommandResult bypassFresh = processor.Execute($"/ask --fresh {Prompt}", IntPtr.Zero);
                ConsultOutcome? bypassFreshOutcome = Wait(bypassFresh.AiTask, 10);
                long bypassFreshId = bypassFresh.Dispatch?.DispatchId ?? 0;
                RecentDispatch? bypassFreshRow = bypassFreshId > 0 ? runtime.Store.GetDispatch(bypassFreshId) : null;

                CockpitCommandResult follow = processor.Execute($"/f #{missId} Bổ sung kiểm tra cửa sổ trượt ADR-43 luôn gọi trực tiếp API", IntPtr.Zero);
                RefineOutcome? followOutcome = Wait(follow.RefineTask, 10);
                RecentDispatch? afterFollow = runtime.Store.GetDispatch(missId);

                double hit1SyncMs = hit1.SyncTiming?.TotalSyncMs ?? hit1.ElapsedMs;
                double hit2SyncMs = hit2.SyncTiming?.TotalSyncMs ?? hit2.ElapsedMs;
                double qaHitSyncMs = qaHit.SyncTiming?.TotalSyncMs ?? qaHit.ElapsedMs;

                int singlePromptTokens = missRow?.PromptTokens ?? 0;
                int singleCompletionTokens = missRow?.CompletionTokens ?? 0;
                int singleTotalTokens = missRow?.TotalTokens ?? 0;

                var checks = new
                {
                    missExecutedApi = miss.Ok && !miss.CacheHit && missOutcome?.AiOk == true && singleTotalTokens > 0 && latestMiss?.CacheHit == false,
                    hit1FlaggedAndNoTask = hit1.Ok && hit1.CacheHit && hit1Row != null && hit1Row.AiResponse == missRow?.AiResponse,
                    askHitLatencyP50P95 = askLatency.Ok,
                    zeroTokensDeductedOnHit = hit1Row != null && hit1Row.PromptTokens == 0 && hit1Row.CompletionTokens == 0 && hit1Row.TotalTokens == 0 &&
                                              hit2Row != null && hit2Row.TotalTokens == 0,
                    quotaGuardUnchangedOnHit = rpmAfterHit1 == rpmAfterMiss && tpmAfterHit1 == tpmAfterMiss && dailyTokensAfterHit1 == dailyTokensAfterMiss,
                    savedTokensAccumulatedOnHit1 = latestHit1 != null && latestHit1.CacheHit &&
                                                   latestHit1.PromptTokensSaved == singlePromptTokens &&
                                                   latestHit1.CompletionTokensSaved == singleCompletionTokens &&
                                                   latestHit1.SavedTotalTokens == singleTotalTokens,
                    savedTokensAccumulatedOnHit2 = latestHit2 != null && latestHit2.CacheHit &&
                                                   latestHit2.PromptTokensSaved == singlePromptTokens * 2 &&
                                                   latestHit2.CompletionTokensSaved == singleCompletionTokens * 2 &&
                                                   latestHit2.SavedTotalTokens == singleTotalTokens * 2 &&
                                                   statsAfterAskHits.SavedTotalTokens == singleTotalTokens * 2 &&
                                                   statsAfterAskHits.TotalHits == 2,
                    qaCacheHitOk = qaMissOutcome?.Reviewed == true && qaHit.Ok && qaHit.CacheHit &&
                                   hit1AfterQa?.Status == "PASSED" && dailyAfterQaHit == dailyAfterQaMiss &&
                                   latestQaHit != null && latestQaHit.CacheHit && latestQaHit.SavedTotalTokens > 0 && qaSamplesPassed,
                    qaHitLatencyP50P95 = qaLatency.Ok,
                    adr14AtomicUnderConcurrentReader = atomicOk,
                    bypassAskBangCallsApi = bypassBang.Ok && !bypassBang.CacheHit && bypassBang.AiTask != null &&
                                            bypassBangOutcome?.AiOk == true && bypassBangRow != null && bypassBangRow.TotalTokens > 0,
                    bypassAskFreshCallsApi = bypassFresh.Ok && !bypassFresh.CacheHit && bypassFresh.AiTask != null &&
                                             bypassFreshOutcome?.AiOk == true && bypassFreshRow != null && bypassFreshRow.TotalTokens > 0,
                    followUpAlwaysCallsApi = follow.Ok && !follow.CacheHit && follow.RefineTask != null &&
                                             followOutcome?.AiOk == true && afterFollow != null && afterFollow.TurnCount == 2 &&
                                             afterFollow.TotalTokens > (missRow?.TotalTokens ?? 0)
                };

                runtime.QuotaGuard.ResetSlidingWindows();
                bool pass = checks.missExecutedApi && checks.hit1FlaggedAndNoTask && checks.askHitLatencyP50P95 &&
                            checks.zeroTokensDeductedOnHit && checks.quotaGuardUnchangedOnHit &&
                            checks.savedTokensAccumulatedOnHit1 && checks.savedTokensAccumulatedOnHit2 &&
                            checks.qaCacheHitOk && checks.qaHitLatencyP50P95 && checks.adr14AtomicUnderConcurrentReader &&
                            checks.bypassAskBangCallsApi && checks.bypassAskFreshCallsApi && checks.followUpAlwaysCallsApi;

                return (new
                {
                    scenario = "cache-hit-bypass",
                    pass,
                    checks,
                    thresholds = new { samples = HitSamples, lookupP95MaxMs = LookupP95MaxMs, totalP50MaxMs = TotalP50MaxMs, totalP95MaxMs = TotalP95MaxMs },
                    askLatency,
                    qaLatency,
                    atomicity,
                    miss = new
                    {
                        dispatchId = missId,
                        cacheHit = miss.CacheHit,
                        elapsedMs = miss.ElapsedMs,
                        promptTokens = missRow?.PromptTokens,
                        completionTokens = missRow?.CompletionTokens,
                        totalTokens = missRow?.TotalTokens
                    },
                    hit1 = new
                    {
                        dispatchId = hit1Id,
                        cacheHit = hit1.CacheHit,
                        elapsedMs = hit1.ElapsedMs,
                        syncTotalMs = hit1SyncMs,
                        tokensDeducted = hit1Row?.TotalTokens,
                        promptTokensSaved = latestHit1?.PromptTokensSaved,
                        completionTokensSaved = latestHit1?.CompletionTokensSaved,
                        savedTotalTokens = latestHit1?.SavedTotalTokens,
                        rpmBefore = rpmAfterMiss,
                        rpmAfter = rpmAfterHit1,
                        dailyTokensBefore = dailyTokensAfterMiss,
                        dailyTokensAfter = dailyTokensAfterHit1
                    },
                    hit2 = new
                    {
                        dispatchId = hit2Id,
                        cacheHit = hit2.CacheHit,
                        elapsedMs = hit2.ElapsedMs,
                        syncTotalMs = hit2SyncMs,
                        tokensDeducted = hit2Row?.TotalTokens,
                        promptTokensSaved = latestHit2?.PromptTokensSaved,
                        completionTokensSaved = latestHit2?.CompletionTokensSaved,
                        savedTotalTokens = latestHit2?.SavedTotalTokens
                    },
                    qaHit = new
                    {
                        dispatchId = hit1Id,
                        cacheHit = qaHit.CacheHit,
                        elapsedMs = qaHit.ElapsedMs,
                        syncTotalMs = qaHitSyncMs,
                        syncTiming = qaHit.SyncTiming,
                        status = hit1AfterQa?.Status,
                        savedTotalTokens = latestQaHit?.SavedTotalTokens
                    },
                    bypass = new
                    {
                        askBang = new { dispatchId = bypassBangId, cacheHit = bypassBang.CacheHit, totalTokens = bypassBangRow?.TotalTokens },
                        askFresh = new { dispatchId = bypassFreshId, cacheHit = bypassFresh.CacheHit, totalTokens = bypassFreshRow?.TotalTokens },
                        followUp = new { dispatchId = missId, cacheHit = follow.CacheHit, turnCount = afterFollow?.TurnCount, totalTokens = afterFollow?.TotalTokens }
                    },
                    cacheStats = runtime.ResponseCache.GetStats()
                }, pass);
            }
            finally
            {
                SetQaMock(previousQaMock);
            }
        }

        /// <summary>
        /// E3 (ADR-45): VaultGroundingEngine extracts relevant sections from 03_Local_Data_Vault/,
        /// enforces &lt;= 3 sections and &lt;= 1500 attached chars, records [file#section] in grounding_sources,
        /// and supports /ground (&amp; alias /g) with cache hit &lt; 2 ms.
        /// </summary>
        private static (object, bool) VaultGroundingTest(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "vault-grounding", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);

            runtime.WarmUp();
            runtime.ResponseCache.Clear();
            runtime.QuotaGuard.ResetSlidingWindows();
            var processor = new CockpitCommandProcessor(runtime);

            // 1. Direct VaultGroundingEngine extraction check (cold + warm)
            const string SearchQuery = "ADR-44 gemini_response_cache VaultGroundingEngine Gate 11";
            VaultGroundingResult directCold = runtime.Grounding.Search(SearchQuery);
            VaultGroundingResult directWarm = runtime.Grounding.Search(SearchQuery);

            bool sectionsBounded = directCold.Sections.Count >= 1 && directCold.Sections.Count <= VaultGroundingEngine.MaxTopSections;
            bool charsBounded = directCold.TotalChars > 0 && directCold.TotalChars <= VaultGroundingEngine.MaxTotalAttachedChars;
            bool sourcesFormatted = directCold.GroundingSources.Count == directCold.Sections.Count &&
                                    directCold.GroundingSources.All(s => s.StartsWith("[", StringComparison.Ordinal) &&
                                                                         s.Contains("#", StringComparison.Ordinal) &&
                                                                         s.EndsWith("]", StringComparison.Ordinal));
            // Gate 11b: gate<N>_handoff.md files are vault docs too and now rank first for "ADR-44 ... Gate 11".
            bool foundVaultDoc = directCold.GroundingSources.Any(s =>
                s.Contains("_handoff.md", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("current_task.md", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("claude_shift_handover.md", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("qa_report.md", StringComparison.OrdinalIgnoreCase));
            bool searchUnder5Ms = directWarm.ElapsedMs < 5.0;

            // 2. /ground <query> -> Cache Miss with selective vault context attached
            const string GroundQuery = "Tóm tắt kiến trúc ADR-44 gemini_response_cache và ADR-45 VaultGroundingEngine trong current_task.md";
            CockpitCommandResult ground1 = processor.Execute($"/ground {GroundQuery}", IntPtr.Zero);
            ConsultOutcome? g1Outcome = Wait(ground1.AiTask, 10);
            long g1Id = ground1.Dispatch?.DispatchId ?? 0;
            RecentDispatch? g1Row = g1Id > 0 ? runtime.Store.GetDispatch(g1Id) : null;
            LatestDispatchSnapshot? latestG1 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

            // 3. /g <query> (alias + identical query + identical grounding fingerprint) -> Cache Hit in < 2 ms
            CockpitCommandResult ground2 = processor.Execute($"/g {GroundQuery}", IntPtr.Zero);
            long g2Id = ground2.Dispatch?.DispatchId ?? 0;
            RecentDispatch? g2Row = g2Id > 0 ? runtime.Store.GetDispatch(g2Id) : null;
            LatestDispatchSnapshot? latestG2 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);

            double g2SyncMs = ground2.SyncTiming?.TotalSyncMs ?? ground2.ElapsedMs;
            // TASK-11b Hạng mục C: ground2 was the warm-up hit; 10 more identical /g hits are the latency samples.
            HitLatency groundLatency = MeasureHits(processor, _ => $"/g {GroundQuery}");
            var checks = new
            {
                sectionsBounded,
                charsBounded,
                sourcesFormatted,
                foundVaultDoc,
                searchUnder5Ms,
                groundMissOk = ground1.Ok && ground1.Kind == "ground" && !ground1.CacheHit &&
                               g1Outcome?.AiOk == true && g1Row != null && g1Row.TotalTokens > 0 &&
                               ground1.GroundingResult != null && ground1.GroundingResult.GroundingSources.Count >= 1 &&
                               ground1.GroundingResult.TotalChars <= VaultGroundingEngine.MaxTotalAttachedChars,
                latestG1HasSources = latestG1 != null && latestG1.GroundingSources != null &&
                                     latestG1.GroundingSources.Count >= 1 &&
                                     latestG1.GroundingSources.Count <= VaultGroundingEngine.MaxTopSections,
                groundHitLatencyP50P95 = groundLatency.Ok,
                groundAliasHitOk = ground2.Ok && ground2.Kind == "ground" && ground2.CacheHit &&
                                         g2Row != null && g2Row.TotalTokens == 0 &&
                                         latestG2 != null && latestG2.CacheHit &&
                                         latestG2.SavedTotalTokens > 0 &&
                                         latestG2.GroundingSources != null &&
                                         latestG2.GroundingSources.SequenceEqual(latestG1!.GroundingSources!)
            };

            runtime.QuotaGuard.ResetSlidingWindows();
            bool pass = checks.sectionsBounded && checks.charsBounded && checks.sourcesFormatted &&
                        checks.foundVaultDoc && checks.searchUnder5Ms && checks.groundMissOk &&
                        checks.latestG1HasSources && checks.groundAliasHitOk && checks.groundHitLatencyP50P95;

            return (new
            {
                scenario = "vault-grounding",
                pass,
                checks,
                thresholds = new { samples = HitSamples, lookupP95MaxMs = LookupP95MaxMs, totalP50MaxMs = TotalP50MaxMs, totalP95MaxMs = TotalP95MaxMs },
                groundLatency,
                directSearch = new
                {
                    coldElapsedMs = directCold.ElapsedMs,
                    warmElapsedMs = directWarm.ElapsedMs,
                    sectionCount = directCold.Sections.Count,
                    attachedContextChars = directCold.TotalChars,
                    maxContextChars = VaultGroundingEngine.MaxTotalAttachedChars,
                    groundingFingerprint = directCold.GroundingFingerprint,
                    sources = directCold.GroundingSources
                },
                groundMiss = new
                {
                    dispatchId = g1Id,
                    cacheHit = ground1.CacheHit,
                    elapsedMs = ground1.ElapsedMs,
                    totalTokens = g1Row?.TotalTokens,
                    groundingSources = latestG1?.GroundingSources
                },
                groundAliasHit = new
                {
                    dispatchId = g2Id,
                    cacheHit = ground2.CacheHit,
                    elapsedMs = ground2.ElapsedMs,
                    syncTotalMs = g2SyncMs,
                    tokensDeducted = g2Row?.TotalTokens,
                    savedTotalTokens = latestG2?.SavedTotalTokens,
                    groundingSources = latestG2?.GroundingSources
                }
            }, pass);
        }

        /// <summary>
        /// E4: /cache, /cache stats, /cache clear commands + audit log verification.
        /// Leaves 1 warm cached entry at the end so subsequent Named Pipe / MCP verification can inspect non-zero stats.
        /// </summary>
        private static (object, bool) CacheCommandsTest(NexusRuntime runtime)
        {
            if (!GeminiAgentClient.MockEnabled)
                return (new { scenario = "cache-commands", pass = false, error = $"{GeminiAgentClient.MockEnvironmentVariable} must be set." }, false);

            runtime.WarmUp();
            runtime.ResponseCache.Clear();
            runtime.QuotaGuard.ResetSlidingWindows();
            var processor = new CockpitCommandProcessor(runtime);

            const string SeedPrompt = "Kiểm thử lệnh /cache stats và /cache clear (Gate 11 E4)";
            CockpitCommandResult ask1 = processor.Execute($"/ask {SeedPrompt}", IntPtr.Zero);
            Wait(ask1.AiTask, 10);
            CockpitCommandResult ask2 = processor.Execute($"/ask {SeedPrompt}", IntPtr.Zero);

            CockpitCommandResult statsCmd = processor.Execute("/cache", IntPtr.Zero);
            CockpitCommandResult statsAliasCmd = processor.Execute("/cache stats", IntPtr.Zero);
            GeminiCacheStats? beforeClear = statsCmd.CacheStats;

            CockpitCommandResult clearCmd = processor.Execute("/cache clear", IntPtr.Zero);
            CockpitCommandResult statsAfterClearCmd = processor.Execute("/cache stats", IntPtr.Zero);
            GeminiCacheStats? afterClear = statsAfterClearCmd.CacheStats;

            // Re-seed one entry + one hit so subsequent MCP get_gemini_cache_stats sees active data
            CockpitCommandResult reseed1 = processor.Execute($"/ask {SeedPrompt}", IntPtr.Zero);
            Wait(reseed1.AiTask, 10);
            CockpitCommandResult reseed2 = processor.Execute($"/ask {SeedPrompt}", IntPtr.Zero);

            bool pass = ask1.Ok && ask2.Ok && ask2.CacheHit &&
                        statsCmd.Ok && statsCmd.Kind == "cache" &&
                        statsAliasCmd.Ok && statsAliasCmd.Kind == "cache" &&
                        beforeClear != null && beforeClear.TotalEntries >= 1 && beforeClear.TotalHits >= 1 && beforeClear.SavedTotalTokens > 0 &&
                        clearCmd.Ok && clearCmd.Kind == "cache" &&
                        afterClear != null && afterClear.TotalEntries == 0 && afterClear.TotalHits == 0 && afterClear.SavedTotalTokens == 0 &&
                        reseed2.CacheHit;

            runtime.QuotaGuard.ResetSlidingWindows();
            return (new
            {
                scenario = "cache-commands",
                pass,
                statsMessage = statsCmd.Message,
                beforeClear,
                clearMessage = clearCmd.Message,
                afterClear,
                reseededStats = runtime.ResponseCache.GetStats()
            }, pass);
        }

        /// <summary>E5 live /ground query (shared with the cache-hit-profile selftest so both search the same vault sections).</summary>
        private const string LiveGroundQuery =
            "Dựa trên tài liệu cục bộ về ADR-44 và ADR-45, hãy tóm tắt ngắn gọn trong 2 câu cơ chế GeminiResponseCache và VaultGroundingEngine của NEXUS Home Hub.";

        /// <summary>E3 mock /ground query (same text as in <see cref="VaultGroundingTest"/>).</summary>
        private const string ProfileShortGroundQuery =
            "Tóm tắt kiến trúc ADR-44 gemini_response_cache và ADR-45 VaultGroundingEngine trong current_task.md";

        private const string ProfileShortAnswer =
            "Gate 11 mock answer: Deterministic SQLite WAL Response Cache (ADR-44) + Selective Local Vault Grounding (ADR-45).";

        /// <summary>About 900 Vietnamese characters with 3 [file#section] citations, like the live gemini-3.5-flash-lite answer.</summary>
        private const string ProfileLongAnswer =
            "- **GeminiResponseCache (ADR-44)** lưu đệm các phản hồi từ Gemini vào bảng gemini_response_cache trong SQLite WAL cục bộ, " +
            "khóa bằng SHA-256 của model, câu hỏi đã chuẩn hóa và dấu vân tay ngữ cảnh, với thời hạn 3600 giây; khi trúng bộ đệm, " +
            "hệ thống trả lời trong vài mili giây, không trừ token của hạn mức ngày và cộng dồn số token tiết kiệm được vào saved_total_tokens " +
            "[handoff/qa_report.md#13. TASK-11: Bộ đệm phản hồi xác định GeminiResponseCache (ADR-44) & Địn].\n" +
            "- **VaultGroundingEngine (ADR-45)** quét các tệp Markdown và JSON trong 03_Local_Data_Vault, tách theo tiêu đề, chấm điểm " +
            "từ khóa một cách xác định rồi chỉ đính kèm tối đa ba phân đoạn liên quan nhất, tổng cộng không quá 1.500 ký tự, kèm nguồn dạng " +
            "[file#section] để câu trả lời có thể kiểm chứng và giữ lời nhắc gửi lên Gemini ngắn gọn " +
            "[handoff/gate11b_handoff.md#Việc Nhật Hòa làm trước khi chạy] [handoff/gate11_handoff.md#5. Lời nhắn dán cho Gemini Web].";

        private sealed record HitStepProfile(string Name, string Command, int AnswerChars, IReadOnlyList<string> GroundingSources,
            int LatestFileBytes, bool AllHits, LatencyStats CacheLookupMs, Dictionary<string, LatencyStats> Steps);

        /// <summary>One miss (mock), one warm-up hit, then <see cref="HitSamples"/> hits; returns every step of every hit.</summary>
        private static HitStepProfile ProfileHits(NexusRuntime runtime, CockpitCommandProcessor processor, string name, string command, string answer)
        {
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, answer);
            runtime.ResponseCache.Clear();
            runtime.QuotaGuard.ResetSlidingWindows();
            CockpitCommandResult miss = processor.Execute(command, IntPtr.Zero);
            ConsultOutcome? missOutcome = Wait(miss.AiTask, 10);
            processor.Execute(command, IntPtr.Zero); // warm-up hit
            HitLatency latency = MeasureHits(processor, _ => command);

            int fileBytes = File.Exists(runtime.LatestDispatchPath) ? (int)new FileInfo(runtime.LatestDispatchPath).Length : -1;
            return new HitStepProfile(name, command.Split(' ')[0], missOutcome?.AiResponse.Length ?? -1,
                missOutcome?.GroundingSources ?? Array.Empty<string>(), fileBytes,
                latency.AllHits && latency.Steps != null, latency.Lookup, latency.Steps ?? new Dictionary<string, LatencyStats>());
        }

        /// <summary>
        /// TASK-11b profile (Gemini Web, Gate 11b NEEDS_WORK): measures every step of the /ask and /ground cache-hit path with
        /// a short mock answer (like E2/E3) and a ~900-character answer + the live /ground query (like E5). No thresholds.
        /// </summary>
        private static (object, bool) CacheHitProfile(NexusRuntime runtime)
        {
            string? savedMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            try
            {
                runtime.WarmUp();
                var processor = new CockpitCommandProcessor(runtime);
                const string AskPrompt = "Gate 11b profile: tóm tắt cơ chế bộ đệm phản hồi ADR-44";
                var profiles = new[]
                {
                    ProfileHits(runtime, processor, "ask-short (E2-like)", $"/ask {AskPrompt}", ProfileShortAnswer),
                    ProfileHits(runtime, processor, "ask-long", $"/ask {AskPrompt}", ProfileLongAnswer),
                    ProfileHits(runtime, processor, "ground-short (E3-like)", $"/g {ProfileShortGroundQuery}", ProfileShortAnswer),
                    ProfileHits(runtime, processor, "ground-long (E5-like)", $"/ground {LiveGroundQuery}", ProfileLongAnswer)
                };
                runtime.ResponseCache.Clear();
                runtime.QuotaGuard.ResetSlidingWindows();
                bool pass = profiles.All(p => p.AllHits);
                return (new
                {
                    scenario = "cache-hit-profile",
                    pass,
                    hostServiceRunning = Process.GetProcessesByName("NEXUSHome.HostService").Length > 1,
                    samples = HitSamples,
                    profiles
                }, pass);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, savedMock);
            }
        }

        /// <summary>
        /// E5: Live Gemini E2E with real DPAPI key (live-e2e11):
        /// Call #1 (/ground) over network -> Call #2 identical (/ground) hits SQLite WAL cache in &lt; 2 ms with 0 additional tokens -> /qa PASSED.
        /// </summary>
        private static (object, bool) LiveE2e11(NexusRuntime runtime)
        {
            if (GeminiAgentClient.MockEnabled ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable)) ||
                GeminiAgentClient.GetSimulatedTransientFailures() > 0)
            {
                return (new { scenario = "live-e2e11", pass = false, error = "Mock/simulation variables must not be set for the live test." }, false);
            }

            ApiKeyResolution key = runtime.Vault.Resolve();
            if (key.Source == ApiKeySource.Missing)
            {
                return (new
                {
                    scenario = "live-e2e11",
                    pass = true,
                    skipped = true,
                    reason = "No live key configured (DPAPI / GEMINI_API_KEY).",
                    hint = "/apikey set <KEY> hoặc tools\\Set-NexusGeminiKey.ps1",
                    keyError = key.Error
                }, true);
            }

            runtime.WarmUp();
            runtime.ResponseCache.Clear();
            runtime.QuotaGuard.ResetSlidingWindows();
            var processor = new CockpitCommandProcessor(runtime);
            NexusConfig cfg = runtime.Config.Current;
            int wait = cfg.GeminiTimeoutSeconds + 15;
            var total = Stopwatch.StartNew();

            // Step 1: Call #1 over network (Cache Miss)
            CockpitCommandResult call1 = processor.Execute($"/ground {LiveGroundQuery}", IntPtr.Zero);
            ConsultOutcome? outcome1 = Wait(call1.AiTask, wait);
            long id1 = call1.Dispatch?.DispatchId ?? 0;
            RecentDispatch? row1 = id1 > 0 ? runtime.Store.GetDispatch(id1) : null;
            LatestDispatchSnapshot? latest1 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            GeminiUsageStats statsAfterCall1 = runtime.QuotaGuard.GetStats(cfg, runtime.Store);
            long dailyAfterCall1 = statsAfterCall1.DailyTotalTokens;
            int rpmAfterCall1 = statsAfterCall1.CurrentRpm;

            // Step 2: Call #2 identical (Cache Hit in < 2 ms, 0 tokens added)
            CockpitCommandResult call2 = processor.Execute($"/ground {LiveGroundQuery}", IntPtr.Zero);
            long id2 = call2.Dispatch?.DispatchId ?? 0;
            RecentDispatch? row2 = id2 > 0 ? runtime.Store.GetDispatch(id2) : null;
            LatestDispatchSnapshot? latest2 = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            GeminiUsageStats statsAfterCall2 = runtime.QuotaGuard.GetStats(cfg, runtime.Store);
            long dailyAfterCall2 = statsAfterCall2.DailyTotalTokens;
            int rpmAfterCall2 = statsAfterCall2.CurrentRpm;

            // TASK-11b Hạng mục C: call2 was the warm-up hit; 10 more identical /ground hits are the latency samples.
            HitLatency liveHitLatency = MeasureHits(processor, _ => $"/ground {LiveGroundQuery}");
            long dailyAfterSamples = runtime.QuotaGuard.GetStats(cfg, runtime.Store).DailyTotalTokens;
            long modelFallbackAudits = id1 > 0 ? CountAudit(runtime, "gemini_model_fallback", id1) : -1;

            // Step 3: Live /qa review on Call #1 (and if NEEDS_WORK, /retry --auto-qa to reach PASSED)
            bool retriedToPass = false;
            CockpitCommandResult qa = id1 > 0 ? processor.Execute($"/qa #{id1}", IntPtr.Zero) : call1;
            QaOutcome? review = Wait(qa.QaTask, wait);
            if (review != null && review.Reviewed && review.Verdict != "PASSED" && id1 > 0)
            {
                retriedToPass = true; // reported (TASK-11 review finding #6)
                CockpitCommandResult retry = processor.Execute($"/retry --auto-qa #{id1} Bổ sung đầy đủ tên bảng gemini_response_cache (TTL 3600s, < 2ms) và giới hạn Top-3 phân đoạn <= 1500 ký tự của VaultGroundingEngine.", IntPtr.Zero);
                Wait(retry.RefineTask, wait);
                review = Wait(retry.QaTask, wait);
            }
            total.Stop();

            RecentDispatch? final1 = id1 > 0 ? runtime.Store.GetDispatch(id1) : null;
            CockpitCommandResult cacheCmd = processor.Execute("/cache stats", IntPtr.Zero);
            CockpitCommandResult usageCmd = processor.Execute("/usage", IntPtr.Zero);

            double call2SyncMs = call2.SyncTiming?.TotalSyncMs ?? call2.ElapsedMs;
            bool call1Ok = call1.Ok && !call1.CacheHit && outcome1?.AiOk == true &&
                           outcome1.PromptTokens > 0 && outcome1.CompletionTokens > 0 && outcome1.TotalTokens > 0 &&
                           row1 != null && row1.TotalTokens > 0 &&
                           latest1?.GroundingSources != null && latest1.GroundingSources.Count >= 1;
            bool call2HitOk = call2.Ok && call2.CacheHit &&
                              row2 != null && row2.TotalTokens == 0 &&
                              dailyAfterSamples == dailyAfterCall1 &&
                              dailyAfterCall2 == dailyAfterCall1 && rpmAfterCall2 == rpmAfterCall1 &&
                              latest2 != null && latest2.CacheHit &&
                              latest2.SavedTotalTokens == (row1?.TotalTokens ?? -1) &&
                              latest2.PromptTokensSaved == (row1?.PromptTokens ?? -1) &&
                              latest2.CompletionTokensSaved == (row1?.CompletionTokens ?? -1);
            bool qaPassed = review?.Reviewed == true && final1 != null && final1.Status == "PASSED";
            // TASK-11b Hạng mục D: the configured model answers directly on the first call (no HTTP 404 -> fallback).
            bool modelDirect = outcome1 != null &&
                               outcome1.AiModel.StartsWith(cfg.GeminiModel, StringComparison.OrdinalIgnoreCase) &&
                               modelFallbackAudits == 0;

            bool pass = call1Ok && call2HitOk && liveHitLatency.Ok && qaPassed && modelDirect;
            return (new
            {
                scenario = "live-e2e11",
                pass,
                skipped = false,
                keySource = key.SourceLabel,
                configuredModel = cfg.GeminiModel,
                checks = new { call1Ok, call2HitOk, liveHitLatencyP50P95 = liveHitLatency.Ok, qaPassed, modelDirect },
                retriedToPass,
                modelFallbackAudits,
                thresholds = new { samples = HitSamples, lookupP95MaxMs = LookupP95MaxMs, totalP50MaxMs = TotalP50MaxMs, totalP95MaxMs = TotalP95MaxMs },
                liveHitLatency,
                call1_NetworkMiss = new
                {
                    dispatchId = id1,
                    cacheHit = call1.CacheHit,
                    model = outcome1?.AiModel,
                    aiLatencyMs = outcome1?.AiLatencyMs,
                    promptTokens = outcome1?.PromptTokens,
                    completionTokens = outcome1?.CompletionTokens,
                    totalTokens = outcome1?.TotalTokens,
                    groundingSources = latest1?.GroundingSources,
                    preview = outcome1 == null ? null : LatestDispatchFile.Shorten(outcome1.AiResponse, 220)
                },
                call2_CacheHit = new
                {
                    dispatchId = id2,
                    cacheHit = call2.CacheHit,
                    elapsedMs = call2.ElapsedMs,
                    syncTotalMs = call2SyncMs,
                    tokensDeducted = row2?.TotalTokens,
                    rpmBefore = rpmAfterCall1,
                    rpmAfter = rpmAfterCall2,
                    dailyTokensBefore = dailyAfterCall1,
                    dailyTokensAfter = dailyAfterCall2,
                    promptTokensSaved = latest2?.PromptTokensSaved,
                    completionTokensSaved = latest2?.CompletionTokensSaved,
                    savedTotalTokens = latest2?.SavedTotalTokens,
                    groundingSources = latest2?.GroundingSources
                },
                qa = new
                {
                    reviewed = review?.Reviewed,
                    verdict = review?.Verdict,
                    status = final1?.Status,
                    qaLatencyMs = review?.QaLatencyMs,
                    promptTokens = review?.PromptTokens,
                    completionTokens = review?.CompletionTokens,
                    totalTokens = review?.TotalTokens,
                    preview = review == null ? null : LatestDispatchFile.Shorten(review.QaResponse, 200)
                },
                cacheStats = cacheCmd.CacheStats,
                usageStats = usageCmd.UsageStats,
                totalMs = Math.Round(total.Elapsed.TotalMilliseconds, 1)
            }, pass);
        }
    }
}
