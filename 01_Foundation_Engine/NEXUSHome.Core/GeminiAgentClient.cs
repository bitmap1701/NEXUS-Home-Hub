using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>Answer (or diagnostic) for one Gemini call. Text never contains the API key.</summary>
    public sealed record GeminiResult(bool Ok, string Text, string Model, int LatencyMs, int? HttpStatus, string? ErrorKind);

    /// <summary>
    /// TASK-07 §3.3: generateContent client. Key in the x-goog-api-key header (never in the URL). Deterministic mock
    /// when NEXUS_GEMINI_MOCK_RESPONSE is set (no network, model "gemini-mock-verifier"). Every failure becomes a short
    /// diagnostic text instead of an exception (graceful degradation).
    /// </summary>
    public sealed class GeminiAgentClient
    {
        public const string MockEnvironmentVariable = "NEXUS_GEMINI_MOCK_RESPONSE";
        public const string MockModel = "gemini-mock-verifier";
        public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";

        /// <summary>Appended to the dispatch SystemInstruction for answers that fit the MSI G272 HUD.</summary>
        public const string HudDirective =
            "## 7. HUD Answer Format\n" +
            "- Answer in the user's language (Vietnamese if the question is Vietnamese).\n" +
            "- Short, concrete, practical: at most ~200 words, numbered steps or bullets, code only when essential.\n" +
            "- Respect the hardware above: the game/IDE on the GTX 1660 keeps priority; never suggest disabling anti-cheat.";

        // One HttpClient for the process (socket reuse); per-request timeout via CancellationToken.
        private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public static bool MockEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(MockEnvironmentVariable));

        public async Task<GeminiResult> GenerateAsync(string systemInstruction, string userText, NexusConfig config,
            string? apiKey, string primaryAgentName, string squadId, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            string? mock = Environment.GetEnvironmentVariable(MockEnvironmentVariable);
            if (!string.IsNullOrEmpty(mock))
            {
                string body = mock is "1" or "true" or "TRUE" ? "Phản hồi giả lập kiểm thử" : mock;
                return new GeminiResult(true, $"[MOCK GEMINI] {body} | Primary: {primaryAgentName} | {squadId}", MockModel,
                    (int)stopwatch.ElapsedMilliseconds, null, null);
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new GeminiResult(false, AgentConsultService.MissingKeyMessage, string.Empty, 0, null, "MissingKey");
            }

            string model = config.GeminiModel;
            var request = new JsonObject
            {
                ["system_instruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstruction + "\n\n" + HudDirective } }
                },
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = userText } }
                    }
                },
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = config.GeminiTemperature,
                    ["maxOutputTokens"] = config.GeminiMaxOutputTokens
                }
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.GeminiTimeoutSeconds));
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, string.Format(Endpoint, Uri.EscapeDataString(model)))
                {
                    Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
                };
                message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using HttpResponseMessage response = await Http.SendAsync(message, timeout.Token).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                int latency = (int)stopwatch.ElapsedMilliseconds;
                int status = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    return new GeminiResult(false, Scrub(ErrorText(status, json), apiKey), model, latency, status, "Http");
                }
                return ParseAnswer(json, model, latency, status, apiKey);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new GeminiResult(false, $"[GEMINI TIMEOUT] Không nhận được phản hồi sau {config.GeminiTimeoutSeconds}s (geminiTimeoutSeconds).",
                    model, (int)stopwatch.ElapsedMilliseconds, null, "Timeout");
            }
            catch (HttpRequestException ex)
            {
                return new GeminiResult(false, Scrub($"[GEMINI NETWORK ERROR] {ex.Message}", apiKey), model,
                    (int)stopwatch.ElapsedMilliseconds, null, "Network");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return new GeminiResult(false, Scrub($"[GEMINI CLIENT ERROR] {ex.GetType().Name}: {ex.Message}", apiKey), model,
                    (int)stopwatch.ElapsedMilliseconds, null, "Client");
            }
        }

        private static GeminiResult ParseAnswer(string json, string model, int latency, int status, string apiKey)
        {
            JsonNode? root = JsonNode.Parse(json);
            string? modelVersion = root?["modelVersion"]?.GetValue<string>();
            JsonArray? parts = root?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            string text = parts == null
                ? string.Empty
                : string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? string.Empty)).Trim();

            if (text.Length == 0)
            {
                string? blocked = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
                string? finish = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
                return new GeminiResult(false,
                    blocked != null ? $"[GEMINI BLOCKED] {blocked}" : $"[GEMINI EMPTY] Không có nội dung (finishReason={finish ?? "?"}).",
                    modelVersion ?? model, latency, status, "Empty");
            }

            string? finishReason = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
            if (finishReason == "MAX_TOKENS") text += "\n[… cắt bớt: đạt geminiMaxOutputTokens]";
            return new GeminiResult(true, Scrub(text, apiKey), modelVersion ?? model, latency, status, null);
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

    public sealed record ConsultStart(DispatchPackage? Package, string? Error, LatestDispatchSnapshot? Snapshot, string? FileError);

    public sealed record ConsultOutcome(
        long DispatchId,
        bool AiOk,
        string AiResponse,
        string AiModel,
        int AiLatencyMs,
        string KeySource,
        RecentDispatch? Row,
        LatestDispatchSnapshot? Snapshot,
        string? FileError);

    /// <summary>
    /// TASK-07 §2.3 / §3.4: one flow for Cockpit "/ask" and MCP "consult_gemini_agent".
    /// <see cref="Start"/> is synchronous and fast (route + DISPATCHED row + latest_dispatch.json "ask" = THINKING);
    /// <see cref="CompleteAsync"/> resolves the key, calls Gemini and stores ai_response / ai_model / ai_latency_ms,
    /// then rewrites latest_dispatch.json (event "ai_response") so the other process's Cockpit shows the answer.
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

        public AgentConsultService(NexusRuntime runtime, DpapiSecretVault? vault = null, GeminiAgentClient? client = null)
        {
            _dispatcher = runtime.Dispatcher;
            _store = runtime.Store;
            _registry = runtime.Registry;
            _config = runtime.Config;
            _latestPath = runtime.LatestDispatchPath;
            _source = runtime.Source;
            Vault = vault ?? runtime.Vault;
            _client = client ?? new GeminiAgentClient();
        }

        public DpapiSecretVault Vault { get; }

        public ConsultStart Start(string task, string? nexusMode, string? preferredAgentId)
        {
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(task))
                return new ConsultStart(null, "Câu hỏi có chuỗi giống API key: không gửi, không lưu. Dùng /apikey set <KEY>.", null, null);

            DispatchOutcome outcome = _dispatcher.Dispatch(task, nexusMode ?? _config.Current.DefaultNexusMode, preferredAgentId);
            if (outcome.Package == null) return new ConsultStart(null, outcome.Error ?? "Dispatch failed.", null, null);

            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromDispatch(outcome.Package, task, _source) with { Event = "ask" };
            string? fileError = LatestDispatchFile.Write(_latestPath, snapshot);
            return new ConsultStart(outcome.Package, null, snapshot, fileError);
        }

        public async Task<ConsultOutcome> CompleteAsync(DispatchPackage package, string task, CancellationToken cancellationToken = default)
        {
            string keySource = "None";
            GeminiResult result;
            try
            {
                ApiKeyResolution key = GeminiAgentClient.MockEnabled ? new ApiKeyResolution(ApiKeySource.Missing, null, null) : Vault.Resolve();
                keySource = GeminiAgentClient.MockEnabled ? "Mock" : key.SourceLabel;
                result = await _client.GenerateAsync(package.SystemInstruction, task, _config.Current, key.Key,
                    package.PrimaryAgent.Name, package.SquadId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new GeminiResult(false, $"[GEMINI CLIENT ERROR] {ex.GetType().Name}", string.Empty, 0, null, "Client");
            }

            RecentDispatch? row = null;
            string? fileError = null;
            LatestDispatchSnapshot? snapshot = null;
            try
            {
                row = _store.SaveAiResponse(package.DispatchId, result.Text, result.Model, result.LatencyMs);
                if (row != null)
                {
                    snapshot = LatestDispatchFile.FromRecord(row, _registry, _source) with { Event = "ai_response", ElapsedMs = package.ElapsedMs };
                    fileError = LatestDispatchFile.Write(_latestPath, snapshot);
                }
                _store.WriteAuditLog("gemini_consult", ActionRiskLevel.Green_SafeRead,
                    JsonSerializer.Serialize(new { dispatchId = package.DispatchId, model = result.Model, ok = result.Ok, latencyMs = result.LatencyMs, keySource, error = result.ErrorKind }),
                    result.Ok ? "Answered" : result.ErrorKind ?? "Failed");
            }
            catch (Exception ex)
            {
                fileError = $"SQLite: {ex.Message}";
            }

            return new ConsultOutcome(package.DispatchId, result.Ok, result.Text, result.Model, result.LatencyMs, keySource, row, snapshot, fileError);
        }
    }
}
