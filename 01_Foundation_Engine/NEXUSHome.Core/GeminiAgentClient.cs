using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace NEXUSHomeHub.Core
{
    /// <summary>Answer (or diagnostic) for one Gemini call. Text never contains the API key.</summary>
    public sealed record GeminiResult(
        bool Ok,
        string Text,
        string Model,
        int LatencyMs,
        int? HttpStatus,
        string? ErrorKind,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        int HttpRetryCount = 0,
        bool IsLiveApi = false, // TASK-11b: true only for an answer parsed from the real Gemini API (mock/stub = false)
        string? ModelFallback = null); // TASK-11b: "[MODEL FALLBACK: <configured> -> <used>]" when another model answered

    /// <summary>
    /// TASK-12 ADR-61: one image sent with the last user turn as Gemini <c>inline_data</c> (JPEG &lt;= maxVisionImageBytes).
    /// <see cref="Sha256Hex"/> is the lowercase SHA-256 of <see cref="Bytes"/> (the mock echoes its first 12 characters).
    /// </summary>
    public sealed record GeminiImageAttachment(byte[] Bytes, string MimeType, string Sha256Hex)
    {
        /// <summary>Gemini bills an image of this size as a fixed ~258 prompt tokens; the mock uses the same figure.</summary>
        public const int EstimatedPromptTokens = 258;

        /// <summary>"[VISION-MOCK mime=image/jpeg bytes=123456 sha256=0123456789ab]": the evidence prefix of a mock vision answer.</summary>
        public string MockTag => $"[VISION-MOCK mime={MimeType} bytes={Bytes.Length} sha256={(Sha256Hex.Length >= 12 ? Sha256Hex.Substring(0, 12) : Sha256Hex)}]";
    }

    /// <summary>TASK-10 §3.2 / §3.4: snapshot of current in-memory RPM/TPM sliding windows and session/daily token counters.</summary>
    public sealed record GeminiUsageStats(
        int CurrentRpm,
        int MaxRequestsPerMinute,
        int CurrentTpm,
        int MaxTokensPerMinute,
        long DailyPromptTokens,
        long DailyCompletionTokens,
        long DailyTotalTokens,
        int MaxDailyTokens,
        long SessionRequests,
        long SessionPromptTokens,
        long SessionCompletionTokens,
        long SessionTotalTokens,
        long SessionHttpRetries,
        long SessionQuotaBlocks,
        int HttpMaxRetries,
        int HttpRetryBaseDelayMs,
        long MockTokensUsed = 0); // TASK-11b: today's mock/stub tokens (never limited, never part of DailyTotalTokens)

    /// <summary>
    /// TASK-10 §3.2 (ADR-41/42): client-side rate &amp; token budget guard.
    /// Maintains a 60-second sliding window in memory for RPM and TPM, plus a cached daily UTC token total seeded from SQLite.
    /// Synchronous checks complete in &lt; 0.05 ms so Cockpit SLA (&lt; 2 ms for quota block) is always met.
    /// </summary>
    public sealed class GeminiQuotaGuard
    {
        public const string BlockMessage =
            "[QUOTA GUARD] Rate/Token limit reached (RPM/TPM/Daily). Adjust in nexus_config.json and run /reload if needed.";

        private static long _globalSessionTotalTokens;
        private static long _globalCachedDailyTokens;
        private static long _globalMockTokensUsed;

        public static long GlobalSessionTotalTokens => Interlocked.Read(ref _globalSessionTotalTokens);
        public static long GlobalCachedDailyTokens => Interlocked.Read(ref _globalCachedDailyTokens);

        /// <summary>TASK-11b Hạng mục A: today's mock/stub tokens (UTC), kept apart from the live daily budget.</summary>
        public static long GlobalMockTokensUsed => Interlocked.Read(ref _globalMockTokensUsed);

        public static void UpdateGlobalDailyTokens(long dailyTotal) =>
            Interlocked.Exchange(ref _globalCachedDailyTokens, dailyTotal);

        private readonly object _gate = new();
        private readonly List<long> _requestTicks = new();
        private readonly List<(long Tick, int Tokens)> _tokenWindow = new();

        private DateTime _cachedDayUtc = DateTime.MinValue;
        private long _cachedDailyPromptTokens;
        private long _cachedDailyCompletionTokens;
        private long _cachedDailyTotalTokens;
        private long _cachedDailyMockTokens;

        private long _sessionRequests;
        private long _sessionPromptTokens;
        private long _sessionCompletionTokens;
        private long _sessionTotalTokens;
        private long _sessionHttpRetries;
        private long _sessionQuotaBlocks;

        /// <summary>
        /// Seeds the cached daily token totals from SQLite (called at WarmUp, /reload, and day rollover).
        /// TASK-11b: live tokens (live_*_tokens) feed the daily budget; mock tokens (mock_tokens_used) are only reported.
        /// </summary>
        public void SeedFromStore(SqliteMemoryStore store)
        {
            var (pt, ct, tt) = store.GetDailyTokenUsage();
            long mock = store.GetDailyMockTokens();
            lock (_gate)
            {
                _cachedDayUtc = DateTime.UtcNow.Date;
                _cachedDailyPromptTokens = pt;
                _cachedDailyCompletionTokens = ct;
                _cachedDailyTotalTokens = tt;
                _cachedDailyMockTokens = mock;
            }
            Interlocked.Exchange(ref _globalCachedDailyTokens, tt);
            Interlocked.Exchange(ref _globalMockTokensUsed, mock);
        }

        /// <summary>Resets the sliding RPM/TPM windows (used by SelfTest benchmark loops so 11 synthetic calls don't trip RPM=10).</summary>
        public void ResetSlidingWindows()
        {
            lock (_gate)
            {
                _requestTicks.Clear();
                _tokenWindow.Clear();
            }
        }

        private void EnsureDaySeededUnlocked(SqliteMemoryStore? store)
        {
            DateTime today = DateTime.UtcNow.Date;
            if (_cachedDayUtc == today) return;
            _cachedDayUtc = today;
            if (store != null)
            {
                var (pt, ct, tt) = store.GetDailyTokenUsage();
                _cachedDailyPromptTokens = pt;
                _cachedDailyCompletionTokens = ct;
                _cachedDailyTotalTokens = tt;
                _cachedDailyMockTokens = store.GetDailyMockTokens();
                Interlocked.Exchange(ref _globalCachedDailyTokens, tt);
                Interlocked.Exchange(ref _globalMockTokensUsed, _cachedDailyMockTokens);
            }
        }

        private void PruneUnlocked(long nowTicks)
        {
            long cutoff = nowTicks - Stopwatch.Frequency * 60L;
            _requestTicks.RemoveAll(t => t < cutoff);
            _tokenWindow.RemoveAll(e => e.Tick < cutoff);
        }

        /// <summary>Checks whether a request is allowed right now without reserving a request slot.</summary>
        public bool CheckCanProceed(NexusConfig config, SqliteMemoryStore? store, out string? limitKind)
        {
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                EnsureDaySeededUnlocked(store);
                PruneUnlocked(now);
                int currentRpm = _requestTicks.Count;
                int currentTpm = 0;
                for (int i = 0; i < _tokenWindow.Count; i++) currentTpm += _tokenWindow[i].Tokens;

                if (currentRpm >= config.MaxRequestsPerMinute)
                {
                    limitKind = "RPM";
                    return false;
                }
                if (currentTpm >= config.MaxTokensPerMinute)
                {
                    limitKind = "TPM";
                    return false;
                }
                if (_cachedDailyTotalTokens >= config.MaxDailyTokens)
                {
                    limitKind = "Daily";
                    return false;
                }
                limitKind = null;
                return true;
            }
        }

        /// <summary>
        /// Atomically checks RPM, TPM, and Daily token budgets; if within budget, records one request in the 60s RPM window.
        /// If exceeded, increments the quota-block counter and returns false.
        /// </summary>
        public bool TryAcquireRequest(NexusConfig config, SqliteMemoryStore? store, out string? limitKind)
        {
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                EnsureDaySeededUnlocked(store);
                PruneUnlocked(now);
                int currentRpm = _requestTicks.Count;
                int currentTpm = 0;
                for (int i = 0; i < _tokenWindow.Count; i++) currentTpm += _tokenWindow[i].Tokens;

                if (currentRpm >= config.MaxRequestsPerMinute)
                {
                    _sessionQuotaBlocks++;
                    limitKind = "RPM";
                    return false;
                }
                if (currentTpm >= config.MaxTokensPerMinute)
                {
                    _sessionQuotaBlocks++;
                    limitKind = "TPM";
                    return false;
                }
                if (_cachedDailyTotalTokens >= config.MaxDailyTokens)
                {
                    _sessionQuotaBlocks++;
                    limitKind = "Daily";
                    return false;
                }

                _requestTicks.Add(now);
                _sessionRequests++;
                limitKind = null;
                return true;
            }
        }

        /// <param name="liveApi">TASK-11b Hạng mục A: false for mock/stub answers. Those tokens only go to the mock counter:
        /// no TPM window, no session total, no daily budget.</param>
        public void RecordTokens(int promptTokens, int completionTokens, int totalTokens, bool liveApi = true)
        {
            if (promptTokens <= 0 && completionTokens <= 0 && totalTokens <= 0) return;
            if (!liveApi)
            {
                long mock;
                lock (_gate)
                {
                    _cachedDailyMockTokens += Math.Max(0, totalTokens);
                    mock = _cachedDailyMockTokens;
                }
                Interlocked.Exchange(ref _globalMockTokensUsed, mock);
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long sessionTotal;
            long dailyTotal;
            lock (_gate)
            {
                PruneUnlocked(now);
                _tokenWindow.Add((now, totalTokens));
                _sessionPromptTokens += promptTokens;
                _sessionCompletionTokens += completionTokens;
                _sessionTotalTokens += totalTokens;
                _cachedDailyPromptTokens += promptTokens;
                _cachedDailyCompletionTokens += completionTokens;
                _cachedDailyTotalTokens += totalTokens;
                sessionTotal = _sessionTotalTokens;
                dailyTotal = _cachedDailyTotalTokens;
            }
            Interlocked.Exchange(ref _globalSessionTotalTokens, sessionTotal);
            Interlocked.Exchange(ref _globalCachedDailyTokens, dailyTotal);
        }

        public void RecordHttpRetry()
        {
            lock (_gate)
            {
                _sessionHttpRetries++;
            }
        }

        public (long SessionTotalTokens, long DailyTotalTokens) GetCurrentTotals()
        {
            lock (_gate)
            {
                return (_sessionTotalTokens, _cachedDailyTotalTokens);
            }
        }

        public GeminiUsageStats GetStats(NexusConfig config, SqliteMemoryStore? store = null)
        {
            if (store != null)
            {
                SeedFromStore(store);
            }
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                PruneUnlocked(now);
                int currentRpm = _requestTicks.Count;
                int currentTpm = 0;
                for (int i = 0; i < _tokenWindow.Count; i++) currentTpm += _tokenWindow[i].Tokens;

                return new GeminiUsageStats(
                    CurrentRpm: currentRpm,
                    MaxRequestsPerMinute: config.MaxRequestsPerMinute,
                    CurrentTpm: currentTpm,
                    MaxTokensPerMinute: config.MaxTokensPerMinute,
                    DailyPromptTokens: _cachedDailyPromptTokens,
                    DailyCompletionTokens: _cachedDailyCompletionTokens,
                    DailyTotalTokens: _cachedDailyTotalTokens,
                    MaxDailyTokens: config.MaxDailyTokens,
                    SessionRequests: _sessionRequests,
                    SessionPromptTokens: _sessionPromptTokens,
                    SessionCompletionTokens: _sessionCompletionTokens,
                    SessionTotalTokens: _sessionTotalTokens,
                    SessionHttpRetries: _sessionHttpRetries,
                    SessionQuotaBlocks: _sessionQuotaBlocks,
                    HttpMaxRetries: config.HttpMaxRetries,
                    HttpRetryBaseDelayMs: config.HttpRetryBaseDelayMs,
                    MockTokensUsed: _cachedDailyMockTokens);
            }
        }
    }

    /// <summary>
    /// TASK-07 §3.3 / TASK-10 §3.2–3.3: generateContent client. Key in the x-goog-api-key header (never in the URL).
    /// Deterministic mock when NEXUS_GEMINI_MOCK_RESPONSE is set (no network, model "gemini-mock-verifier").
    /// Supports HTTP 429/5xx exponential backoff + jitter and NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES.
    /// Every failure becomes a short diagnostic text instead of an exception (graceful degradation).
    /// </summary>
    public sealed class GeminiAgentClient
    {
        public const string MockEnvironmentVariable = "NEXUS_GEMINI_MOCK_RESPONSE";
        public const string MockModel = "gemini-mock-verifier";
        public const string QaMockEnvironmentVariable = "NEXUS_GEMINI_QA_MOCK_VERDICT";
        public const string QaMockModel = "gemini-qa-mock-verifier";
        public const string SimulateTransientFailuresEnvironmentVariable = "NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES";

        /// <summary>
        /// TASK-11b Hạng mục D: default model (nexus_config.json geminiModel) and the one fallback used when the configured
        /// model answers HTTP 404. A fallback is never silent: every result it produces carries <see cref="GeminiResult.ModelFallback"/>
        /// and the consult service writes a "gemini_model_fallback" audit row.
        /// </summary>
        public const string FallbackModel = "gemini-3.5-flash-lite";

        /// <summary>TASK-08 §3.3.3: appended to the QA agent's instruction instead of <see cref="HudDirective"/>.</summary>
        public const string QaDirective =
            "## QA Verdict Format (mandatory)\n" +
            "- The FIRST line is exactly `VERDICT: PASSED` or `VERDICT: NEEDS_WORK`.\n" +
            "- Then 1-3 short sentences in the user's language: why, citing the evidence (or what evidence is missing).\n" +
            "- Reality Checker rule: claims without evidence, unsafe advice or anti-cheat bypass -> NEEDS_WORK.";
        public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";

        /// <summary>Appended to the dispatch SystemInstruction for answers that fit the MSI G272 HUD.</summary>
        public const string HudDirective =
            "## 7. HUD Answer Format\n" +
            "- Answer in the user's language (Vietnamese if the question is Vietnamese).\n" +
            "- Short, concrete, practical: at most ~200 words, numbered steps or bullets, code only when essential.\n" +
            "- Respect the hardware above: the game/IDE on the GTX 1660 keeps priority; never suggest disabling anti-cheat.";

        // ADR-29: one HttpClient for the process, created only when a real request is about to be sent (after the mock
        // and missing-key checks): mock runs, key-less runs and the Cockpit at rest never load the HTTP stack.
        // UseProxy = false avoids the slow WPAD proxy discovery on the first call; per-request timeout via CancellationToken.
        private static readonly Lazy<HttpClient> LazyHttp = new(() => new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>True once a real Gemini request has been prepared in this process (selftest evidence for ADR-29).</summary>
        public static bool HttpClientCreated => LazyHttp.IsValueCreated;

        public static bool MockEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(MockEnvironmentVariable));

        public static int GetSimulatedTransientFailures()
        {
            string? raw = Environment.GetEnvironmentVariable(SimulateTransientFailuresEnvironmentVariable);
            return int.TryParse(raw?.Trim(), out int n) && n > 0 ? n : 0;
        }

        public static (int PromptTokens, int CompletionTokens, int TotalTokens) EstimateMockTokens(int promptChars, int responseChars)
        {
            int promptTokens = Math.Max(10, promptChars / 4);
            int completionTokens = Math.Max(8, responseChars / 4);
            return (promptTokens, completionTokens, promptTokens + completionTokens);
        }

        public Task<GeminiResult> GenerateAsync(string systemInstruction, string userText, NexusConfig config,
            string? apiKey, string primaryAgentName, string squadId, CancellationToken cancellationToken = default,
            string? directive = null, GeminiQuotaGuard? quotaGuard = null, GeminiImageAttachment? image = null) =>
            GenerateAsync(systemInstruction, new[] { new GeminiTurn(TurnHistory.RoleUser, userText) }, config, apiKey,
                primaryAgentName, squadId, cancellationToken, directive, null, quotaGuard, image);

        /// <summary>TASK-09 / TASK-10: multi-turn request with HTTP 429/5xx exponential backoff + jitter and usageMetadata extraction.</summary>
        /// <param name="mockTag">"RETRY-TURN-n" / "FOLLOWUP-TURN-n": mock answers are prefixed "[MOCK-&lt;tag&gt;]" and echo
        /// what the request actually carried (ADR-38). Ignored by real requests.</param>
        public async Task<GeminiResult> GenerateAsync(string systemInstruction, IReadOnlyList<GeminiTurn> contents, NexusConfig config,
            string? apiKey, string primaryAgentName, string squadId, CancellationToken cancellationToken = default,
            string? directive = null, string? mockTag = null, GeminiQuotaGuard? quotaGuard = null, GeminiImageAttachment? image = null)
        {
            var stopwatch = Stopwatch.StartNew();
            string? mock = Environment.GetEnvironmentVariable(MockEnvironmentVariable);
            int simulateFailures = GetSimulatedTransientFailures();
            int maxRetries = Math.Clamp(config.HttpMaxRetries, 0, 5);
            int httpRetries = 0;

            if (string.IsNullOrEmpty(mock) && string.IsNullOrWhiteSpace(apiKey) && simulateFailures == 0)
            {
                return new GeminiResult(false, AgentConsultService.MissingKeyMessage, string.Empty, 0, null, "MissingKey");
            }

            string configuredModel = config.GeminiModel;
            // TASK-11b: reuse a fallback learned earlier in this process only for the same configured model.
            string model = !string.IsNullOrEmpty(mock)
                ? MockModel
                : (_autoUpgradedModel != null && string.Equals(_autoUpgradedFrom, configuredModel, StringComparison.OrdinalIgnoreCase)
                    ? _autoUpgradedModel
                    : configuredModel);
            string? FallbackNote() => string.IsNullOrEmpty(mock) && !string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase)
                ? $"[MODEL FALLBACK: {configuredModel} -> {model}]"
                : null;
            string fullDirective = directive ?? HudDirective;
            bool modelFallbackTried = false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.GeminiTimeoutSeconds));

            try
            {
                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    if (attempt < simulateFailures)
                    {
                        const string simulatedJson = "{\"error\":{\"message\":\"Simulated HTTP 429 Too Many Requests (NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES)\"}}";
                        if (attempt < maxRetries)
                        {
                            httpRetries++;
                            quotaGuard?.RecordHttpRetry();
                            int delayMs = ComputeBackoffDelayMs(attempt, config.HttpRetryBaseDelayMs, null);
                            await Task.Delay(delayMs, timeout.Token).ConfigureAwait(false);
                            continue;
                        }
                        return new GeminiResult(false, Scrub(ErrorText(429, simulatedJson), apiKey), model,
                            (int)stopwatch.ElapsedMilliseconds, 429, "Http", 0, 0, 0, httpRetries);
                    }

                    if (!string.IsNullOrEmpty(mock))
                    {
                        string body = mock is "1" or "true" or "TRUE" ? "Phản hồi giả lập kiểm thử" : mock;
                        string text = $"[MOCK GEMINI] {body} | Primary: {primaryAgentName} | {squadId}";
                        if (mockTag != null) text = $"[MOCK-{mockTag}] {text}{MockEcho(mockTag, contents)}";
                        if (image != null) text = $"{image.MockTag} {text}"; // TASK-12: proves what the request carried
                        int promptChars = contents.Sum(c => c.Text.Length) + (image != null ? GeminiImageAttachment.EstimatedPromptTokens * 4 : 0);
                        var (pt, ct, tt) = EstimateMockTokens(promptChars, text.Length);
                        return new GeminiResult(true, text, MockModel, (int)stopwatch.ElapsedMilliseconds, null, null, pt, ct, tt, httpRetries);
                    }

                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        return new GeminiResult(false, AgentConsultService.MissingKeyMessage, string.Empty,
                            (int)stopwatch.ElapsedMilliseconds, null, "MissingKey", 0, 0, 0, httpRetries);
                    }

                    var request = new JsonObject
                    {
                        ["system_instruction"] = new JsonObject
                        {
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstruction + "\n\n" + fullDirective } }
                        },
                        ["contents"] = new JsonArray(contents.Select((turn, index) => (JsonNode)new JsonObject
                        {
                            ["role"] = turn.Role == TurnHistory.RoleModel ? "model" : "user",
                            ["parts"] = image != null && index == contents.Count - 1 && turn.Role != TurnHistory.RoleModel
                                ? new JsonArray // TASK-12 ADR-61: image first, then the question
                                {
                                    new JsonObject
                                    {
                                        ["inline_data"] = new JsonObject
                                        {
                                            ["mime_type"] = image.MimeType,
                                            ["data"] = Convert.ToBase64String(image.Bytes)
                                        }
                                    },
                                    new JsonObject { ["text"] = turn.Text }
                                }
                                : new JsonArray { new JsonObject { ["text"] = turn.Text } }
                        }).ToArray()),
                        ["generationConfig"] = new JsonObject
                        {
                            ["temperature"] = config.GeminiTemperature,
                            ["maxOutputTokens"] = config.GeminiMaxOutputTokens
                        }
                    };

                    using var message = new HttpRequestMessage(HttpMethod.Post, string.Format(Endpoint, Uri.EscapeDataString(model)))
                    {
                        Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
                    };
                    message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using HttpResponseMessage response = await LazyHttp.Value.SendAsync(message, timeout.Token).ConfigureAwait(false);
                    string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    int latency = (int)stopwatch.ElapsedMilliseconds;
                    int status = (int)response.StatusCode;

                    if (!response.IsSuccessStatusCode)
                    {
                        if (status == 404 && !modelFallbackTried && !string.Equals(model, FallbackModel, StringComparison.OrdinalIgnoreCase))
                        {
                            modelFallbackTried = true;
                            model = FallbackModel;
                            _autoUpgradedFrom = configuredModel;
                            _autoUpgradedModel = model;
                            attempt--;
                            continue;
                        }
                        if (IsTransientStatus(status) && attempt < maxRetries)
                        {
                            httpRetries++;
                            quotaGuard?.RecordHttpRetry();
                            int delayMs = ComputeBackoffDelayMs(attempt, config.HttpRetryBaseDelayMs, response.Headers.RetryAfter);
                            await Task.Delay(delayMs, timeout.Token).ConfigureAwait(false);
                            continue;
                        }
                        return new GeminiResult(false, Scrub(ErrorText(status, json), apiKey), model, latency, status, "Http", 0, 0, 0, httpRetries,
                            ModelFallback: FallbackNote());
                    }
                    return ParseAnswer(json, model, latency, status, apiKey, httpRetries) with { ModelFallback = FallbackNote() };
                }

                return new GeminiResult(false, "[GEMINI API ERROR HTTP 429] Quota exceeded / Rate limited.", model,
                    (int)stopwatch.ElapsedMilliseconds, 429, "Http", 0, 0, 0, httpRetries);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new GeminiResult(false, $"[GEMINI TIMEOUT] Không nhận được phản hồi sau {config.GeminiTimeoutSeconds}s (geminiTimeoutSeconds).",
                    model, (int)stopwatch.ElapsedMilliseconds, null, "Timeout", 0, 0, 0, httpRetries);
            }
            catch (HttpRequestException ex)
            {
                return new GeminiResult(false, Scrub($"[GEMINI NETWORK ERROR] {ex.Message}", apiKey), model,
                    (int)stopwatch.ElapsedMilliseconds, null, "Network", 0, 0, 0, httpRetries);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return new GeminiResult(false, Scrub($"[GEMINI CLIENT ERROR] {ex.GetType().Name}: {ex.Message}", apiKey), model,
                    (int)stopwatch.ElapsedMilliseconds, null, "Client", 0, 0, 0, httpRetries);
            }
        }

        private static volatile string? _autoUpgradedModel;
        private static volatile string? _autoUpgradedFrom;

        private static bool IsTransientStatus(int status) =>
            status is 429 or 500 or 502 or 503 or 504;

        /// <summary>
        /// TASK-10 §3.3 (ADR-42): respects Retry-After if &lt;= 3 seconds; otherwise httpRetryBaseDelayMs * 2^attempt + Random(0..100),
        /// clamped to &lt;= 2500 ms so a single retry never stalls the background worker.
        /// </summary>
        public static int ComputeBackoffDelayMs(int attempt, int baseDelayMs, RetryConditionHeaderValue? retryAfter)
        {
            if (retryAfter != null)
            {
                TimeSpan? delta = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);
                if (delta.HasValue && delta.Value.TotalMilliseconds > 0 && delta.Value.TotalSeconds <= 3.0)
                {
                    return Math.Clamp((int)delta.Value.TotalMilliseconds, 50, 2500);
                }
            }
            int safeBase = Math.Clamp(baseDelayMs, 50, 2500);
            double exp = safeBase * Math.Pow(2, Math.Clamp(attempt, 0, 5));
            int jitter = Random.Shared.Next(0, 101);
            return (int)Math.Clamp(exp + jitter, 50, 2500);
        }

        public const string RetryTaskSection = "### 1. Nhiệm vụ gốc";
        public const string RetryPreviousSection = "### 2. Câu trả lời trước của bạn";
        public const string RetryQaSection = "### 3. Nhận xét QA / lý do trượt";
        public const string RetryHintSection = "### 4. Gợi ý bổ sung của người dùng";

        /// <summary>
        /// ADR-38 mock evidence, computed from the request itself (not from the caller): the number of turns sent and, for a
        /// retry, the start of the QA-critique and hint sections of the prompt; for a follow-up, the start of the previous
        /// model turn and of the new question. Verify-Task09 checks these against what is stored in SQLite.
        /// </summary>
        private static string MockEcho(string tag, IReadOnlyList<GeminiTurn> contents)
        {
            int users = contents.Count(t => t.Role != TurnHistory.RoleModel);
            var sb = new StringBuilder();
            sb.Append($" | contents={contents.Count} (user={users}, model={contents.Count - users})");
            string last = contents.Count > 0 ? contents[contents.Count - 1].Text : string.Empty;
            if (tag.StartsWith("RETRY", StringComparison.Ordinal))
            {
                sb.Append($" | task=\"{Excerpt(Section(last, RetryTaskSection), 60)}\"");
                sb.Append($" | prev_answer=\"{Excerpt(Section(last, RetryPreviousSection), 60)}\"");
                sb.Append($" | qa_seen=\"{Excerpt(Section(last, RetryQaSection), 160)}\"");
                sb.Append($" | hint=\"{Excerpt(Section(last, RetryHintSection), 100)}\"");
            }
            else
            {
                GeminiTurn? previous = contents.Take(Math.Max(0, contents.Count - 1)).LastOrDefault(t => t.Role == TurnHistory.RoleModel);
                sb.Append($" | prev_model=\"{Excerpt(previous?.Text ?? string.Empty, 80)}\"");
                sb.Append($" | ask=\"{Excerpt(last, 100)}\"");
            }
            return sb.ToString();
        }

        private static string Section(string text, string header)
        {
            int start = text.IndexOf(header, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += header.Length;
            int end = text.IndexOf("\n### ", start, StringComparison.Ordinal);
            return (end < 0 ? text.Substring(start) : text.Substring(start, end - start)).Trim();
        }

        private static string Excerpt(string text, int max)
        {
            string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("\"", "'");
            return flat.Length > max ? flat.Substring(0, max) + "…" : flat;
        }

        private static GeminiResult ParseAnswer(string json, string model, int latency, int status, string apiKey, int httpRetries)
        {
            JsonNode? root = JsonNode.Parse(json);
            string? modelVersion = root?["modelVersion"]?.GetValue<string>();
            JsonArray? parts = root?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            string text = parts == null
                ? string.Empty
                : string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? string.Empty)).Trim();

            JsonNode? usage = root?["usageMetadata"];
            int promptTokens = usage?["promptTokenCount"]?.GetValue<int>() ?? 0;
            int completionTokens = usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0;
            int totalTokens = usage?["totalTokenCount"]?.GetValue<int>() ?? (promptTokens + completionTokens);
            if (completionTokens == 0 && totalTokens > promptTokens)
            {
                completionTokens = totalTokens - promptTokens;
            }
            if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
            {
                totalTokens = promptTokens + completionTokens;
            }

            if (text.Length == 0)
            {
                string? blocked = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
                string? finish = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
                return new GeminiResult(false,
                    blocked != null ? $"[GEMINI BLOCKED] {blocked}" : $"[GEMINI EMPTY] Không có nội dung (finishReason={finish ?? "?"}).",
                    modelVersion ?? model, latency, status, "Empty", promptTokens, completionTokens, totalTokens, httpRetries);
            }

            string? finishReason = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
            if (finishReason == "MAX_TOKENS") text += "\n[… cắt bớt: đạt geminiMaxOutputTokens]";
            return new GeminiResult(true, Scrub(text, apiKey), modelVersion ?? model, latency, status, null,
                promptTokens, completionTokens, totalTokens, httpRetries, IsLiveApi: true);
        }

        private static string ErrorText(int status, string json)
        {
            string detail = string.Empty;
            try
            {
                detail = JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>() ?? string.Empty;
            }
            catch (JsonException)
            {
                // not JSON: keep it short below
            }
            if (detail.Length == 0) detail = json.Length > 120 ? json.Substring(0, 120) : json;
            if (detail.Length > 160) detail = detail.Substring(0, 160) + "…";

            string hint = status switch
            {
                400 => "Yêu cầu không hợp lệ (khóa sai định dạng hoặc model không tồn tại?)",
                401 or 403 => "Khóa không hợp lệ hoặc không có quyền",
                404 => "Không tìm thấy model (kiểm tra geminiModel)",
                429 => "Quota exceeded / Rate limited",
                >= 500 => "Máy chủ Gemini lỗi tạm thời",
                _ => "Lỗi HTTP"
            };
            return $"[GEMINI API ERROR HTTP {status}] {hint}. {detail}".Trim();
        }

        /// <summary>Defence in depth: no message may ever echo the key.</summary>
        private static string Scrub(string text, string? apiKey) =>
            DpapiSecretVault.Redact(string.IsNullOrEmpty(apiKey) ? text : text.Replace(apiKey, "***", StringComparison.Ordinal));
    }

    /// <summary>TASK-08 ADR-28: synchronous path of /ask (or /qa) as seen by the Cockpit, in milliseconds.</summary>
    /// <remarks>DispatchMs (route + persona + SQLite INSERT) and LatestFileMs (latest_dispatch.json) split DispatchAndDbMs.</remarks>
    public sealed record AskSyncBreakdown(double GuardMs, double DispatchAndDbMs, double TaskSpawnMs, double TotalSyncMs,
        double DispatchMs = 0, double LatestFileMs = 0, DispatchTimings? Dispatch = null, QaCacheHitTimings? QaCache = null,
        ConsultCacheHitTimings? ConsultCache = null);

    /// <summary>TASK-11b profile: steps inside the SQLite transaction of an /ask or /ground cache hit (ms).</summary>
    public sealed record ConsultCacheTxTimings(double OpenAndBeginMs, double CacheUpdateMs, double SavedSumMs,
        double HistoryJsonMs, double InsertDispatchMs, double CommitMs);

    /// <summary>
    /// TASK-11b profile: steps of an /ask or /ground cache hit (ms). GuardMs = key-leak regex, GroundingSearchMs = Vault
    /// search (/ground only), RouteMs = in-memory squad routing, SerializeMs / TempWriteMs / FileMoveMs = ADR-14 write of
    /// latest_dispatch.json (JSON bytes, temp file, File.Move).
    /// </summary>
    public sealed record ConsultCacheHitTimings(double GuardMs, double GroundingSearchMs, double BuildKeyMs, double RouteMs,
        double TransactionMs, ConsultCacheTxTimings? Tx, double SnapshotMs, double SerializeMs, double TempWriteMs,
        double FileMoveMs, double TotalSyncMs);

    /// <summary>TASK-11: steps inside the SQLite transaction of a /qa cache hit (ms).</summary>
    public sealed record QaCacheTxTimings(double OpenAndBeginMs, double CacheUpdateMs, double SavedSumMs,
        double BuildVerdictMs, double DispatchUpdateMs, double CommitMs);

    /// <summary>TASK-11: steps of TryExecuteCachedQa on a hit (ms); Tx = null on a miss.</summary>
    /// <remarks>TASK-11b: AtomicWriteMs = ADR-14 temp file + File.Move of latest_dispatch.json (was WriteFastMs, in place).</remarks>
    public sealed record QaCacheHitTimings(double BuildKeyMs, double TransactionMs, QaCacheTxTimings? Tx,
        double SnapshotMs, double AtomicWriteMs);

    public sealed record QaStart(long DispatchId, RecentDispatch? Row, LatestDispatchSnapshot? Snapshot, string? Error);

    public sealed record QaOutcome(
        long DispatchId,
        bool Reviewed,
        string? Verdict,
        string Status,
        int RetryCount,
        string QaAgent,
        string QaResponse,
        int QaLatencyMs,
        string KeySource,
        bool EscalatedNow,
        LatestDispatchSnapshot? Snapshot,
        string? Error,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        int HttpRetryCount = 0,
        bool CacheHit = false,
        long SavedTotalTokens = 0);

    public sealed record ConsultStart(DispatchPackage? Package, string? Error, LatestDispatchSnapshot? Snapshot, string? FileError,
        double DispatchMs = 0, double LatestFileMs = 0, DispatchTimings? DispatchTimings = null);

    /// <summary>TASK-09: synchronous part of /retry and /followup. Blocked = refused by the 3-strike circuit breaker.</summary>
    public sealed record RefineStart(long DispatchId, RecentDispatch? Row, LatestDispatchSnapshot? Snapshot, string? Error, bool Blocked = false);

    /// <summary>TASK-09 / TASK-10: background part of /retry ("retry") or /followup ("followup").</summary>
    public sealed record RefineOutcome(
        long DispatchId,
        string Kind,
        bool AiOk,
        bool Blocked,
        string AiResponse,
        string AiModel,
        int AiLatencyMs,
        string KeySource,
        int TurnCount,
        int ContentsSent,
        RecentDispatch? Row,
        LatestDispatchSnapshot? Snapshot,
        string? Error,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        int HttpRetryCount = 0);

    public sealed record ConsultOutcome(
        long DispatchId,
        bool AiOk,
        string AiResponse,
        string AiModel,
        int AiLatencyMs,
        string KeySource,
        RecentDispatch? Row,
        LatestDispatchSnapshot? Snapshot,
        string? FileError,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        int HttpRetryCount = 0,
        bool CacheHit = false,
        long PromptTokensSaved = 0,
        long CompletionTokensSaved = 0,
        long SavedTotalTokens = 0,
        IReadOnlyList<string>? GroundingSources = null);

    /// <summary>
    /// TASK-07 §2.3 / §3.4 / TASK-10 / TASK-11: one flow for Cockpit "/ask", "/ground" ("/g") and MCP "consult_gemini_agent".
    /// <see cref="Start"/> is synchronous and fast (route + DISPATCHED row + latest_dispatch.json "ask" = THINKING);
    /// <see cref="CompleteAsync"/> resolves the key, calls Gemini and stores ai_response / ai_model / ai_latency_ms
    /// plus token usage and caches the response in <see cref="GeminiResponseCache"/> (ADR-44).
    /// Never throws: a missing key or an API failure becomes the stored diagnostic text, the dispatch stays valid.
    /// </summary>
    public sealed class AgentConsultService
    {
        public const string MissingKeyMessage =
            "[LOCAL DISPATCH ONLY] Chưa cấu hình Gemini API Key. Hãy gõ /apikey set <KEY> để kích hoạt.";

        private readonly SquadDispatcher _dispatcher;
        private readonly SqliteMemoryStore _store;
        private readonly AgentRegistry _registry;
        private readonly NexusConfigService _config;
        private readonly string _latestPath;
        private readonly string _source;
        private readonly GeminiAgentClient _client;
        private readonly DispatchVerdictService _verdicts;

        public AgentConsultService(NexusRuntime runtime, DpapiSecretVault? vault = null, GeminiAgentClient? client = null, GeminiQuotaGuard? quotaGuard = null)
        {
            _verdicts = runtime.Verdicts;
            _dispatcher = runtime.Dispatcher;
            _store = runtime.Store;
            _registry = runtime.Registry;
            _config = runtime.Config;
            _latestPath = runtime.LatestDispatchPath;
            _source = runtime.Source;
            Vault = vault ?? runtime.Vault;
            _client = client ?? new GeminiAgentClient();
            QuotaGuard = quotaGuard ?? runtime.QuotaGuard;
            ResponseCache = runtime.ResponseCache;
            Grounding = runtime.Grounding;
        }

        public DpapiSecretVault Vault { get; }

        public GeminiQuotaGuard QuotaGuard { get; }

        public GeminiResponseCache ResponseCache { get; }

        public VaultGroundingEngine Grounding { get; }

        /// <summary>
        /// TASK-11 ADR-44: fast synchronous cache-hit path (&lt; 2 ms, 0 quota/tokens deducted).
        /// Returns true if an unexpired entry matched <c>model_id | normalized_prompt | grounding_fingerprint</c>.
        /// </summary>
        public bool TryExecuteCachedConsult(
            string task,
            string? nexusMode,
            string? preferredAgentId,
            string commandType,
            string? groundingFingerprint,
            IReadOnlyList<string>? groundingSources,
            out DispatchPackage? package,
            out ConsultOutcome? outcome,
            out double dispatchAndDbMs,
            out double latestFileMs,
            bool rollbackWarmup = false) =>
            TryExecuteCachedConsult(task, nexusMode, preferredAgentId, commandType, groundingFingerprint, groundingSources,
                out package, out outcome, out dispatchAndDbMs, out latestFileMs, out _, rollbackWarmup);

        /// <param name="timings">TASK-11b profile of the hit (GuardMs, GroundingSearchMs and TotalSyncMs are filled by the caller).</param>
        public bool TryExecuteCachedConsult(
            string task,
            string? nexusMode,
            string? preferredAgentId,
            string commandType,
            string? groundingFingerprint,
            IReadOnlyList<string>? groundingSources,
            out DispatchPackage? package,
            out ConsultOutcome? outcome,
            out double dispatchAndDbMs,
            out double latestFileMs,
            out ConsultCacheHitTimings? timings,
            bool rollbackWarmup = false)
        {
            package = null;
            outcome = null;
            dispatchAndDbMs = 0;
            latestFileMs = 0;
            timings = null;

            var sw = Stopwatch.StartNew();
            string modelId = GeminiResponseCache.ResolveModelIdForCache(_config.Current, isQa: false);
            string cacheKey = GeminiResponseCache.ComputeKey(modelId, task, groundingFingerprint);
            double buildKeyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3);

            if (!_dispatcher.TryDispatchFromCache(cacheKey, task, nexusMode ?? _config.Current.DefaultNexusMode, preferredAgentId,
                    out package, out GeminiCacheHit? hit, out RecentDispatch? row, out _,
                    out double routeMs, out ConsultCacheTxTimings? tx, rollbackWarmup) ||
                package == null || hit == null || row == null)
            {
                return false;
            }

            dispatchAndDbMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3);
            double fileStart = sw.Elapsed.TotalMilliseconds;
            var (sessionTotal, dailyTotal) = QuotaGuard.GetCurrentTotals();
            IReadOnlyList<string> effectiveSources = groundingSources is { Count: > 0 } ? groundingSources : hit.GroundingSources;

            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(row, _registry, _source, sessionTotal, dailyTotal) with
            {
                Event = commandType == "ground" ? "ground_cache_hit" : "ai_response",
                ElapsedMs = package.ElapsedMs,
                CacheHit = true,
                PromptTokensSaved = hit.PromptTokensSaved,
                CompletionTokensSaved = hit.CompletionTokensSaved,
                SavedTotalTokens = hit.CumulativeSavedTotalTokens,
                GroundingSources = effectiveSources,
                // TASK-11b: cache_lookup_ms = SHA-256 key + in-memory routing + the SQLite WAL transaction (cache UPDATE
                // RETURNING + dispatch INSERT); total_dispatch_ms = everything before this file write (a file cannot hold
                // the duration of its own write; the full time incl. the ADR-14 write is the command's SyncTiming).
                CacheLookupMs = dispatchAndDbMs,
                TotalDispatchMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3)
            };
            double snapshotMs = Math.Round(sw.Elapsed.TotalMilliseconds - fileStart, 3);
            string targetPath = rollbackWarmup ? _latestPath + ".warmup.tmp" : _latestPath;
            string? fileError = LatestDispatchFile.Write(targetPath, snapshot, out double serializeMs, out double tempWriteMs, out double moveMs);
            if (rollbackWarmup)
            {
                try { File.Delete(targetPath); } catch (IOException) { }
            }
            latestFileMs = Math.Round(sw.Elapsed.TotalMilliseconds - fileStart, 3);
            timings = new ConsultCacheHitTimings(0, 0, buildKeyMs, routeMs,
                Math.Round(dispatchAndDbMs - buildKeyMs - routeMs, 3), tx, snapshotMs, serializeMs, tempWriteMs, moveMs, 0);

            outcome = new ConsultOutcome(
                DispatchId: row.Id,
                AiOk: true,
                AiResponse: hit.ResponseText,
                AiModel: hit.ModelId,
                AiLatencyMs: 0,
                KeySource: "Cache",
                Row: row,
                Snapshot: snapshot,
                FileError: fileError,
                PromptTokens: 0,
                CompletionTokens: 0,
                TotalTokens: 0,
                HttpRetryCount: 0,
                CacheHit: true,
                PromptTokensSaved: hit.PromptTokensSaved,
                CompletionTokensSaved: hit.CompletionTokensSaved,
                SavedTotalTokens: hit.CumulativeSavedTotalTokens,
                GroundingSources: effectiveSources);
            return true;
        }

        /// <param name="guardChecked">True when the caller already ran the key-leak guard (Cockpit /ask times it as GuardMs).</param>
        public ConsultStart Start(string task, string? nexusMode, string? preferredAgentId, bool guardChecked = false,
            IReadOnlyList<string>? groundingSources = null)
        {
            if (!guardChecked && DpapiSecretVault.GoogleApiKeyPattern.IsMatch(task))
                return new ConsultStart(null, "Câu hỏi có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", null, null);

            var timer = Stopwatch.StartNew();
            DispatchOutcome outcome = _dispatcher.Dispatch(task, nexusMode ?? _config.Current.DefaultNexusMode, preferredAgentId);
            double dispatchMs = timer.Elapsed.TotalMilliseconds;
            if (outcome.Package == null) return new ConsultStart(null, outcome.Error ?? "Dispatch failed.", null, null, dispatchMs);

            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromDispatch(outcome.Package, task, _source) with
            {
                Event = groundingSources is { Count: > 0 } ? "ground" : "ask",
                GroundingSources = groundingSources ?? Array.Empty<string>()
            };
            string? fileError = LatestDispatchFile.Write(_latestPath, snapshot);
            double fileMs = timer.Elapsed.TotalMilliseconds - dispatchMs;
            return new ConsultStart(outcome.Package, null, snapshot, fileError, Math.Round(dispatchMs, 3), Math.Round(fileMs, 3), outcome.Timings);
        }

        public async Task<ConsultOutcome> CompleteAsync(
            DispatchPackage package,
            string task,
            CancellationToken cancellationToken = default,
            bool quotaSlotAcquired = false,
            string commandType = "ask",
            string? promptForAi = null,
            string? groundingFingerprint = null,
            IReadOnlyList<string>? groundingSources = null)
        {
            string keySource = "None";
            GeminiResult result;
            string effectivePrompt = string.IsNullOrWhiteSpace(promptForAi) ? task : promptForAi;
            try
            {
                if (!quotaSlotAcquired && !QuotaGuard.TryAcquireRequest(_config.Current, _store, out string? limitKind))
                {
                    _store.WriteAuditLog("gemini_quota_guard", ActionRiskLevel.Green_SafeRead,
                        JsonSerializer.Serialize(new { dispatchId = package.DispatchId, limit = limitKind, source = _source }),
                        $"Blocked_{limitKind}");
                    result = new GeminiResult(false, GeminiQuotaGuard.BlockMessage, string.Empty, 0, null, "QuotaGuard");
                }
                else
                {
                    ApiKeyResolution key = GeminiAgentClient.MockEnabled ? new ApiKeyResolution(ApiKeySource.Missing, null, null) : Vault.Resolve();
                    keySource = GeminiAgentClient.MockEnabled ? "Mock" : key.SourceLabel;
                    result = await _client.GenerateAsync(package.SystemInstruction, effectivePrompt, _config.Current, key.Key,
                        package.PrimaryAgent.Name, package.SquadId, cancellationToken, null, QuotaGuard).ConfigureAwait(false);
                    NoteModelFallback(result, package.DispatchId);
                    if (result.TotalTokens > 0)
                    {
                        QuotaGuard.RecordTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                    }
                }
            }
            catch (Exception ex)
            {
                result = new GeminiResult(false, $"[GEMINI CLIENT ERROR] {ex.GetType().Name}", string.Empty, 0, null, "Client");
            }

            RecentDispatch? row = null;
            string? fileError = null;
            LatestDispatchSnapshot? snapshot = null;
            long savedTotalTokens = 0;
            IReadOnlyList<string> effectiveSources = groundingSources ?? Array.Empty<string>();
            try
            {
                if (result.Ok)
                {
                    string cacheModelId = GeminiResponseCache.ResolveModelIdForCache(_config.Current, isQa: false);
                    string cacheKey = GeminiResponseCache.ComputeKey(cacheModelId, task, groundingFingerprint);
                    ResponseCache.Put(
                        cacheKey: cacheKey,
                        commandType: commandType,
                        modelId: result.Model.Length > 0 ? result.Model : cacheModelId,
                        normalizedPrompt: task,
                        groundingFingerprint: groundingFingerprint,
                        groundingSources: effectiveSources,
                        responseText: result.Text,
                        promptTokens: result.PromptTokens,
                        completionTokens: result.CompletionTokens);
                }

                savedTotalTokens = ResponseCache.GetStats().SavedTotalTokens;
                row = _store.SaveAiResponse(package.DispatchId, result.Text, result.Model, result.LatencyMs,
                    TurnHistory.FirstTurn(task, result.Text, result.Ok),
                    result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                if (row != null)
                {
                    var (sessionTotal, dailyTotal) = QuotaGuard.GetCurrentTotals();
                    snapshot = LatestDispatchFile.FromRecord(row, _registry, _source, sessionTotal, dailyTotal) with
                    {
                        Event = "ai_response",
                        ElapsedMs = package.ElapsedMs,
                        CacheHit = false,
                        SavedTotalTokens = savedTotalTokens,
                        GroundingSources = effectiveSources
                    };
                    fileError = LatestDispatchFile.Write(_latestPath, snapshot);
                }
                _store.WriteAuditLog("gemini_consult", ActionRiskLevel.Green_SafeRead,
                    JsonSerializer.Serialize(new
                    {
                        dispatchId = package.DispatchId,
                        commandType,
                        model = result.Model,
                        ok = result.Ok,
                        latencyMs = result.LatencyMs,
                        promptTokens = result.PromptTokens,
                        completionTokens = result.CompletionTokens,
                        totalTokens = result.TotalTokens,
                        httpRetryCount = result.HttpRetryCount,
                        cacheHit = false,
                        groundingSources = effectiveSources,
                        keySource,
                        error = result.ErrorKind
                    }),
                    result.Ok ? "Answered" : result.ErrorKind ?? "Failed");
            }
            catch (Exception ex)
            {
                fileError = $"SQLite: {ex.Message}";
            }

            return new ConsultOutcome(package.DispatchId, result.Ok, result.Text, result.Model, result.LatencyMs, keySource,
                row, snapshot, fileError, result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.HttpRetryCount,
                CacheHit: false, PromptTokensSaved: 0, CompletionTokensSaved: 0, SavedTotalTokens: savedTotalTokens,
                GroundingSources: effectiveSources);
        }

        // ───────────── TASK-09: self-correction /retry (ADR-36) and multi-turn /followup (ADR-37) ─────────────

        public const string RetryKind = "retry";
        public const string FollowUpKind = "followup";

        public static string CircuitBreakerMessage(long dispatchId) =>
            $"[3-STRIKE CIRCUIT BREAKER] Dispatch #{dispatchId} is ESCALATED. Use /retry --force to override.";

        /// <summary>ADR-36 guard: ESCALATED, or retry_count already at the strike limit.</summary>
        public static bool IsEscalated(RecentDispatch row) =>
            row.Status == "ESCALATED" || row.RetryCount >= DispatchVerdictService.StrikeLimit;

        /// <summary>
        /// TASK-10 ADR-43: each turn can store up to 3 entries (user, model, qa), so cap turn_history_json at
        /// maxConversationTurns * 3 (18 entries when maxConversationTurns = 6) so QA entries never evict user/model pairs.
        /// </summary>
        private int MaxHistoryEntries => _config.Current.MaxConversationTurns * 3;

        public RecentDispatch? GetDispatch(long dispatchId) => _store.GetDispatch(dispatchId);

        /// <summary>TASK-11b Hạng mục D: a model switch is written to system_audit_logs, never silent.</summary>
        private void NoteModelFallback(GeminiResult result, long dispatchId)
        {
            if (result.ModelFallback == null) return;
            _store.WriteAuditLog("gemini_model_fallback", ActionRiskLevel.Green_SafeRead,
                JsonSerializer.Serialize(new { dispatchId, configured = _config.Current.GeminiModel, used = result.Model, ok = result.Ok }),
                result.ModelFallback);
        }

        /// <summary>
        /// Synchronous part of /retry and MCP retry_gemini_dispatch: key guard on the hint, resolve the dispatch (null = newest),
        /// 3-strike circuit breaker (no Gemini call, nothing changed, one audit row), then latest_dispatch.json event "retry"
        /// (RETRYING) unless <paramref name="writeFile"/> is false (the Cockpit shows the snapshot itself). No DPAPI, no HTTP.
        /// </summary>
        public RefineStart StartRetry(long? dispatchId, string? hint, bool force, bool writeFile = true)
        {
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(hint ?? string.Empty))
                return new RefineStart(0, null, null, "Gợi ý có chuỗi giống API key: không gửi, không lưu.");
            (long id, RecentDispatch? row, string? error) = Resolve(dispatchId);
            if (row == null) return new RefineStart(id, null, null, error);

            LatestDispatchSnapshot current = LatestDispatchFile.FromRecord(row, _registry, _source);
            if (IsEscalated(row) && !force)
            {
                _store.WriteAuditLog("gemini_retry_blocked", ActionRiskLevel.Green_SafeRead,
                    JsonSerializer.Serialize(new { dispatchId = row.Id, status = row.Status, retryCount = row.RetryCount, source = _source }),
                    "CircuitBreaker");
                return new RefineStart(row.Id, row, current, CircuitBreakerMessage(row.Id), Blocked: true);
            }
            LatestDispatchSnapshot snapshot = current with { Event = RetryKind };
            if (writeFile) LatestDispatchFile.Write(_latestPath, snapshot);
            return new RefineStart(row.Id, row, snapshot, null);
        }

        /// <summary>Synchronous part of /followup and MCP followup_gemini_dispatch (latest_dispatch.json event "followup").</summary>
        public RefineStart StartFollowUp(long? dispatchId, string? text, bool writeFile = true)
        {
            string question = (text ?? string.Empty).Trim();
            if (question.Length == 0) return new RefineStart(0, null, null, "Thiếu nội dung câu hỏi tiếp theo.");
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(question))
                return new RefineStart(0, null, null, "Câu hỏi có chuỗi giống API key: không gửi, không lưu.");
            if (question.Length > SquadDispatcher.TaskDescriptionMaxChars)
                return new RefineStart(0, null, null, $"Câu hỏi dài quá {SquadDispatcher.TaskDescriptionMaxChars} ký tự.");
            (long id, RecentDispatch? row, string? error) = Resolve(dispatchId);
            if (row == null) return new RefineStart(id, null, null, error);
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(row, _registry, _source) with { Event = FollowUpKind };
            if (writeFile) LatestDispatchFile.Write(_latestPath, snapshot);
            return new RefineStart(row.Id, row, snapshot, null);
        }

        /// <summary>
        /// Background part of /retry: the Primary Agent's SystemInstruction + one prompt with the original task, its previous
        /// answer, the QA critique (qa_response + evidence_notes) and the user's hint. A new answer becomes the next turn and
        /// sets status DISPATCHED (retry_count kept: only a NEEDS_WORK verdict counts a strike).
        /// </summary>
        public async Task<RefineOutcome> CompleteRetryAsync(long dispatchId, string? hint, bool force, CancellationToken cancellationToken = default, bool quotaSlotAcquired = false)
        {
            RecentDispatch? row = _store.GetDispatch(dispatchId);
            if (row == null) return RefineFailure(dispatchId, RetryKind, $"Dispatch #{dispatchId} not found.", null);
            if (IsEscalated(row) && !force)
                return RefineFailure(dispatchId, RetryKind, CircuitBreakerMessage(dispatchId), row, blocked: true);

            var contents = new[] { new GeminiTurn(TurnHistory.RoleUser, BuildRetryPrompt(row, hint)) };
            string userEntry = "[RETRY theo nhận xét QA] " + (string.IsNullOrWhiteSpace(hint) ? "(không có gợi ý bổ sung)" : hint.Trim());
            return await RefineAsync(row, RetryKind, contents, userEntry, resetToDispatched: true, force, cancellationToken, quotaSlotAcquired).ConfigureAwait(false);
        }

        /// <summary>
        /// Background part of /followup: the newest maxConversationTurns user/model pairs of turn_history_json + the new
        /// question as a multi-turn request. The answer becomes the next turn; status and retry_count are left as they are.
        /// Note (ADR-44): /f always calls the API directly with the ADR-43 sliding window (bypasses GeminiResponseCache).
        /// </summary>
        public async Task<RefineOutcome> CompleteFollowUpAsync(long dispatchId, string text, CancellationToken cancellationToken = default, bool quotaSlotAcquired = false)
        {
            RecentDispatch? row = _store.GetDispatch(dispatchId);
            if (row == null) return RefineFailure(dispatchId, FollowUpKind, $"Dispatch #{dispatchId} not found.", null);
            string question = text.Trim();
            ContentsWindow window = TurnHistory.BuildContents(row, _config.Current.MaxConversationTurns, question);
            return await RefineAsync(row, FollowUpKind, window.Contents, question, resetToDispatched: false, false, cancellationToken, quotaSlotAcquired).ConfigureAwait(false);
        }

        private async Task<RefineOutcome> RefineAsync(RecentDispatch row, string kind, IReadOnlyList<GeminiTurn> contents, string userEntry,
            bool resetToDispatched, bool force, CancellationToken cancellationToken, bool quotaSlotAcquired)
        {
            string keySource = "None";
            GeminiResult result;
            try
            {
                if (!quotaSlotAcquired && !QuotaGuard.TryAcquireRequest(_config.Current, _store, out string? limitKind))
                {
                    _store.WriteAuditLog("gemini_quota_guard", ActionRiskLevel.Green_SafeRead,
                        JsonSerializer.Serialize(new { dispatchId = row.Id, kind, limit = limitKind, source = _source }),
                        $"Blocked_{limitKind}");
                    return RefineFailure(row.Id, kind, GeminiQuotaGuard.BlockMessage, row);
                }

                string? instruction = _dispatcher.BuildInstructionFor(row, out string? instructionError);
                if (instruction == null) return RefineFailure(row.Id, kind, instructionError ?? "SystemInstruction unavailable.", row);

                ApiKeyResolution key = GeminiAgentClient.MockEnabled ? new ApiKeyResolution(ApiKeySource.Missing, null, null) : Vault.Resolve();
                keySource = GeminiAgentClient.MockEnabled ? "Mock" : key.SourceLabel;
                AgentName primary = DescribeAgent(row.PrimaryAgent);
                string tag = $"{(kind == RetryKind ? "RETRY" : "FOLLOWUP")}-TURN-{row.TurnCount + 1}";
                result = await _client.GenerateAsync(instruction, contents, _config.Current, key.Key, primary.Name, row.SquadId,
                    cancellationToken, null, tag, QuotaGuard).ConfigureAwait(false);
                NoteModelFallback(result, row.Id);
                if (result.TotalTokens > 0)
                {
                    QuotaGuard.RecordTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                }
            }
            catch (Exception ex)
            {
                result = new GeminiResult(false, $"[GEMINI CLIENT ERROR] {ex.GetType().Name}", string.Empty, 0, null, "Client");
            }

            try
            {
                if (!result.Ok)
                {
                    LatestDispatchSnapshot unchanged = LatestDispatchFile.FromRecord(row, _registry, _source) with { Event = kind + "_failed" };
                    LatestDispatchFile.Write(_latestPath, unchanged);
                    AuditRefine(kind, row.Id, result, keySource, row.TurnCount, contents.Count, force);
                    return new RefineOutcome(row.Id, kind, false, false, result.Text, result.Model, result.LatencyMs, keySource,
                        row.TurnCount, contents.Count, row, unchanged, result.Text,
                        result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.HttpRetryCount);
                }

                RecentDispatch? saved = _store.SaveRefinedTurn(row.Id, result.Text, result.Model, result.LatencyMs,
                    new[] { (Role: TurnHistory.RoleUser, Text: userEntry), (Role: TurnHistory.RoleModel, Text: result.Text) },
                    MaxHistoryEntries, resetToDispatched, result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                if (saved == null) return RefineFailure(row.Id, kind, $"Dispatch #{row.Id} not found.", row);
                var (sessionTotal, dailyTotal) = QuotaGuard.GetCurrentTotals();
                long savedTotalTokens = ResponseCache.GetStats().SavedTotalTokens;
                LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(saved, _registry, _source, sessionTotal, dailyTotal) with
                {
                    Event = kind + "_response",
                    CacheHit = false,
                    SavedTotalTokens = savedTotalTokens
                };
                string? fileError = LatestDispatchFile.Write(_latestPath, snapshot);
                AuditRefine(kind, row.Id, result, keySource, saved.TurnCount, contents.Count, force);
                return new RefineOutcome(saved.Id, kind, true, false, result.Text, result.Model, result.LatencyMs, keySource,
                    saved.TurnCount, contents.Count, saved, snapshot, fileError,
                    result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.HttpRetryCount);
            }
            catch (Exception ex)
            {
                return RefineFailure(row.Id, kind, $"SQLite: {ex.Message}", row);
            }
        }

        private void AuditRefine(string kind, long id, GeminiResult result, string keySource, int turnCount, int contentsSent, bool force) =>
            _store.WriteAuditLog(kind == RetryKind ? "gemini_retry" : "gemini_followup", ActionRiskLevel.Yellow_ReversibleTune,
                JsonSerializer.Serialize(new
                {
                    dispatchId = id,
                    model = result.Model,
                    ok = result.Ok,
                    latencyMs = result.LatencyMs,
                    promptTokens = result.PromptTokens,
                    completionTokens = result.CompletionTokens,
                    totalTokens = result.TotalTokens,
                    httpRetryCount = result.HttpRetryCount,
                    keySource,
                    turnCount,
                    contentsSent,
                    force,
                    error = result.ErrorKind
                }),
                result.Ok ? (kind == RetryKind ? "Refined" : "Answered") : result.ErrorKind ?? "Failed");

        private RefineOutcome RefineFailure(long id, string kind, string error, RecentDispatch? row, bool blocked = false) =>
            new(id, kind, false, blocked, string.Empty, string.Empty, 0, "None", row?.TurnCount ?? 0, 0, row,
                row == null ? null : LatestDispatchFile.FromRecord(row, _registry, _source), error);

        /// <summary>Retry prompt (ADR-36). Section headers are fixed: the mock echoes sections 1-4 (ADR-38).</summary>
        private string BuildRetryPrompt(RecentDispatch row, string? hint)
        {
            string critique = string.Join("\n", new[]
            {
                row.QaResponse.Length > 0 ? row.QaResponse.Trim() : null,
                row.EvidenceNotes.Length > 0 ? $"Evidence notes: {row.EvidenceNotes.Trim()}" : null
            }.Where(x => x != null));
            var sb = new StringBuilder();
            sb.AppendLine($"## SELF-CORRECTION REQUEST: Dispatch #{row.Id}, lượt {row.TurnCount + 1}, strike {row.RetryCount}/{DispatchVerdictService.StrikeLimit}");
            sb.AppendLine();
            sb.AppendLine(GeminiAgentClient.RetryTaskSection);
            sb.AppendLine(row.TaskSummary);
            sb.AppendLine();
            sb.AppendLine(GeminiAgentClient.RetryPreviousSection);
            sb.AppendLine(row.AiResponse.Length > 0 ? row.AiResponse : "(chưa có câu trả lời)");
            sb.AppendLine();
            sb.AppendLine(GeminiAgentClient.RetryQaSection);
            sb.AppendLine(critique.Length > 0 ? critique : "(không có nhận xét QA: người dùng yêu cầu làm lại)");
            sb.AppendLine();
            sb.AppendLine(GeminiAgentClient.RetryHintSection);
            sb.AppendLine(string.IsNullOrWhiteSpace(hint) ? "(không có)" : hint.Trim());
            sb.AppendLine();
            sb.AppendLine("### 5. Yêu cầu");
            sb.AppendLine("Viết lại lời giải hoàn chỉnh: sửa đúng từng điểm QA nêu, giữ phần đã đúng, cuối cùng liệt kê ngắn gọn đã sửa gì.");
            return sb.ToString();
        }

        private (long Id, RecentDispatch? Row, string? Error) Resolve(long? dispatchId)
        {
            long id = dispatchId is > 0 ? dispatchId.Value : _store.GetLatestDispatchId() ?? 0;
            if (id <= 0) return (0, null, "Chưa có lượt điều phối nào trong agent_dispatch_logs.");
            RecentDispatch? row = _store.GetDispatch(id);
            return row == null ? (id, null, $"Dispatch #{id} not found.") : (id, row, null);
        }

        /// <summary>--auto-qa after a /retry or /followup that produced no answer: QA does not run, nothing is written
        /// (the previous QA critique is kept for the next retry).</summary>
        public QaOutcome NotReviewed(long dispatchId, string reason)
        {
            RecentDispatch? row = _store.GetDispatch(dispatchId);
            return new QaOutcome(dispatchId, false, null, row?.Status ?? string.Empty, row?.RetryCount ?? 0,
                row == null ? string.Empty : DescribeAgent(row.QaAgent).Name, string.Empty, 0, "None", false,
                row == null ? null : LatestDispatchFile.FromRecord(row, _registry, _source), reason);
        }

        // ───────────── TASK-08 / TASK-11: Gemini QA agent review (ADR-30/31 + ADR-44 cache) ─────────────

        public const string QaSkippedNoKey = "[LOCAL QA SKIPPED] Chưa cấu hình Gemini API Key.";
        public const string QaSkippedNoAnswer = "[LOCAL QA SKIPPED] Primary Agent chưa có câu trả lời hợp lệ để kiểm định.";

        private static readonly System.Text.RegularExpressions.Regex VerdictLine = new(
            @"VERDICT\s*[:：]\s*(PASSED|PASS|NEEDS[_ ]?WORK|FAILED|FAIL)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Synchronous part of /qa and MCP run_gemini_qa_review: resolve the dispatch (null = newest), refuse key-like
        /// evidence, write latest_dispatch.json event "qa" so every Cockpit shows "QA REVIEWING". No DPAPI, no HTTP.
        /// </summary>
        /// <param name="writeFile">False from the Cockpit (it shows the snapshot itself; keeps file I/O off the sync path).</param>
        public QaStart StartQa(long? dispatchId, string? evidence, bool writeFile = true)
        {
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(evidence ?? string.Empty))
                return new QaStart(0, null, null, "Bằng chứng có chuỗi giống API key: không gửi, không lưu.");
            long id = dispatchId is > 0 ? dispatchId.Value : _store.GetLatestDispatchId() ?? 0;
            if (id <= 0) return new QaStart(0, null, null, "Chưa có lượt điều phối nào trong agent_dispatch_logs.");
            RecentDispatch? row = _store.GetDispatch(id);
            if (row == null) return new QaStart(id, null, null, $"Dispatch #{id} not found.");
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(row, _registry, _source) with { Event = "qa" };
            if (writeFile) LatestDispatchFile.Write(_latestPath, snapshot);
            return new QaStart(id, row, snapshot, null);
        }

        /// <summary>
        /// TASK-11 ADR-44: synchronous QA cache-hit check (&lt; 2 ms, 0 quota/tokens deducted).
        /// </summary>
        public bool TryExecuteCachedQa(RecentDispatch row, string? evidence, out QaOutcome? cachedOutcome, bool rollbackWarmup = false) =>
            TryExecuteCachedQa(row, evidence, out cachedOutcome, out _, rollbackWarmup);

        public bool TryExecuteCachedQa(RecentDispatch row, string? evidence, out QaOutcome? cachedOutcome,
            out QaCacheHitTimings? timings, bool rollbackWarmup = false)
        {
            cachedOutcome = null;
            timings = null;
            long ts0 = Stopwatch.GetTimestamp();
            string ev = (evidence ?? string.Empty).Trim();
            string qaContent = BuildQaContent(row, ev);
            string qaModelId = GeminiResponseCache.ResolveModelIdForCache(_config.Current, isQa: true);
            string cacheKey = GeminiResponseCache.ComputeKey(qaModelId, qaContent, string.Empty);
            long ts1 = Stopwatch.GetTimestamp();

            AgentName qa = DescribeAgent(row.QaAgent);
            string verdict = "PASSED";
            string summary = string.Empty;

            RecentDispatch? updated = _store.TryCacheHitAndApplyQaVerdict(
                cacheKey,
                row.Id,
                DispatchVerdictService.StrikeLimit,
                responseText =>
                {
                    Match m = VerdictLine.Match(responseText);
                    verdict = m.Success && m.Groups[1].Value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "PASSED" : "NEEDS_WORK";
                    summary = Summarize(responseText, m, qa.Name, verdict);

                    List<TurnEntry> entries = TurnHistory.Parse(row.TurnHistoryJson);
                    if (entries.Count == 0 && (row.TaskSummary.Length > 0 || row.AiResponse.Length > 0))
                    {
                        entries = TurnHistory.Parse(TurnHistory.FirstTurn(row.TaskSummary, row.AiResponse, true));
                    }
                    entries.Add(new TurnEntry(Math.Max(1, row.TurnCount), TurnHistory.RoleQa, responseText, DateTime.UtcNow.ToString("o")));
                    string updatedHistoryJson = TurnHistory.Serialize(TurnHistory.Trim(entries, MaxHistoryEntries));
                    return (verdict, summary, updatedHistoryJson);
                },
                out GeminiCacheHit? hit,
                out QaCacheTxTimings? tx,
                rollbackWarmup);
            long ts2 = Stopwatch.GetTimestamp();

            if (hit == null)
            {
                return false;
            }

            RecentDispatch final = updated ?? row;
            string previousStatus = row.Status;
            bool escalatedNow = !rollbackWarmup && verdict == "NEEDS_WORK" && final.Status == "ESCALATED" && previousStatus != "ESCALATED";

            var (sessionTotal, dailyTotal) = QuotaGuard.GetCurrentTotals();
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(final, _registry, _source, sessionTotal, dailyTotal) with
            {
                Event = "qa_verdict",
                CacheHit = true,
                PromptTokensSaved = hit.PromptTokensSaved,
                CompletionTokensSaved = hit.CompletionTokensSaved,
                SavedTotalTokens = hit.CumulativeSavedTotalTokens,
                CacheLookupMs = Ms(ts0, ts2), // TASK-11b: SHA-256 key + SQLite WAL transaction (cache + verdict)
                TotalDispatchMs = Ms(ts0, Stopwatch.GetTimestamp()) // everything before the ADR-14 write below
            };
            long ts3 = Stopwatch.GetTimestamp();
            string targetPath = rollbackWarmup ? _latestPath + ".warmup.tmp" : _latestPath;
            string? fileError = LatestDispatchFile.Write(targetPath, snapshot);
            long ts4 = Stopwatch.GetTimestamp();
            if (rollbackWarmup)
            {
                try { File.Delete(targetPath); } catch (IOException) { }
            }
            else if (escalatedNow)
            {
                // Strike 3 reached from the cache: same report line + audit as DispatchVerdictService, off the sync path.
                _verdicts.RecordCachedEscalationInBackground(final, verdict);
            }

            static double Ms(long from, long to) => Math.Round((to - from) * 1000.0 / Stopwatch.Frequency, 3);
            timings = new QaCacheHitTimings(Ms(ts0, ts1), Ms(ts1, ts2), tx, Ms(ts2, ts3), Ms(ts3, ts4));
            cachedOutcome = new QaOutcome(
                DispatchId: row.Id,
                Reviewed: true,
                Verdict: verdict,
                Status: final.Status,
                RetryCount: final.RetryCount,
                QaAgent: qa.Name,
                QaResponse: hit.ResponseText,
                QaLatencyMs: 0,
                KeySource: "Cache",
                EscalatedNow: escalatedNow,
                Snapshot: snapshot,
                Error: fileError,
                PromptTokens: 0,
                CompletionTokens: 0,
                TotalTokens: 0,
                HttpRetryCount: 0,
                CacheHit: true,
                SavedTotalTokens: hit.CumulativeSavedTotalTokens);
            return true;
        }

        /// <summary>
        /// Background part: QA agent persona (02_Agents_Registry) + task + Primary answer + evidence -> Gemini (or the
        /// NEXUS_GEMINI_QA_MOCK_VERDICT mock) -> "VERDICT: PASSED|NEEDS_WORK" -> qa_response / qa_latency_ms + tokens saved ->
        /// DispatchVerdictService (ADR-19 state machine, 3-strike ESCALATED, one escalation line).
        /// </summary>
        public async Task<QaOutcome> RunQaReviewAsync(long dispatchId, string? evidence, CancellationToken cancellationToken = default, bool quotaSlotAcquired = false)
        {
            RecentDispatch? row = null;
            try
            {
                row = _store.GetDispatch(dispatchId);
                if (row == null) return QaFailure(dispatchId, $"Dispatch #{dispatchId} not found.");
                AgentName qa = DescribeAgent(row.QaAgent);
                string ev = (evidence ?? string.Empty).Trim();

                if (!quotaSlotAcquired && TryExecuteCachedQa(row, ev, out QaOutcome? cachedQa) && cachedQa != null)
                {
                    return cachedQa;
                }

                if (!quotaSlotAcquired && !QuotaGuard.TryAcquireRequest(_config.Current, _store, out string? limitKind))
                {
                    _store.WriteAuditLog("gemini_quota_guard", ActionRiskLevel.Green_SafeRead,
                        JsonSerializer.Serialize(new { dispatchId = row.Id, kind = "qa", limit = limitKind, source = _source }),
                        $"Blocked_{limitKind}");
                    return QaFailure(dispatchId, GeminiQuotaGuard.BlockMessage, row);
                }

                GeminiResult result;
                string keySource;
                string qaInstruction = BuildQaInstruction(row, qa);
                string qaContent = BuildQaContent(row, ev);
                string? mock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
                if (!string.IsNullOrEmpty(mock))
                {
                    keySource = "Mock";
                    result = MockQa(mock, qaContent.Length);
                    QuotaGuard.RecordTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                }
                else
                {
                    ApiKeyResolution key = Vault.Resolve(); // ADR-28: DPAPI only here, on the background task
                    keySource = key.SourceLabel;
                    if (key.Source == ApiKeySource.Missing) return SaveQaOnly(row, qa, QaSkippedNoKey, 0, keySource);
                    result = await _client.GenerateAsync(qaInstruction, qaContent, _config.Current, key.Key,
                        qa.Name, row.SquadId, cancellationToken, GeminiAgentClient.QaDirective, QuotaGuard).ConfigureAwait(false);
                    NoteModelFallback(result, row.Id);
                    if (result.TotalTokens > 0)
                    {
                        QuotaGuard.RecordTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                    }
                    if (!result.Ok) return SaveQaOnly(row, qa, $"[QA NOT RUN] {result.Text}", result.LatencyMs, keySource, result.HttpRetryCount);
                }

                string qaModelId = GeminiResponseCache.ResolveModelIdForCache(_config.Current, isQa: true);
                string qaCacheKey = GeminiResponseCache.ComputeKey(qaModelId, qaContent, string.Empty);
                ResponseCache.Put(
                    cacheKey: qaCacheKey,
                    commandType: "qa",
                    modelId: result.Model.Length > 0 ? result.Model : qaModelId,
                    normalizedPrompt: qaContent,
                    groundingFingerprint: string.Empty,
                    groundingSources: null,
                    responseText: result.Text,
                    promptTokens: result.PromptTokens,
                    completionTokens: result.CompletionTokens);

                Match m = VerdictLine.Match(result.Text);
                string verdict = m.Success && m.Groups[1].Value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "PASSED" : "NEEDS_WORK";
                string summary = Summarize(result.Text, m, qa.Name, verdict);

                _store.SaveQaResponse(row.Id, result.Text, result.LatencyMs, result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.IsLiveApi);
                _store.AppendTurnEntry(row.Id, TurnHistory.RoleQa, result.Text, MaxHistoryEntries); // TASK-09 §3.1.1 / TASK-10 ADR-43
                VerdictOutcome outcome = _verdicts.Apply(row.Id, verdict, summary);
                RecentDispatch final = outcome.Dispatch ?? _store.GetDispatch(row.Id) ?? row;
                long savedTotalTokens = ResponseCache.GetStats().SavedTotalTokens;
                _store.WriteAuditLog("gemini_qa_review", ActionRiskLevel.Yellow_ReversibleTune,
                    JsonSerializer.Serialize(new
                    {
                        dispatchId = row.Id,
                        qaAgent = qa.Id,
                        verdict,
                        status = final.Status,
                        retryCount = final.RetryCount,
                        latencyMs = result.LatencyMs,
                        promptTokens = result.PromptTokens,
                        completionTokens = result.CompletionTokens,
                        totalTokens = result.TotalTokens,
                        httpRetryCount = result.HttpRetryCount,
                        cacheHit = false,
                        keySource
                    }),
                    $"QA {verdict}");
                var (sessionTotal, dailyTotal) = QuotaGuard.GetCurrentTotals();
                LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(final, _registry, _source, sessionTotal, dailyTotal) with
                {
                    Event = "qa_verdict",
                    CacheHit = false,
                    SavedTotalTokens = savedTotalTokens
                };
                LatestDispatchFile.Write(_latestPath, snapshot);
                return new QaOutcome(row.Id, true, verdict, final.Status, final.RetryCount, qa.Name, result.Text, result.LatencyMs,
                    keySource, outcome.EscalatedNow,
                    snapshot,
                    outcome.Error ?? outcome.FileError,
                    result.PromptTokens, result.CompletionTokens, result.TotalTokens, result.HttpRetryCount,
                    CacheHit: false, SavedTotalTokens: savedTotalTokens);
            }
            catch (Exception ex)
            {
                return QaFailure(dispatchId, $"[QA CLIENT ERROR] {ex.GetType().Name}", row);
            }
        }

        /// <summary>/ask --auto-qa when the Primary answer is missing or a diagnostic: record why QA did not run.</summary>
        public QaOutcome SkipQa(long dispatchId, string reason)
        {
            RecentDispatch? row = _store.GetDispatch(dispatchId);
            return row == null ? QaFailure(dispatchId, $"Dispatch #{dispatchId} not found.") : SaveQaOnly(row, DescribeAgent(row.QaAgent), reason, 0, "None");
        }

        private QaOutcome SaveQaOnly(RecentDispatch row, AgentName qa, string message, int latencyMs, string keySource, int httpRetryCount = 0)
        {
            RecentDispatch saved = _store.SaveQaResponse(row.Id, message, latencyMs) ?? row;
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(saved, _registry, _source) with { Event = "qa_skipped" };
            string? fileError = LatestDispatchFile.Write(_latestPath, snapshot);
            return new QaOutcome(row.Id, false, null, saved.Status, saved.RetryCount, qa.Name, message, latencyMs, keySource, false, snapshot, fileError, 0, 0, 0, httpRetryCount);
        }

        private static QaOutcome QaFailure(long dispatchId, string error, RecentDispatch? row = null) =>
            new(dispatchId, false, null, row?.Status ?? string.Empty, row?.RetryCount ?? 0, string.Empty, string.Empty, 0, "None", false, null, error);

        private AgentName DescribeAgent(string agentId) =>
            _registry.TryGet(agentId, out AgentProfile profile) ? new AgentName(profile.Id, profile.Name) : new AgentName(agentId, agentId);

        private string BuildQaInstruction(RecentDispatch row, AgentName qa)
        {
            string persona = $"{qa.Name}: evidence-based QA reviewer.";
            if (_registry.TryGet(row.QaAgent, out AgentProfile profile))
            {
                try
                {
                    persona = _registry.LoadMarkdown(profile).Trim();
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // keep the one-line fallback persona
                }
            }
            var sb = new StringBuilder();
            sb.AppendLine($"# NEXUS QA Review: Dispatch #{row.Id} ({row.SquadId}), mode NEXUS-{row.NexusMode}");
            sb.AppendLine();
            sb.AppendLine($"## 1. Identity: {qa.Name} ({qa.Id})");
            sb.AppendLine(persona);
            sb.AppendLine();
            sb.AppendLine("## 2. Hardware & Environment");
            foreach (string constraint in SquadDispatcher.HardwareConstraints) sb.AppendLine($"- {constraint}");
            sb.AppendLine();
            sb.AppendLine("## 3. Review Rules");
            sb.AppendLine("- Judge only what is shown: the task, the Primary Agent's answer and the additional evidence.");
            sb.AppendLine("- Minimal Change, measurable results (latency, memory, frame time), no secrets, never weaken anti-cheat.");
            return sb.ToString();
        }

        internal string BuildQaContent(RecentDispatch row, string evidence)
        {
            AgentName primary = DescribeAgent(row.PrimaryAgent);
            // TASK-09: after /retry or /followup the answer belongs to the newest user turn, not only to the original task.
            string? latest = row.TurnCount > 1 ? TurnHistory.LatestUserText(row) : null;
            return
                $"Task Summary:\n{row.TaskSummary}\n\n" +
                (latest != null ? $"Current Turn ({row.TurnCount}) Request:\n{latest}\n\n" : string.Empty) +
                $"Primary Agent: {primary.Name} ({primary.Id})\n" +
                $"AI Response:\n{(row.AiResponse.Length > 0 ? row.AiResponse : "(chưa có câu trả lời AI — chỉ kiểm định theo đề bài và bằng chứng)")}\n\n" +
                $"Additional Evidence:\n{(evidence.Length > 0 ? evidence : "(không có)")}";
        }

        /// <summary>"PASSED: Kịch bản UDP đạt chuẩn" -> "VERDICT: PASSED\nKịch bản UDP đạt chuẩn" (ADR-31, no network).</summary>
        private static GeminiResult MockQa(string value, int promptChars)
        {
            string trimmed = value.Trim();
            int colon = trimmed.IndexOf(':');
            string head = colon > 0 ? trimmed.Substring(0, colon).Trim() : trimmed;
            string body = colon > 0 ? trimmed.Substring(colon + 1).Trim() : string.Empty;
            string text = $"VERDICT: {head.ToUpperInvariant()}\n{(body.Length > 0 ? body : "[MOCK QA]")}";
            var (pt, ct, tt) = GeminiAgentClient.EstimateMockTokens(promptChars, text.Length);
            return new GeminiResult(true, text, GeminiAgentClient.QaMockModel, 0, null, null, pt, ct, tt, 0);
        }

        private static string Summarize(string text, Match verdict, string qaName, string normalized)
        {
            string rest = verdict.Success ? text.Remove(verdict.Index, verdict.Length) : text;
            rest = System.Text.RegularExpressions.Regex.Replace(rest, @"\s+", " ").Trim(' ', '-', '*', '.', ':');
            if (rest.Length > 300) rest = rest.Substring(0, 300) + "…";
            return rest.Length > 0 ? $"QA {qaName}: {rest}" : $"QA {qaName}: {normalized}";
        }
    }
}
