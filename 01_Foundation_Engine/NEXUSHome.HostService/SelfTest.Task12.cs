using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-12 (ADR-61 Multimodal Vision: /img, /snap) ─────────────────────────────

        private const int VisionHitSamples = 25;
        private const double VisionHitP50MaxMs = 2.0;
        private const double VisionHitP95MaxMs = 2.5; // TASK-12c Gate 12: selftest P95 <= 2.5 ms (was 3.0)
        private const double CombatShieldMaxMs = 2.0;
        private const int CombatSamples = 20;
        private const int VisionBudgetBytes = 256000;
        private const string VisionMockAnswer = "Gate 12 vision mock";
        internal const string Gate12PrivacyTitle = "Vietcombank - Password Manager";
        private static readonly (int X, int Y) Gate12FlipPixel = (1001, 501);
        /// <summary>E3 pixel test on a 1280x720 image (sent without downscaling): a dark spot set to white.</summary>
        private static readonly (int X, int Y) Gate12SentFlipPixel = (64, 48);

        /// <summary>Folder of the Gate 12 test images (outside the repository, so the Git guard stays clean).</summary>
        internal static string Gate12ImageFolder => Path.Combine(Path.GetTempPath(), "NEXUS_Gate12");

        private static ConsultOutcome? AwaitVision(CockpitCommandResult result, int timeoutSeconds = 60)
        {
            System.Threading.Tasks.Task<ConsultOutcome>? task = result.AiTask;
            if (task == null) return null;
            return task.Wait(TimeSpan.FromSeconds(timeoutSeconds)) ? task.Result : null;
        }

        /// <summary>
        /// E1 (Gate 13 polish, headless part) + E2 + E3: Right MFD preview without the repeated title line; the clipboard
        /// history opt-out is in ClipboardService; a 2560x1440 colour + noise PNG (&gt; 2 MB) compressed to JPEG &lt;= 256 000 B with
        /// edge &lt;= 1280 px; a pure-noise image still fits the budget; /img --file → mock Gemini answer echoing the exact JPEG size
        /// and SHA-256; latest_dispatch.json vision fields; 25 identical /img calls = 25 Vision cache hits, 0 tokens,
        /// P50 &lt;= 2.0 / P95 &lt;= 3.0 ms; one changed pixel or another question = cache miss.
        /// The key hashes the JPEG that Gemini receives (§3.2/§3.4). The pixel test therefore changes one pixel of an image
        /// that is sent at its own size (1280x720). On the 2560x1440 pair the changed pixel can vanish in the 2:1 downscale
        /// plus JPEG quantization; then the JPEG bytes are identical, Gemini would see the same image, and a hit is correct.
        /// That case is reported as consistent (same bytes → hit, different bytes → miss).
        /// </summary>
        private static (object, bool) Vision(NexusRuntime runtime)
        {
            runtime.WarmUp();
            string? savedMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, VisionMockAnswer);
            try
            {
                var processor = new CockpitCommandProcessor(runtime);
                string runId = DateTime.UtcNow.ToString("HHmmssfff");

                // ── E1 (a): Right MFD preview starts after the title line ──
                IReadOnlyList<HudWidgetCard> gate13Cards = WidgetCardExtractor.Extract(Gate13Response);
                HudWidgetCard codeCard = gate13Cards.First(c => c.Type == "code");
                HudWidgetCard checklistCard = gate13Cards.First(c => c.Type == "checklist");
                string codePreview = WidgetCardExtractor.PreviewAfterTitle(codeCard.Title, codeCard.Content);
                string checklistPreview = WidgetCardExtractor.PreviewAfterTitle(checklistCard.Title, checklistCard.Content);
                string notePreview = WidgetCardExtractor.PreviewAfterTitle("Cổng TCP Socket UTH: 8888", "Cổng TCP Socket UTH: 8888");
                string oneLinePreview = WidgetCardExtractor.PreviewAfterTitle("powershell: Test-NetConnection -ComputerName 127.0.0.1 -Port 8888", Gate13CodePowerShell);
                bool previewOk = codePreview == "listener.Start();" && checklistPreview == "- [x] Build Release -warnaserror" &&
                                 notePreview.Length == 0 && oneLinePreview.Length == 0;

                // ── E1 (b): Win+V history opt-out in ClipboardService (live clipboard flags are read by Verify-Task12 over the pipe) ──
                string engineRoot = Path.GetFullPath(Path.Combine(runtime.VaultPath, "..", "01_Foundation_Engine"));
                string hudSource = Path.Combine(engineRoot, "NEXUSHome.Core", "HudWidgets.cs");
                string hudText = File.Exists(hudSource) ? File.ReadAllText(hudSource) : string.Empty;
                bool historyOptOutInSource = hudText.Contains("\"" + ClipboardService.ExcludeFormatName + "\"", StringComparison.Ordinal) &&
                                             hudText.Contains("SetDwordFormat(ExcludeFormatName, 0)", StringComparison.Ordinal) &&
                                             hudText.Contains("\"" + ClipboardService.CanIncludeInHistoryFormatName + "\"", StringComparison.Ordinal);

                // ── E2: test images ──
                Directory.CreateDirectory(Gate12ImageFolder);
                string imageA = Path.Combine(Gate12ImageFolder, "gate12_noise_2560x1440.png");
                string imageB = Path.Combine(Gate12ImageFolder, "gate12_noise_2560x1440_pixel.png");
                string worst = Path.Combine(Gate12ImageFolder, "gate12_purenoise_1920x1080.png");
                long sizeA = VisionImageProcessor.WriteTestPng(imageA, 2560, 1440, 1201, 6);
                long sizeB = VisionImageProcessor.WriteTestPng(imageB, 2560, 1440, 1201, 6, Gate12FlipPixel);
                long sizeWorst = VisionImageProcessor.WriteTestPng(worst, 1920, 1080, 77, 127);
                string imageC = Path.Combine(Gate12ImageFolder, "gate12_smooth_1280x720.png");
                string imageD = Path.Combine(Gate12ImageFolder, "gate12_smooth_1280x720_pixel.png");
                VisionImageProcessor.WriteTestPng(imageC, 1280, 720, 3, 0);
                VisionImageProcessor.WriteTestPng(imageD, 1280, 720, 3, 0, Gate12SentFlipPixel);

                NexusConfig config = runtime.Config.Current;
                VisionImage direct = VisionImageProcessor.CompressFile(imageA, config.MaxVisionDimensionPx, config.MaxVisionImageBytes);
                var probe = VisionImageProcessor.Probe(direct.Bytes);
                bool shaOk = direct.Sha256Hex.Length == 64 && direct.Sha256Hex.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                             direct.Sha256Hex == VisionImageProcessor.Sha256Hex(direct.Bytes.Span);
                VisionImage directAgain = VisionImageProcessor.CompressFile(imageA, config.MaxVisionDimensionPx, config.MaxVisionImageBytes);
                VisionImage worstImage = VisionImageProcessor.CompressFile(worst, config.MaxVisionDimensionPx, config.MaxVisionImageBytes);
                bool compressOk = sizeA > 2_000_000 && direct.ByteCount <= VisionBudgetBytes && Math.Max(direct.Width, direct.Height) <= 1280 &&
                                  direct.Width == 1280 && direct.Height == 720 && direct.MimeType == "image/jpeg" && probe.Format == "jpeg" &&
                                  probe.Width == direct.Width && probe.Height == direct.Height && shaOk &&
                                  directAgain.Sha256Hex == direct.Sha256Hex; // deterministic: same file → same JPEG → same cache key
                bool budgetLoopOk = worstImage.ByteCount <= VisionBudgetBytes && Math.Max(worstImage.Width, worstImage.Height) <= 1280;

                // ── TASK-12b: zero-copy decode from unmanaged memory (the clipboard path) + 4096 px source limit ──
                UnmanagedDecode unmanaged = DecodeFromUnmanaged(imageA, config);
                bool zeroCopyOk = unmanaged.PngSha == direct.Sha256Hex && unmanaged.DibBytes is > 0 and <= VisionBudgetBytes &&
                                  unmanaged.DibWidth == 1280 && unmanaged.DibHeight == 720;
                string tooWide = Path.Combine(Gate12ImageFolder, "gate12_too_wide_4100x64.png");
                VisionImageProcessor.WriteTestPng(tooWide, 4100, 64, 5, 0);
                string? limitMessage = null;
                try { VisionImageProcessor.CompressFile(tooWide, config.MaxVisionDimensionPx, config.MaxVisionImageBytes, config.MaxSourceDimensionPx); }
                catch (VisionSourceTooLargeException ex) { limitMessage = ex.Message; }
                CockpitCommandResult tooWideCmd = processor.Execute($"/img --file \"{tooWide}\" Gate 12b limit {runId}", IntPtr.Zero);
                ConsultOutcome? tooWideOutcome = AwaitVision(tooWideCmd);
                string limitPrefix = VisionImageProcessor.SourceTooLargeMessage(4096);
                bool sourceLimitOk = config.MaxSourceDimensionPx == 4096 && limitMessage != null && limitMessage.StartsWith(limitPrefix, StringComparison.Ordinal) &&
                                     tooWideOutcome != null && !tooWideOutcome.AiOk && tooWideOutcome.AiResponse.StartsWith(limitPrefix, StringComparison.Ordinal);

                // ── E2: /img --file end to end (mock Gemini) ──
                string prompt = $"Gate 12 E2 {runId}: ảnh này có lỗi gì?";
                long rowsBefore = runtime.Store.CountDispatchLogs();
                CockpitCommandResult first = processor.Execute($"/img --file \"{imageA}\" {prompt}", IntPtr.Zero);
                ConsultOutcome? firstOutcome = AwaitVision(first);
                string expectedTag = $"[VISION-MOCK mime=image/jpeg bytes={direct.ByteCount} sha256={direct.Sha256Hex.Substring(0, 12)}]";
                LatestDispatchSnapshot? afterFirst = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                // TASK-12c: with visionWorker = process the image step ran in a McpTools --vision-worker child that exited.
                VisionBurstStats? firstBurst = runtime.Vision.LastBurst;
                bool workerOk = config.VisionWorker != "process" ||
                                (firstBurst != null && firstBurst.WorkerMode == "process" && firstBurst.WorkerExitCode == 0);
                bool flowOk = first.Ok && first.Kind == "img" && !first.CacheHit && firstOutcome != null && firstOutcome.AiOk &&
                              firstOutcome.AiResponse.StartsWith(expectedTag, StringComparison.Ordinal) &&
                              firstOutcome.AiResponse.Contains(VisionMockAnswer, StringComparison.Ordinal) &&
                              afterFirst != null && afterFirst.DispatchId == firstOutcome.DispatchId && afterFirst.HasVisionAttachment &&
                              afterFirst.VisionImageBytes == direct.ByteCount && afterFirst.VisionImageSha256 == direct.Sha256Hex &&
                              afterFirst.VisionSource == "file" && runtime.Store.CountDispatchLogs() == rowsBefore + 1;

                // ── E3: 25 identical calls = 25 Vision cache hits, 0 tokens ──
                CockpitCommandResult warmHit = processor.Execute($"/img --file \"{imageA}\" {prompt}", IntPtr.Zero); // first hit: JIT, not timed
                long sessionTokensBefore = runtime.QuotaGuard.GetCurrentTotals().SessionTotalTokens;
                long mockTokensBefore = GeminiQuotaGuard.GlobalMockTokensUsed;
                var hitTimes = new List<double>(VisionHitSamples);
                int hits = 0, zeroTokenHits = 0;
                string? hitKey = null;
                CockpitCommandResult? lastHit = null;
                for (int i = 0; i < VisionHitSamples; i++)
                {
                    CockpitCommandResult r = processor.Execute($"/img --file \"{imageA}\" {prompt}", IntPtr.Zero);
                    hitTimes.Add(r.ElapsedMs);
                    ConsultOutcome? o = r.AiTask is { IsCompleted: true } done ? done.Result : null;
                    if (r.CacheHit && o != null && o.CacheHit) hits++;
                    if (o != null && o.TotalTokens == 0 && r.Snapshot?.TotalTokens == 0) zeroTokenHits++;
                    hitKey ??= r.Vision?.VisionKey;
                    lastHit = r;
                }
                LatencyStats hitStats = LatencyStats.Of(hitTimes);
                bool tokensUnchanged = runtime.QuotaGuard.GetCurrentTotals().SessionTotalTokens == sessionTokensBefore &&
                                       GeminiQuotaGuard.GlobalMockTokensUsed == mockTokensBefore;
                LatestDispatchSnapshot? hitSnapshot = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                bool hitAnswerOk = lastHit != null && lastHit.AiTask is { IsCompleted: true } hitTask &&
                                   hitTask.Result.AiResponse == firstOutcome?.AiResponse &&
                                   hitSnapshot != null && hitSnapshot.CacheHit && hitSnapshot.TotalTokens == 0 && hitSnapshot.HasVisionAttachment &&
                                   hitSnapshot.VisionImageSha256 == direct.Sha256Hex;
                string expectedKey = VisionConsultService.ComputeVisionKey(direct.Sha256Hex, prompt,
                    SquadDispatcher.Route(VisionConsultService.BuildTask("img", prompt)).Squad.Primary.AgentId,
                    GeminiResponseCache.ResolveModelIdForCache(config));
                bool hitsOk = warmHit.CacheHit && hits == VisionHitSamples && zeroTokenHits == VisionHitSamples && tokensUnchanged && hitAnswerOk && hitKey == expectedKey;
                bool hitLatencyOk = WithinSla("/img Vision cache hit P50", hitStats.P50, VisionHitP50MaxMs) &&
                                    WithinSla("/img Vision cache hit P95", hitStats.P95, VisionHitP95MaxMs);

                // ── E3: one pixel changed on the image Gemini receives → new SHA-256 → miss ──
                VisionImage sentC = VisionImageProcessor.CompressFile(imageC, config.MaxVisionDimensionPx, config.MaxVisionImageBytes);
                VisionImage sentD = VisionImageProcessor.CompressFile(imageD, config.MaxVisionDimensionPx, config.MaxVisionImageBytes);
                string pixelPrompt = $"Gate 12 E3 {runId}: pixel test";
                ConsultOutcome? seedC = AwaitVision(processor.Execute($"/img --file \"{imageC}\" {pixelPrompt}", IntPtr.Zero));
                CockpitCommandResult againC = processor.Execute($"/img --file \"{imageC}\" {pixelPrompt}", IntPtr.Zero);
                ConsultOutcome? againCOutcome = AwaitVision(againC);
                CockpitCommandResult pixel = processor.Execute($"/img --file \"{imageD}\" {pixelPrompt}", IntPtr.Zero);
                ConsultOutcome? pixelOutcome = AwaitVision(pixel);
                LatestDispatchSnapshot? afterPixel = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                bool pixelMissOk = sentC.Width == 1280 && sentC.Height == 720 && sentC.Sha256Hex != sentD.Sha256Hex &&
                                   seedC != null && seedC.AiOk && againCOutcome != null && againCOutcome.CacheHit &&
                                   !pixel.CacheHit && pixelOutcome != null && pixelOutcome.AiOk && !pixelOutcome.CacheHit &&
                                   afterPixel?.VisionImageSha256 == sentD.Sha256Hex;

                // ── E3 (reference): same pixel test on the 2560x1440 pair, which is downscaled 2:1 before JPEG ──
                VisionImage directB = VisionImageProcessor.CompressFile(imageB, config.MaxVisionDimensionPx, config.MaxVisionImageBytes);
                bool downscaleSameBytes = directB.Sha256Hex == direct.Sha256Hex;
                CockpitCommandResult pixel2k = processor.Execute($"/img --file \"{imageB}\" {prompt}", IntPtr.Zero);
                ConsultOutcome? pixel2kOutcome = AwaitVision(pixel2k);
                bool pixel2kHit = pixel2k.CacheHit || pixel2kOutcome?.CacheHit == true;
                bool downscaleConsistentOk = pixel2kOutcome != null && pixel2kOutcome.AiOk && pixel2kHit == downscaleSameBytes;
                string otherPrompt = prompt + " (câu hỏi khác)";
                CockpitCommandResult other = processor.Execute($"/img --file \"{imageA}\" {otherPrompt}", IntPtr.Zero);
                ConsultOutcome? otherOutcome = AwaitVision(other);
                LatestDispatchSnapshot? afterOther = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                bool promptMissOk = !other.CacheHit && other.Vision?.MemoHit == true && otherOutcome != null && otherOutcome.AiOk &&
                                    !otherOutcome.CacheHit && afterOther?.VisionImageSha256 == direct.Sha256Hex;
                CockpitCommandResult fresh = processor.Execute($"/img --fresh --file \"{imageA}\" {prompt}", IntPtr.Zero);
                ConsultOutcome? freshOutcome = AwaitVision(fresh);
                bool freshOk = !fresh.CacheHit && freshOutcome != null && freshOutcome.AiOk && !freshOutcome.CacheHit;

                // ── usage errors ──
                CockpitCommandResult missingFile = processor.Execute("/img --file \"C:\\nexus\\khong_co_anh_nay.png\" test", IntPtr.Zero);
                CockpitCommandResult keyLeak = processor.Execute("/img " + "AIza" + "SyDummySecretKeyLeakTest123456789", IntPtr.Zero);
                bool guardsOk = !missingFile.Ok && missingFile.Message.Contains("Không tìm thấy file ảnh", StringComparison.Ordinal) &&
                                !keyLeak.Ok && keyLeak.Kind == "blocked";

                var checks = new { previewOk, historyOptOutInSource, compressOk, budgetLoopOk, zeroCopyOk, sourceLimitOk, workerOk, flowOk, hitsOk, hitLatencyOk, pixelMissOk, downscaleConsistentOk, promptMissOk, freshOk, guardsOk };
                bool pass = previewOk && historyOptOutInSource && compressOk && budgetLoopOk && zeroCopyOk && sourceLimitOk && workerOk && flowOk && hitsOk && hitLatencyOk &&
                            pixelMissOk && downscaleConsistentOk && promptMissOk && freshOk && guardsOk;
                return (new
                {
                    scenario = "vision",
                    pass,
                    checks,
                    preview = new { codeTitle = codeCard.Title, codePreview, checklistTitle = checklistCard.Title, checklistPreview, notePreview, oneLinePreview },
                    images = new
                    {
                        folder = Gate12ImageFolder,
                        imageA,
                        imageB,
                        worst,
                        sourceBytesA = sizeA,
                        sourceBytesB = sizeB,
                        sourceBytesWorst = sizeWorst,
                        flipPixel = new { x = Gate12FlipPixel.X, y = Gate12FlipPixel.Y },
                        imageC,
                        imageD,
                        sentFlipPixel = new { x = Gate12SentFlipPixel.X, y = Gate12SentFlipPixel.Y }
                    },
                    compressed = direct.Info,
                    compressedWorst = worstImage.Info,
                    zeroCopy = unmanaged,
                    worker = new { config.VisionWorker, burst = firstBurst, path = VisionWorkerClient.ResolvePath() },
                    sourceLimit = new { limitMessage, command = tooWideOutcome?.AiResponse, config.MaxSourceDimensionPx },
                    budgetBytes = VisionBudgetBytes,
                    prompt,
                    expectedTag,
                    first = new { first.Message, first.ElapsedMs, aiResponse = firstOutcome?.AiResponse, dispatchId = firstOutcome?.DispatchId },
                    latestAfterFirst = afterFirst == null ? null : new { afterFirst.Event, afterFirst.HasVisionAttachment, afterFirst.VisionImageBytes, afterFirst.VisionImageSha256, afterFirst.VisionSource },
                    hits = new { samples = VisionHitSamples, hits, zeroTokenHits, latency = hitStats, p50MaxMs = VisionHitP50MaxMs, p95MaxMs = VisionHitP95MaxMs, timings = lastHit?.Vision?.Timings, message = lastHit?.Message, visionKey = hitKey, expectedKey, tokensUnchanged },
                    pixelMiss = new
                    {
                        pixel.Message,
                        cacheHit = pixel.CacheHit,
                        asyncCacheHit = pixelOutcome?.CacheHit,
                        seedHitAgain = againCOutcome?.CacheHit,
                        sha256 = afterPixel?.VisionImageSha256,
                        originalSha256 = sentC.Sha256Hex,
                        changedSha256 = sentD.Sha256Hex,
                        bytesOriginal = sentC.ByteCount,
                        bytesChanged = sentD.ByteCount
                    },
                    downscalePixel = new
                    {
                        sameJpegBytes = downscaleSameBytes,
                        cacheHit = pixel2kHit,
                        sha256 = directB.Sha256Hex,
                        originalSha256 = direct.Sha256Hex,
                        note = "2560x1440 → 1280x720: a 1-pixel change can vanish in downscale + JPEG; identical bytes must hit, different bytes must miss"
                    },
                    promptMiss = new { other.Message, cacheHit = other.CacheHit, memoHit = other.Vision?.MemoHit, sha256 = afterOther?.VisionImageSha256 },
                    fresh = fresh.Message,
                    missingFile = missingFile.Message,
                    keyLeak = keyLeak.Message
                }, pass);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, savedMock);
            }
        }

        /// <summary>
        /// E4 (headless): NEXUS_SIMULATE_COMBAT_MODE=1 → /snap blocked by [COMBAT SHIELD] in &lt; 2 ms with no screen copy and no
        /// dispatch row; NEXUS_SIMULATE_WINDOW_TITLE="Vietcombank - Password Manager" → [PRIVACY GUARD]; keyword matching ignores
        /// case; with both flags off /snap copies the MSI G272 work area once (Cockpit painted black) and answers via mock Gemini.
        /// </summary>
        private static (object, bool) VisionGuard(NexusRuntime runtime)
        {
            runtime.WarmUp();
            string? savedMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            string? savedCombat = Environment.GetEnvironmentVariable(VisionSafetyGuard.SimulateCombatVariable);
            string? savedTitle = Environment.GetEnvironmentVariable(VisionSafetyGuard.SimulateWindowTitleVariable);
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, VisionMockAnswer);
            try
            {
                var processor = new CockpitCommandProcessor(runtime);
                string runId = DateTime.UtcNow.ToString("HHmmssfff");

                // Combat-Mode Shield
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateWindowTitleVariable, null);
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateCombatVariable, "1");
                long captures0 = VisionImageProcessor.CaptureCount;
                long rows0 = runtime.Store.CountDispatchLogs();
                var combatTimes = new List<double>();
                CockpitCommandResult? combat = processor.Execute($"/snap Gate 12 combat warm-up {runId}", IntPtr.Zero); // JIT, not timed
                bool combatAllBlocked = combat.Kind == "blocked" && combat.Message == VisionSafetyGuard.CombatShieldMessage;
                for (int i = 0; i < CombatSamples; i++)
                {
                    combat = processor.Execute($"/snap Gate 12 combat {runId}", IntPtr.Zero);
                    combatTimes.Add(combat.ElapsedMs);
                    combatAllBlocked &= !combat.Ok && combat.Kind == "blocked" && combat.Message == VisionSafetyGuard.CombatShieldMessage &&
                                        combat.Vision?.Guard?.BlockedBy == "combat" && combat.AiTask == null;
                }
                LatencyStats combatStats = LatencyStats.Of(combatTimes);
                bool combatOk = combatAllBlocked && VisionImageProcessor.CaptureCount == captures0 && runtime.Store.CountDispatchLogs() == rows0 &&
                                WithinSla("/snap Combat Shield block P95", combatStats.P95, CombatShieldMaxMs);

                // Privacy Window Guard (simulated title)
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateCombatVariable, null);
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateWindowTitleVariable, Gate12PrivacyTitle);
                CockpitCommandResult privacy = processor.Execute($"/snap Gate 12 privacy {runId}", IntPtr.Zero);
                bool privacyOk = !privacy.Ok && privacy.Kind == "blocked" && privacy.Vision?.Guard?.BlockedBy == "privacy" &&
                                 privacy.Message.StartsWith("[PRIVACY GUARD] /snap blocked because a sensitive window title was detected (", StringComparison.Ordinal) &&
                                 VisionImageProcessor.CaptureCount == captures0 && runtime.Store.CountDispatchLogs() == rows0;

                IReadOnlyList<string> keywords = runtime.Config.Current.PrivacyRedactedWindowKeywords ?? NexusConfig.DefaultPrivacyRedactedWindowKeywords;
                bool keywordsOk = VisionSafetyGuard.MatchKeyword("secrets.env - Visual Studio Code", keywords) == ".env" &&
                                  VisionSafetyGuard.MatchKeyword("NEW TAB - [INPRIVATE] - MICROSOFT EDGE", keywords) == "InPrivate" &&
                                  VisionSafetyGuard.MatchKeyword("Google AI Studio - Get API key", keywords) == "AI Studio" &&
                                  VisionSafetyGuard.MatchKeyword("Untitled - Notepad", keywords) == null &&
                                  keywords.Count >= 15;

                // Safe path: both flags off
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateWindowTitleVariable, null);
                CockpitCommandResult safe = processor.Execute($"/snap Gate 12 safe {runId}: màn hình đang hiện gì?", IntPtr.Zero);
                ConsultOutcome? safeOutcome = AwaitVision(safe);
                LatestDispatchSnapshot? afterSafe = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
                bool safeOk = safe.Ok && safe.Kind == "snap" && safe.Vision?.Guard?.Allowed == true && safeOutcome != null && safeOutcome.AiOk &&
                              safeOutcome.AiResponse.StartsWith("[VISION-MOCK mime=image/jpeg bytes=", StringComparison.Ordinal) &&
                              VisionImageProcessor.CaptureCount == captures0 + 1 &&
                              afterSafe != null && afterSafe.HasVisionAttachment && afterSafe.VisionSource == "snap" &&
                              afterSafe.VisionImageBytes is > 0 and <= VisionBudgetBytes && afterSafe.VisionImageSha256?.Length == 64;

                var checks = new { combatOk, privacyOk, keywordsOk, safeOk };
                bool pass = combatOk && privacyOk && keywordsOk && safeOk;
                return (new
                {
                    scenario = "vision-guard",
                    pass,
                    checks,
                    combat = new { message = combat?.Message, latency = combatStats, slaMs = CombatShieldMaxMs, guard = combat?.Vision?.Guard, capturesDuringBlocks = VisionImageProcessor.CaptureCount - captures0 - (safeOk ? 1 : 0) },
                    privacy = new { privacy.Message, guard = privacy.Vision?.Guard, simulatedTitle = Gate12PrivacyTitle },
                    safe = new
                    {
                        safe.Message,
                        safe.Ok,
                        guard = safe.Vision?.Guard,
                        aiResponse = safeOutcome?.AiResponse,
                        dispatchId = safeOutcome?.DispatchId,
                        visionImageBytes = afterSafe?.VisionImageBytes,
                        visionImageSha256 = afterSafe?.VisionImageSha256,
                        captures = VisionImageProcessor.CaptureCount - captures0
                    }
                }, pass);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, savedMock);
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateCombatVariable, savedCombat);
                Environment.SetEnvironmentVariable(VisionSafetyGuard.SimulateWindowTitleVariable, savedTitle);
            }
        }
    }
}
