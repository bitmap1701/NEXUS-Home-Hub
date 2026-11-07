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
using System.Text.RegularExpressions;

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
        public const string QaMockEnvironmentVariable = "NEXUS_GEMINI_QA_MOCK_VERDICT";
        public const string QaMockModel = "gemini-qa-mock-verifier";

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

        public async Task<GeminiResult> GenerateAsync(string systemInstruction, string userText, NexusConfig config,
            string? apiKey, string primaryAgentName, string squadId, CancellationToken cancellationToken = default,
            string? directive = null)
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
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstruction + "\n\n" + (directive ?? HudDirective) } }
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

                using HttpResponseMessage response = await LazyHttp.Value.SendAsync(message, timeout.Token).ConfigureAwait(false);
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

    /// <summary>TASK-08 ADR-28: synchronous path of /ask (or /qa) as seen by the Cockpit, in milliseconds.</summary>
    public sealed record AskSyncBreakdown(double GuardMs, double DispatchAndDbMs, double TaskSpawnMs, double TotalSyncMs);

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
        string? Error);

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
        private readonly DispatchVerdictService _verdicts;

        public AgentConsultService(NexusRuntime runtime, DpapiSecretVault? vault = null, GeminiAgentClient? client = null)
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
        }

        public DpapiSecretVault Vault { get; }

        /// <param name="guardChecked">True when the caller already ran the key-leak guard (Cockpit /ask times it as GuardMs).</param>
        public ConsultStart Start(string task, string? nexusMode, string? preferredAgentId, bool guardChecked = false)
        {
            if (!guardChecked && DpapiSecretVault.GoogleApiKeyPattern.IsMatch(task))
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

        // ───────────── TASK-08: Gemini QA agent review (ADR-30/31) ─────────────

        public const string QaSkippedNoKey = "[LOCAL QA SKIPPED] Chưa cấu hình Gemini API Key.";
        public const string QaSkippedNoAnswer = "[LOCAL QA SKIPPED] Primary Agent chưa có câu trả lời hợp lệ để kiểm định.";

        private static readonly System.Text.RegularExpressions.Regex VerdictLine = new(
            @"VERDICT\s*[:：]\s*(PASSED|PASS|NEEDS[_ ]?WORK|FAILED|FAIL)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Synchronous part of /qa and MCP run_gemini_qa_review: resolve the dispatch (null = newest), refuse key-like
        /// evidence, write latest_dispatch.json event "qa" so every Cockpit shows "QA REVIEWING". No DPAPI, no HTTP.
        /// </summary>
        public QaStart StartQa(long? dispatchId, string? evidence)
        {
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(evidence ?? string.Empty))
                return new QaStart(0, null, null, "Bằng chứng có chuỗi giống API key: không gửi, không lưu.");
            long id = dispatchId is > 0 ? dispatchId.Value : _store.GetLatestDispatchId() ?? 0;
            if (id <= 0) return new QaStart(0, null, null, "Chưa có lượt điều phối nào trong agent_dispatch_logs.");
            RecentDispatch? row = _store.GetDispatch(id);
            if (row == null) return new QaStart(id, null, null, $"Dispatch #{id} not found.");
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(row, _registry, _source) with { Event = "qa" };
            LatestDispatchFile.Write(_latestPath, snapshot);
            return new QaStart(id, row, snapshot, null);
        }

        /// <summary>
        /// Background part: QA agent persona (02_Agents_Registry) + task + Primary answer + evidence -> Gemini (or the
        /// NEXUS_GEMINI_QA_MOCK_VERDICT mock) -> "VERDICT: PASSED|NEEDS_WORK" -> qa_response / qa_latency_ms saved ->
        /// DispatchVerdictService (ADR-19 state machine, 3-strike ESCALATED, one escalation line).
        /// No key, an API failure or a cancelled call change nothing but qa_response: a network problem is never a strike.
        /// Never throws.
        /// </summary>
        public async Task<QaOutcome> RunQaReviewAsync(long dispatchId, string? evidence, CancellationToken cancellationToken = default)
        {
            RecentDispatch? row = null;
            try
            {
                row = _store.GetDispatch(dispatchId);
                if (row == null) return QaFailure(dispatchId, $"Dispatch #{dispatchId} not found.");
                AgentName qa = DescribeAgent(row.QaAgent);
                string ev = (evidence ?? string.Empty).Trim();

                GeminiResult result;
                string keySource;
                string? mock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
                if (!string.IsNullOrEmpty(mock))
                {
                    keySource = "Mock";
                    result = MockQa(mock);
                }
                else
                {
                    ApiKeyResolution key = Vault.Resolve(); // ADR-28: DPAPI only here, on the background task
                    keySource = key.SourceLabel;
                    if (key.Source == ApiKeySource.Missing) return SaveQaOnly(row, qa, QaSkippedNoKey, 0, keySource);
                    result = await _client.GenerateAsync(BuildQaInstruction(row, qa), BuildQaContent(row, ev), _config.Current, key.Key,
                        qa.Name, row.SquadId, cancellationToken, GeminiAgentClient.QaDirective).ConfigureAwait(false);
                    if (!result.Ok) return SaveQaOnly(row, qa, $"[QA NOT RUN] {result.Text}", result.LatencyMs, keySource);
                }

                Match m = VerdictLine.Match(result.Text);
                string verdict = m.Success && m.Groups[1].Value.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "PASSED" : "NEEDS_WORK";
                string summary = Summarize(result.Text, m, qa.Name, verdict);

                _store.SaveQaResponse(row.Id, result.Text, result.LatencyMs);
                VerdictOutcome outcome = _verdicts.Apply(row.Id, verdict, summary);
                RecentDispatch final = outcome.Dispatch ?? _store.GetDispatch(row.Id) ?? row;
                _store.WriteAuditLog("gemini_qa_review", ActionRiskLevel.Yellow_ReversibleTune,
                    JsonSerializer.Serialize(new { dispatchId = row.Id, qaAgent = qa.Id, verdict, status = final.Status, retryCount = final.RetryCount, latencyMs = result.LatencyMs, keySource }),
                    $"QA {verdict}");
                return new QaOutcome(row.Id, true, verdict, final.Status, final.RetryCount, qa.Name, result.Text, result.LatencyMs,
                    keySource, outcome.EscalatedNow, LatestDispatchFile.FromRecord(final, _registry, _source) with { Event = "qa_verdict" },
                    outcome.Error ?? outcome.FileError);
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

        private QaOutcome SaveQaOnly(RecentDispatch row, AgentName qa, string message, int latencyMs, string keySource)
        {
            RecentDispatch saved = _store.SaveQaResponse(row.Id, message, latencyMs) ?? row;
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromRecord(saved, _registry, _source) with { Event = "qa_skipped" };
            string? fileError = LatestDispatchFile.Write(_latestPath, snapshot);
            return new QaOutcome(row.Id, false, null, saved.Status, saved.RetryCount, qa.Name, message, latencyMs, keySource, false, snapshot, fileError);
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

        private string BuildQaContent(RecentDispatch row, string evidence)
        {
            AgentName primary = DescribeAgent(row.PrimaryAgent);
            return
                $"Task Summary:\n{row.TaskSummary}\n\n" +
                $"Primary Agent: {primary.Name} ({primary.Id})\n" +
                $"AI Response:\n{(row.AiResponse.Length > 0 ? row.AiResponse : "(chưa có câu trả lời AI — chỉ kiểm định theo đề bài và bằng chứng)")}\n\n" +
                $"Additional Evidence:\n{(evidence.Length > 0 ? evidence : "(không có)")}";
        }

        /// <summary>"PASSED: Kịch bản UDP đạt chuẩn" -> "VERDICT: PASSED\nKịch bản UDP đạt chuẩn" (ADR-31, no network).</summary>
        private static GeminiResult MockQa(string value)
        {
            string trimmed = value.Trim();
            int colon = trimmed.IndexOf(':');
            string head = colon > 0 ? trimmed.Substring(0, colon).Trim() : trimmed;
            string body = colon > 0 ? trimmed.Substring(colon + 1).Trim() : string.Empty;
            string text = $"VERDICT: {head.ToUpperInvariant()}\n{(body.Length > 0 ? body : "[MOCK QA]")}";
            return new GeminiResult(true, text, GeminiAgentClient.QaMockModel, 0, null, null);
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
