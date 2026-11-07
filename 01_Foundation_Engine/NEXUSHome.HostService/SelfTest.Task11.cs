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
            long rows = runtime.Store.CountDispatchLogs();
            bool rowsPreserved = rows >= 1296 && migration.Error == null && migration.RowsAfter >= migration.RowsBefore;
            bool pass = missingCacheCols.Count == 0 && cacheTableSql.Length > 0 && rowsPreserved;

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

                // 4. /qa cache Miss -> /qa cache Hit (< 2 ms)
                SetQaMock("PASSED: Kiểm định ADR-44 cache phản hồi đạt chuẩn Gate 11 E2.");
                CockpitCommandResult qaMiss = processor.Execute($"/qa #{missId}", IntPtr.Zero);
                QaOutcome? qaMissOutcome = Wait(qaMiss.QaTask, 10);
                long dailyAfterQaMiss = runtime.QuotaGuard.GetStats(cfg, runtime.Store).DailyTotalTokens;

                // Run /qa on hit1Id (which has the exact same task & AI response -> hits /qa cache in < 2 ms)
                CockpitCommandResult qaHit = processor.Execute($"/qa #{hit1Id}", IntPtr.Zero);
                RecentDispatch? hit1AfterQa = runtime.Store.GetDispatch(hit1Id);
                LatestDispatchSnapshot? latestQaHit = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                long dailyAfterQaHit = runtime.QuotaGuard.GetStats(cfg, runtime.Store).DailyTotalTokens;

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
                    hit1Under2Ms = hit1.ElapsedMs < 2.0 && hit1SyncMs < 2.0,
                    hit2Under2Ms = hit2.ElapsedMs < 2.0 && hit2SyncMs < 2.0,
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
                    qaCacheHitUnder2Ms = qaMissOutcome?.Reviewed == true && qaHit.Ok && qaHit.CacheHit &&
                                         qaHit.ElapsedMs < 2.0 && qaHitSyncMs < 2.0 &&
                                         hit1AfterQa?.Status == "PASSED" && dailyAfterQaHit == dailyAfterQaMiss &&
                                         latestQaHit != null && latestQaHit.CacheHit && latestQaHit.SavedTotalTokens > 0,
                    bypassAskBangCallsApi = bypassBang.Ok && !bypassBang.CacheHit && bypassBang.AiTask != null &&
                                            bypassBangOutcome?.AiOk == true && bypassBangRow != null && bypassBangRow.TotalTokens > 0,
                    bypassAskFreshCallsApi = bypassFresh.Ok && !bypassFresh.CacheHit && bypassFresh.AiTask != null &&
                                             bypassFreshOutcome?.AiOk == true && bypassFreshRow != null && bypassFreshRow.TotalTokens > 0,
                    followUpAlwaysCallsApi = follow.Ok && !follow.CacheHit && follow.RefineTask != null &&
                                             followOutcome?.AiOk == true && afterFollow != null && afterFollow.TurnCount == 2 &&
                                             afterFollow.TotalTokens > (missRow?.TotalTokens ?? 0)
                };

                runtime.QuotaGuard.ResetSlidingWindows();
                bool pass = checks.missExecutedApi && checks.hit1FlaggedAndNoTask && checks.hit1Under2Ms && checks.hit2Under2Ms &&
                            checks.zeroTokensDeductedOnHit && checks.quotaGuardUnchangedOnHit &&
                            checks.savedTokensAccumulatedOnHit1 && checks.savedTokensAccumulatedOnHit2 &&
                            checks.qaCacheHitUnder2Ms && checks.bypassAskBangCallsApi &&
                            checks.bypassAskFreshCallsApi && checks.followUpAlwaysCallsApi;

                return (new
                {
                    scenario = "cache-hit-bypass",
                    pass,
                    checks,
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
            bool foundVaultDoc = directCold.GroundingSources.Any(s =>
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
                groundAliasHitUnder2Ms = ground2.Ok && ground2.Kind == "ground" && ground2.CacheHit &&
                                         ground2.ElapsedMs < 2.0 && g2SyncMs < 2.0 &&
                                         g2Row != null && g2Row.TotalTokens == 0 &&
                                         latestG2 != null && latestG2.CacheHit &&
                                         latestG2.SavedTotalTokens > 0 &&
                                         latestG2.GroundingSources != null &&
                                         latestG2.GroundingSources.SequenceEqual(latestG1!.GroundingSources!)
            };

            runtime.QuotaGuard.ResetSlidingWindows();
            bool pass = checks.sectionsBounded && checks.charsBounded && checks.sourcesFormatted &&
                        checks.foundVaultDoc && checks.searchUnder5Ms && checks.groundMissOk &&
                        checks.latestG1HasSources && checks.groundAliasHitUnder2Ms;

            return (new
            {
                scenario = "vault-grounding",
                pass,
                checks,
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

            const string LiveGroundQuery =
                "Dựa trên tài liệu cục bộ về ADR-44 và ADR-45, hãy tóm tắt ngắn gọn trong 2 câu cơ chế GeminiResponseCache và VaultGroundingEngine của NEXUS Home Hub.";

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

            // Step 3: Live /qa review on Call #1 (and if NEEDS_WORK, /retry --auto-qa to reach PASSED)
            CockpitCommandResult qa = id1 > 0 ? processor.Execute($"/qa #{id1}", IntPtr.Zero) : call1;
            QaOutcome? review = Wait(qa.QaTask, wait);
            if (review != null && review.Reviewed && review.Verdict != "PASSED" && id1 > 0)
            {
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
                              call2.ElapsedMs < 2.0 && call2SyncMs < 2.0 &&
                              row2 != null && row2.TotalTokens == 0 &&
                              dailyAfterCall2 == dailyAfterCall1 && rpmAfterCall2 == rpmAfterCall1 &&
                              latest2 != null && latest2.CacheHit &&
                              latest2.SavedTotalTokens == (row1?.TotalTokens ?? -1) &&
                              latest2.PromptTokensSaved == (row1?.PromptTokens ?? -1) &&
                              latest2.CompletionTokensSaved == (row1?.CompletionTokens ?? -1);
            bool qaPassed = review?.Reviewed == true && final1 != null && final1.Status == "PASSED";

            bool pass = call1Ok && call2HitOk && qaPassed;
            return (new
            {
                scenario = "live-e2e11",
                pass,
                skipped = false,
                keySource = key.SourceLabel,
                configuredModel = cfg.GeminiModel,
                checks = new { call1Ok, call2HitOk, qaPassed },
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
