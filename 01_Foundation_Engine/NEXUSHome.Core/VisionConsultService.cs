using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-12: where the synchronous part of /img or /snap spent its time (ms). TotalMs is the whole Execute call.</summary>
    public sealed record VisionSyncTimings(double GuardMs, double SourceMs, double CacheMs, double TotalMs);

    /// <summary>
    /// TASK-12 (ADR-61): result of /img, /snap or MCP analyze_visual_input. On a cache hit <see cref="AiTask"/> is already
    /// completed (0 tokens); otherwise it compresses, dispatches and calls Gemini in the background.
    /// </summary>
    public sealed record VisionResult(
        string Kind,          // "img" | "snap"
        string Source,        // "clipboard" | "file" | "snap"
        bool Ok,
        bool Blocked,
        string Message,
        DispatchPackage? Package,
        LatestDispatchSnapshot? Snapshot,
        Task<ConsultOutcome>? AiTask,
        VisionImageInfo? Image,
        VisionGuardResult? Guard,
        bool CacheHit,
        bool MemoHit,
        string? VisionKey,
        VisionSyncTimings Timings);

    /// <summary>
    /// TASK-12 §3.4 (ADR-61): /img [question], /img --file &lt;path&gt; [question], /snap [question] and MCP analyze_visual_input.
    ///
    /// Synchronous part (Cockpit command, timed): key guard on the question → for /snap the Combat-Mode Shield and the Privacy
    /// Window Guard (<see cref="VisionSafetyGuard"/>) → source signature (file path + size + last write, or the clipboard
    /// sequence number) → if that source was compressed before in this process (memo of SHA-256 + size, never the bytes),
    /// the Vision cache key SHA-256("VISION_V1|" + imageSha256 + "|" + normalizedPrompt + "|" + primaryAgentId + "|" + model)
    /// is looked up in gemini_response_cache (SQLite WAL, same table as ADR-44) → on a hit the answer, a dispatch row and
    /// latest_dispatch.json come back at once with total_tokens = 0.
    ///
    /// Background part (miss): capture (snap) or decode → <see cref="VisionImageProcessor.CompressToJpegUnderBudget"/> →
    /// cache lookup again (a new process has an empty memo) → dispatch (event "vision", THINKING) → quota guard → Gemini with
    /// <c>inline_data</c> → cache Put under the Vision key → SQLite answer → latest_dispatch.json with hasVisionAttachment,
    /// visionImageBytes and visionImageSha256. The JPEG bytes live only inside that background call.
    /// </summary>
    public sealed class VisionConsultService
    {
        public const string NoClipboardImageMessage =
            "[VISION INFO] No image found in Clipboard. Copy an image (Win+Shift+S) or use /img --file <path> <prompt>.";

        public const string DefaultPrompt = "Mô tả ngắn gọn nội dung ảnh và chỉ ra lỗi hoặc điểm cần chú ý (nếu có).";
        public const string VisionEvent = "vision";
        public const string CacheCommandType = "vision";
        public const int MaxPromptChars = 4000;
        private const int MemoCapacity = 32;

        private readonly SquadDispatcher _dispatcher;
        private readonly SqliteMemoryStore _store;
        private readonly AgentRegistry _registry;
        private readonly NexusConfigService _config;
        private readonly string _latestPath;
        private readonly string _source;
        private readonly GeminiAgentClient _client;
        private readonly AgentConsultService _consult;
        private readonly object _memoGate = new();
        private readonly Dictionary<string, VisionImageInfo> _memo = new(StringComparer.Ordinal);
        private readonly Queue<string> _memoOrder = new();

        public VisionConsultService(NexusRuntime runtime, GeminiAgentClient? client = null)
        {
            _dispatcher = runtime.Dispatcher;
            _store = runtime.Store;
            _registry = runtime.Registry;
            _config = runtime.Config;
            _latestPath = runtime.LatestDispatchPath;
            _source = runtime.Source;
            _consult = runtime.Consult;
            _client = client ?? new GeminiAgentClient();
        }

        /// <summary>Number of compressed sources remembered in this process (SHA-256 + size only).</summary>
        public int MemoCount
        {
            get { lock (_memoGate) return _memo.Count; }
        }

        /// <summary>§3.4.2: SHA-256 hex (lowercase) of "VISION_V1|imageSha256|normalizedPrompt|primaryAgentId|modelName".</summary>
        public static string ComputeVisionKey(string imageSha256Hex, string prompt, string primaryAgentId, string modelName)
        {
            string payload = "VISION_V1|" + (imageSha256Hex ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                             GeminiResponseCache.NormalizePrompt(prompt) + "|" + (primaryAgentId ?? string.Empty).Trim() + "|" +
                             (modelName ?? string.Empty).Trim().ToLowerInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        /// <summary>
        /// "/img [--fresh] [--file &lt;path&gt; | --clipboard] [question]". A path with spaces goes in double quotes.
        /// Returns the source ("clipboard" | "file"), the path, the question and an error (usage) or null.
        /// </summary>
        public static (string Source, string? FilePath, string Prompt, bool Fresh, string? Error) ParseImgArguments(string? argument)
        {
            string rest = (argument ?? string.Empty).Trim();
            string source = "clipboard";
            string? path = null;
            bool fresh = false;
            while (rest.Length > 0)
            {
                int space = rest.IndexOf(' ');
                string token = space < 0 ? rest : rest.Substring(0, space);
                string after = space < 0 ? string.Empty : rest.Substring(space + 1).TrimStart();
                if (token.Equals("--fresh", StringComparison.OrdinalIgnoreCase) || token.Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    fresh = true;
                    rest = after;
                }
                else if (token.Equals("--clipboard", StringComparison.OrdinalIgnoreCase))
                {
                    source = "clipboard";
                    rest = after;
                }
                else if (token.Equals("--file", StringComparison.OrdinalIgnoreCase))
                {
                    source = "file";
                    if (after.Length == 0) return (source, null, string.Empty, fresh, "Dùng: /img --file <đường_dẫn_ảnh> [câu hỏi]");
                    if (after[0] == '"')
                    {
                        int close = after.IndexOf('"', 1);
                        if (close < 0) return (source, null, string.Empty, fresh, "Đường dẫn thiếu dấu \" đóng.");
                        path = after.Substring(1, close - 1);
                        rest = after.Substring(close + 1).TrimStart();
                    }
                    else
                    {
                        int pathEnd = after.IndexOf(' ');
                        path = pathEnd < 0 ? after : after.Substring(0, pathEnd);
                        rest = pathEnd < 0 ? string.Empty : after.Substring(pathEnd + 1).TrimStart();
                    }
                }
                else
                {
                    break;
                }
            }
            return (source, path, rest, fresh, null);
        }

        /// <summary>Synchronous entry point (Cockpit /img, /snap; MCP awaits <see cref="VisionResult.AiTask"/>). Never throws.</summary>
        /// <param name="source">"clipboard" | "file" | "snap".</param>
        /// <param name="previousForeground">/snap: the window that had focus before the Cockpit (privacy check).</param>
        public VisionResult Execute(string source, string? prompt, string? filePath, string? nexusMode, string? preferredAgentId,
            bool fresh, IntPtr previousForeground = default)
        {
            long t0 = Stopwatch.GetTimestamp();
            string src = (source ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "clipboard" or "clip" or "" => "clipboard",
                "file" => "file",
                "snap" or "screen" => "snap",
                _ => "?"
            };
            string kind = src == "snap" ? "snap" : "img";
            if (src == "?") return Fail(kind, src, $"[VISION] source '{source}' không hợp lệ: clipboard | file | snap.", t0);

            string question = string.IsNullOrWhiteSpace(prompt) ? DefaultPrompt : prompt.Trim();
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(question))
                return Fail(kind, src, "[VISION] Câu hỏi có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", t0, blocked: true);
            if (question.Length > MaxPromptChars) return Fail(kind, src, $"[VISION] Câu hỏi dài quá {MaxPromptChars} ký tự.", t0);
            if (!OperatingSystem.IsWindows()) return Fail(kind, src, "[VISION INFO] /img và /snap cần Windows.", t0);

            NexusConfig config = _config.Current;
            string mode = string.IsNullOrWhiteSpace(nexusMode) ? config.DefaultNexusMode : nexusMode;

            // 1. /snap shields (Combat first; nothing touches the screen when either one blocks).
            VisionGuardResult? guard = null;
            double guardMs = 0;
            if (src == "snap")
            {
                guard = VisionSafetyGuard.CheckSnap(config, previousForeground);
                guardMs = guard.ElapsedMs;
                if (!guard.Allowed)
                {
                    _store.WriteAuditLog("vision_snap_blocked", ActionRiskLevel.Green_SafeRead,
                        JsonSerializer.Serialize(new { blockedBy = guard.BlockedBy, keyword = guard.Keyword, simulated = guard.Simulated, combat = guard.CombatProcesses, source = _source }),
                        guard.BlockedBy == "combat" ? "CombatShield" : guard.BlockedBy == "privacy" ? "PrivacyGuard" : "Unavailable");
                    return new VisionResult(kind, src, false, true, guard.Message, null, null, null, null, guard, false, false, null,
                        new VisionSyncTimings(guardMs, 0, 0, Ms(t0)));
                }
            }

            // 2. Source signature + memo.
            long tSource = Stopwatch.GetTimestamp();
            string? memoKey = null;
            string? fullPath = null;
            if (src == "file")
            {
                if (string.IsNullOrWhiteSpace(filePath)) return Fail(kind, src, "Dùng: /img --file <đường_dẫn_ảnh> [câu hỏi]", t0);
                try
                {
                    fullPath = Path.GetFullPath(filePath.Trim().Trim('"'));
                    var info = new FileInfo(fullPath);
                    if (!info.Exists) return Fail(kind, src, $"[VISION INFO] Không tìm thấy file ảnh: {fullPath}", t0);
                    memoKey = $"file|{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    return Fail(kind, src, $"[VISION INFO] Đường dẫn ảnh không hợp lệ: {ex.Message}", t0);
                }
            }
            else if (src == "clipboard")
            {
                if (!ClipboardService.ContainsImage()) return Fail(kind, src, NoClipboardImageMessage, t0);
                memoKey = $"clip|{ClipboardService.SequenceNumber}";
            }
            VisionImageInfo? memo = memoKey == null ? null : MemoGet(memoKey);
            double sourceMs = Ms(tSource);

            string task = BuildTask(kind, question);
            string primaryAgentId = ResolvePrimaryAgentId(task, preferredAgentId);
            string modelName = GeminiResponseCache.ResolveModelIdForCache(config, isQa: false);

            // 3. Cache hit on the synchronous path (memo known): 0 tokens, answer at once.
            double cacheMs = 0;
            if (memo != null && !fresh)
            {
                long tCache = Stopwatch.GetTimestamp();
                string key = ComputeVisionKey(memo.Sha256Hex, question, primaryAgentId, modelName);
                bool hit = TryCacheHit(key, task, mode, preferredAgentId, memo, src, out DispatchPackage? package, out ConsultOutcome? outcome);
                cacheMs = Ms(tCache);
                if (hit && package != null && outcome != null)
                {
                    string hitMessage =
                        $"[{kind.ToUpperInvariant()} #{package.DispatchId} // VISION CACHE HIT] {package.SquadId} → {package.PrimaryAgent.Name} | " +
                        $"{memo.Bytes:N0} B JPEG sha256 {Short(memo.Sha256Hex)} | 0 tokens deducted (saved {outcome.SavedTotalTokens:N0} total tokens) in {Ms(t0):F2} ms";
                    return new VisionResult(kind, src, true, false, hitMessage, package, outcome.Snapshot, Task.FromResult(outcome), memo, guard,
                        true, true, key, new VisionSyncTimings(guardMs, sourceMs, cacheMs, Ms(t0)));
                }
            }

            // 4. Miss: the clipboard is read now (it may change while the background task runs); everything else in the background.
            byte[]? clipboardBytes = null;
            string clipboardFormat = string.Empty;
            if (src == "clipboard")
            {
                clipboardBytes = ClipboardService.TryReadImageBytes(out clipboardFormat);
                if (clipboardBytes == null) return Fail(kind, src, $"{NoClipboardImageMessage} ({clipboardFormat})", t0);
            }
            var pending = new PendingVision(kind, src, question, task, mode, preferredAgentId, fresh, memoKey, fullPath, clipboardBytes,
                clipboardFormat, guard?.Region, primaryAgentId, modelName);
            Task<ConsultOutcome> aiTask = Task.Run(() => CompleteAsync(pending));
            string model = GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : config.GeminiModel;
            string message =
                $"[{kind.ToUpperInvariant()} // VISION{(fresh ? " // FRESH" : string.Empty)}] " +
                (src == "snap" ? $"Chụp {guard?.Region?.Monitor ?? "màn hình"} (khung Cockpit bị che đen), " : $"Ảnh từ {(src == "file" ? Path.GetFileName(fullPath) : "Clipboard")}: ") +
                $"nén JPEG ≤ {config.MaxVisionImageBytes / 1024} KB, cạnh ≤ {config.MaxVisionDimensionPx} px → THINKING... Calling Gemini ({model})";
            return new VisionResult(kind, src, true, false, message, null, null, aiTask, memo, guard, false, memo != null, null,
                new VisionSyncTimings(guardMs, sourceMs, cacheMs, Ms(t0)));
        }

        private sealed record PendingVision(string Kind, string Source, string Question, string TaskText, string Mode, string? PreferredAgentId,
            bool Fresh, string? MemoKey, string? FullPath, byte[]? ClipboardBytes, string ClipboardFormat, VisionSnapRegion? Region,
            string PrimaryAgentId, string ModelName);

        private async Task<ConsultOutcome> CompleteAsync(PendingVision p)
        {
            if (!OperatingSystem.IsWindows()) return Failure("[VISION INFO] /img và /snap cần Windows.", null);
            NexusConfig config = _config.Current;
            VisionImage image;
            try
            {
                image = Acquire(p, config);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OutOfMemoryException or
                                           ExternalException or InvalidOperationException or UnauthorizedAccessException)
            {
                _store.WriteAuditLog("gemini_vision", ActionRiskLevel.Green_SafeRead,
                    JsonSerializer.Serialize(new { source = p.Source, error = ex.GetType().Name }), "ImageFailed");
                return Failure($"[VISION ERROR] Không đọc / nén được ảnh ({p.Source}): {ex.GetType().Name}: {ex.Message}", null);
            }
            VisionImageInfo info = image.Info;
            if (p.MemoKey != null) MemoPut(p.MemoKey, info);

            string key = ComputeVisionKey(image.Sha256Hex, p.Question, p.PrimaryAgentId, p.ModelName);
            if (!p.Fresh && TryCacheHit(key, p.TaskText, p.Mode, p.PreferredAgentId, info, p.Source, out _, out ConsultOutcome? hit) && hit != null)
            {
                return hit;
            }

            DispatchOutcome dispatch = _dispatcher.Dispatch(p.TaskText, p.Mode, p.PreferredAgentId);
            if (dispatch.Package == null) return Failure($"[VISION] Dispatch lỗi: {dispatch.Error}", null);
            DispatchPackage package = dispatch.Package;
            LatestDispatchSnapshot thinking = WithVision(LatestDispatchFile.FromDispatch(package, p.TaskText, _source) with { Event = VisionEvent }, info, p.Source);
            LatestDispatchFile.Write(_latestPath, thinking);

            GeminiQuotaGuard quota = _consult.QuotaGuard;
            GeminiResult result;
            string keySource = "None";
            try
            {
                if (!quota.TryAcquireRequest(config, _store, out string? limitKind))
                {
                    _store.WriteAuditLog("gemini_quota_guard", ActionRiskLevel.Green_SafeRead,
                        JsonSerializer.Serialize(new { dispatchId = package.DispatchId, limit = limitKind, source = _source }), $"Blocked_{limitKind}");
                    result = new GeminiResult(false, GeminiQuotaGuard.BlockMessage, string.Empty, 0, null, "QuotaGuard");
                }
                else
                {
                    ApiKeyResolution apiKey = GeminiAgentClient.MockEnabled ? new ApiKeyResolution(ApiKeySource.Missing, null, null) : _consult.Vault.Resolve();
                    keySource = GeminiAgentClient.MockEnabled ? "Mock" : apiKey.SourceLabel;
                    result = await _client.GenerateAsync(package.SystemInstruction, p.Question, config, apiKey.Key, package.PrimaryAgent.Name,
                        package.SquadId, default, null, quota, new GeminiImageAttachment(image.Bytes, image.MimeType, image.Sha256Hex)).ConfigureAwait(false);
                    if (result.TotalTokens > 0) quota.RecordTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                }
            }
            catch (Exception ex)
            {
                result = new GeminiResult(false, $"[GEMINI CLIENT ERROR] {ex.GetType().Name}", string.Empty, 0, null, "Client");
            }

            RecentDispatch? row = null;
            LatestDispatchSnapshot? snapshot = null;
            string? fileError = null;
            long savedTotal = 0;
            try
            {
                if (result.Ok)
                {
                    _consult.ResponseCache.Put(key, CacheCommandType, result.Model.Length > 0 ? result.Model : p.ModelName,
                        $"VISION_V1|{image.Sha256Hex}|{p.Question}", image.Sha256Hex, Array.Empty<string>(), result.Text,
                        result.PromptTokens, result.CompletionTokens);
                }
                savedTotal = _consult.ResponseCache.GetStats().SavedTotalTokens;
                row = _store.SaveAiResponse(package.DispatchId, result.Text, result.Model, result.LatencyMs,
                    TurnHistory.FirstTurn(p.TaskText, result.Text, result.Ok), result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                if (row != null)
                {
                    var (sessionTotal, dailyTotal) = quota.GetCurrentTotals();
                    snapshot = WithVision(LatestDispatchFile.FromRecord(row, _registry, _source, sessionTotal, dailyTotal) with
                    {
                        Event = "ai_response",
                        ElapsedMs = package.ElapsedMs,
                        CacheHit = false,
                        SavedTotalTokens = savedTotal
                    }, info, p.Source);
                    fileError = LatestDispatchFile.Write(_latestPath, snapshot);
                }
                _store.WriteAuditLog("gemini_vision", ActionRiskLevel.Green_SafeRead,
                    JsonSerializer.Serialize(new
                    {
                        dispatchId = package.DispatchId,
                        source = p.Source,
                        model = result.Model,
                        ok = result.Ok,
                        latencyMs = result.LatencyMs,
                        totalTokens = result.TotalTokens,
                        imageBytes = info.Bytes,
                        imageSha256 = info.Sha256Hex,
                        quality = info.Quality,
                        width = info.Width,
                        height = info.Height,
                        cacheHit = false,
                        keySource,
                        error = result.ErrorKind
                    }),
                    result.Ok ? "Answered" : result.ErrorKind ?? "Failed");
            }
            catch (Exception ex)
            {
                fileError = $"SQLite: {ex.Message}";
            }

            return new ConsultOutcome(package.DispatchId, result.Ok, result.Text, result.Model, result.LatencyMs, keySource, row, snapshot, fileError,
                result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.HttpRetryCount, CacheHit: false, SavedTotalTokens: savedTotal);
        }

        [SupportedOSPlatform("windows")]
        private static VisionImage Acquire(PendingVision p, NexusConfig config)
        {
            int maxDim = config.MaxVisionDimensionPx;
            int maxBytes = config.MaxVisionImageBytes;
            switch (p.Source)
            {
                case "file":
                    return VisionImageProcessor.CompressFile(p.FullPath ?? string.Empty, maxDim, maxBytes);
                case "clipboard":
                    return VisionImageProcessor.CompressBytes(p.ClipboardBytes ?? Array.Empty<byte>(), "clipboard-" + p.ClipboardFormat.ToLowerInvariant(), maxDim, maxBytes);
                default:
                {
                    VisionSnapRegion region = p.Region ?? throw new InvalidOperationException("No capture region (the /snap shields did not run).");
                    using var dpi = new VisionSafetyGuard.PerMonitorDpiScope();
                    using Bitmap shot = VisionImageProcessor.CaptureScreen(region.Left, region.Top, region.Width, region.Height, region.Blackouts);
                    return VisionImageProcessor.CompressToJpegUnderBudget(shot, maxDim, maxBytes, (long)region.Width * region.Height * 3, "screen");
                }
            }
        }

        /// <summary>Vision cache lookup + dispatch row in one SQLite WAL transaction, then latest_dispatch.json (ADR-14).</summary>
        private bool TryCacheHit(string key, string task, string mode, string? preferredAgentId, VisionImageInfo info, string source,
            out DispatchPackage? package, out ConsultOutcome? outcome)
        {
            outcome = null;
            long t0 = Stopwatch.GetTimestamp();
            if (!_dispatcher.TryDispatchFromCache(key, task, mode, preferredAgentId, out package, out GeminiCacheHit? hit, out RecentDispatch? row, out _) ||
                package == null || hit == null || row == null)
            {
                return false;
            }
            double lookupMs = Ms(t0);
            var (sessionTotal, dailyTotal) = _consult.QuotaGuard.GetCurrentTotals();
            LatestDispatchSnapshot snapshot = WithVision(LatestDispatchFile.FromRecord(row, _registry, _source, sessionTotal, dailyTotal) with
            {
                Event = "ai_response",
                ElapsedMs = package.ElapsedMs,
                CacheHit = true,
                PromptTokensSaved = hit.PromptTokensSaved,
                CompletionTokensSaved = hit.CompletionTokensSaved,
                SavedTotalTokens = hit.CumulativeSavedTotalTokens,
                CacheLookupMs = lookupMs,
                TotalDispatchMs = Ms(t0)
            }, info, source);
            string? fileError = LatestDispatchFile.Write(_latestPath, snapshot);
            outcome = new ConsultOutcome(row.Id, true, hit.ResponseText, hit.ModelId, 0, "Cache", row, snapshot, fileError,
                0, 0, 0, 0, CacheHit: true, PromptTokensSaved: hit.PromptTokensSaved, CompletionTokensSaved: hit.CompletionTokensSaved,
                SavedTotalTokens: hit.CumulativeSavedTotalTokens);
            return true;
        }

        private static LatestDispatchSnapshot WithVision(LatestDispatchSnapshot snapshot, VisionImageInfo info, string source) =>
            snapshot with
            {
                HasVisionAttachment = true,
                VisionImageBytes = info.Bytes,
                VisionImageSha256 = info.Sha256Hex,
                VisionSource = source,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o") // the Cockpit measures its sync latency from this moment
            };

        /// <summary>What goes into agent_dispatch_logs.task_summary and is routed (the "[VISION /img]" tag matches no squad keyword).</summary>
        public static string BuildTask(string kind, string question) => $"[VISION /{kind}] {question}";

        private string ResolvePrimaryAgentId(string task, string? preferredAgentId)
        {
            if (!string.IsNullOrWhiteSpace(preferredAgentId))
            {
                AgentLookup lookup = _registry.Resolve(preferredAgentId);
                return lookup.Profile?.Id ?? preferredAgentId.Trim();
            }
            return SquadDispatcher.Route(task).Squad.Primary.AgentId;
        }

        private VisionImageInfo? MemoGet(string key)
        {
            lock (_memoGate) return _memo.TryGetValue(key, out VisionImageInfo? info) ? info : null;
        }

        private void MemoPut(string key, VisionImageInfo info)
        {
            lock (_memoGate)
            {
                if (!_memo.ContainsKey(key)) _memoOrder.Enqueue(key);
                _memo[key] = info;
                while (_memoOrder.Count > MemoCapacity) _memo.Remove(_memoOrder.Dequeue());
            }
        }

        private static ConsultOutcome Failure(string message, RecentDispatch? row) =>
            new(0, false, message, string.Empty, 0, "None", row, null, null);

        private static VisionResult Fail(string kind, string source, string message, long t0, bool blocked = false) =>
            new(kind, source, false, blocked, message, null, null, null, null, null, false, false, null, new VisionSyncTimings(0, 0, 0, Ms(t0)));

        private static string Short(string sha) => sha.Length >= 12 ? sha.Substring(0, 12) : sha;

        private static double Ms(long startTicks) =>
            Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
