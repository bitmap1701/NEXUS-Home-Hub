using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    public sealed record AgentName(string Id, string Name);

    /// <summary>
    /// Contents of 03_Local_Data_Vault/handoff/latest_dispatch.json (ADR-14/15): the live dispatch / QA state shared by
    /// CockpitUI and McpTools. Event = "dispatch" | "verdict"; Source = "CockpitUI" | "McpTools".
    /// </summary>
    public sealed record LatestDispatchSnapshot(
        string Event,
        string Source,
        long DispatchId,
        string Status,
        int RetryCount,
        string EvidenceNotes,
        string TimestampUtc,
        string UpdatedAtUtc,
        string TaskSummary,
        string NexusMode,
        string SquadId,
        string? SquadName,
        AgentName PrimaryAgent,
        AgentName QaAgent,
        string RealityChecker,
        IReadOnlyList<string>? MatchedKeywords,
        double? ElapsedMs,
        string? SystemInstructionPreview,
        string AiResponse = "",
        string AiModel = "",
        int AiLatencyMs = 0,
        string QaResponse = "",
        int QaLatencyMs = 0,
        int TurnCount = 1,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        long SessionTotalTokens = 0,
        long DailyTotalTokens = 0,
        bool CacheHit = false,
        long PromptTokensSaved = 0,
        long CompletionTokensSaved = 0,
        long SavedTotalTokens = 0,
        IReadOnlyList<string>? GroundingSources = null,
        [property: JsonPropertyName("mock_tokens_used")] long MockTokensUsed = 0,
        [property: JsonPropertyName("cache_lookup_ms")] double? CacheLookupMs = null,
        [property: JsonPropertyName("total_dispatch_ms")] double? TotalDispatchMs = null)
    {
        /// <summary>TASK-11b Hạng mục D: the model that actually produced <see cref="AiResponse"/> (same as aiModel).</summary>
        [JsonPropertyName("model_used")]
        public string ModelUsed => AiModel;

        [JsonPropertyName("cache_hit")]
        public bool CacheHitSnake => CacheHit;

        [JsonPropertyName("prompt_tokens_saved")]
        public long PromptTokensSavedSnake => PromptTokensSaved;

        [JsonPropertyName("completion_tokens_saved")]
        public long CompletionTokensSavedSnake => CompletionTokensSaved;

        [JsonPropertyName("saved_total_tokens")]
        public long SavedTotalTokensSnake => SavedTotalTokens;

        [JsonPropertyName("grounding_sources")]
        public IReadOnlyList<string> GroundingSourcesSnake => GroundingSources ?? Array.Empty<string>();
    }

    /// <summary>Atomic, UTF-8 (no BOM) read/write of latest_dispatch.json, safe across processes.</summary>
    public static class LatestDispatchFile
    {
        public const string FileName = "latest_dispatch.json";
        private const int InstructionPreviewChars = 600;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        public static LatestDispatchSnapshot FromDispatch(DispatchPackage package, string task, string source)
        {
            string now = DateTime.UtcNow.ToString("o");
            string preview = package.SystemInstruction.Length > InstructionPreviewChars
                ? package.SystemInstruction.Substring(0, InstructionPreviewChars) + "…"
                : package.SystemInstruction;
            return new LatestDispatchSnapshot("dispatch", source, package.DispatchId, "DISPATCHED", 0, string.Empty,
                now, now, Shorten(task, 200), package.NexusMode, package.SquadId, package.SquadName,
                new AgentName(package.PrimaryAgent.Id, package.PrimaryAgent.Name),
                new AgentName(package.QaAgent.Id, package.QaAgent.Name),
                SquadDispatcher.RealityCheckerId, package.MatchedKeywords.ToArray(), package.ElapsedMs, preview,
                SessionTotalTokens: GeminiQuotaGuard.GlobalSessionTotalTokens,
                DailyTotalTokens: GeminiQuotaGuard.GlobalCachedDailyTokens,
                GroundingSources: Array.Empty<string>(),
                MockTokensUsed: GeminiQuotaGuard.GlobalMockTokensUsed);
        }

        public static LatestDispatchSnapshot FromRecord(RecentDispatch row, AgentRegistry registry, string source,
            long? sessionTotalTokens = null, long? dailyTotalTokens = null)
        {
            string? squadName = SquadDispatcher.Squads.FirstOrDefault(s => s.SquadId == row.SquadId)?.Name;
            return new LatestDispatchSnapshot("verdict", source, row.Id, row.Status, row.RetryCount, row.EvidenceNotes,
                row.TimestampUtc, row.UpdatedAtUtc, row.TaskSummary, row.NexusMode, row.SquadId, squadName,
                Describe(registry, row.PrimaryAgent), Describe(registry, row.QaAgent),
                SquadDispatcher.RealityCheckerId, null, null, null,
                row.AiResponse, row.AiModel, row.AiLatencyMs, // TASK-07 §3.2
                row.QaResponse, row.QaLatencyMs,              // TASK-08 §3.3.2
                row.TurnCount,                                // TASK-09 §3.1.3
                row.PromptTokens, row.CompletionTokens, row.TotalTokens, // TASK-10 §3.1.3
                sessionTotalTokens ?? GeminiQuotaGuard.GlobalSessionTotalTokens,
                dailyTotalTokens ?? GeminiQuotaGuard.GlobalCachedDailyTokens,
                GroundingSources: Array.Empty<string>(),
                MockTokensUsed: GeminiQuotaGuard.GlobalMockTokensUsed);
        }

        /// <summary>Serializes writers of this process: they share one per-process temp file name (ADR-14).</summary>
        private static readonly object WriteGate = new();

        /// <summary>
        /// ADR-14 (restored in TASK-11b Hạng mục B): UTF-8 (no BOM) bytes into <c>latest_dispatch.json.&lt;pid&gt;.tmp</c> in the
        /// same folder, then <c>File.Move(tmp, target, overwrite: true)</c>, under a process-wide lock. A reader (Cockpit,
        /// McpTools) sees either the previous file or the new one, never a partly written file. TASK-11's in-place
        /// <c>WriteFast</c> is gone: every writer, cache-hit paths included, uses this method.
        /// </summary>
        public static string? Write(string path, LatestDispatchSnapshot snapshot) => Write(path, snapshot, out _, out _, out _);

        /// <summary>Same ADR-14 write, with the TASK-11b profile split: JSON bytes, temp file, File.Move (ms).</summary>
        public static string? Write(string path, LatestDispatchSnapshot snapshot, out double serializeMs, out double tempWriteMs, out double moveMs)
        {
            long ts0 = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            serializeMs = Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - ts0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3);
            lock (WriteGate)
            {
                return AtomicFile.WriteAllBytes(path, bytes, out tempWriteMs, out moveMs);
            }
        }

        public static string Serialize(LatestDispatchSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

        /// <summary>Reads with FileShare.ReadWrite | Delete so writers can keep replacing the file (ADR-14).</summary>
        public static LatestDispatchSnapshot? TryRead(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                LatestDispatchSnapshot? s = JsonSerializer.Deserialize<LatestDispatchSnapshot>(stream, Json);
                if (s == null || s.DispatchId <= 0) return null;

                // Files written by TASK-04 (or by hand) can miss fields: never hand nulls to the UI.
                return s with
                {
                    Event = s.Event ?? "dispatch",
                    Source = s.Source ?? "unknown",
                    Status = s.Status ?? "DISPATCHED",
                    EvidenceNotes = s.EvidenceNotes ?? string.Empty,
                    TimestampUtc = s.TimestampUtc ?? string.Empty,
                    UpdatedAtUtc = s.UpdatedAtUtc ?? s.TimestampUtc ?? string.Empty,
                    TaskSummary = s.TaskSummary ?? string.Empty,
                    NexusMode = s.NexusMode ?? string.Empty,
                    SquadId = s.SquadId ?? string.Empty,
                    PrimaryAgent = s.PrimaryAgent ?? new AgentName("-", "-"),
                    QaAgent = s.QaAgent ?? new AgentName("-", "-"),
                    RealityChecker = s.RealityChecker ?? SquadDispatcher.RealityCheckerId,
                    AiResponse = s.AiResponse ?? string.Empty,
                    AiModel = s.AiModel ?? string.Empty,
                    QaResponse = s.QaResponse ?? string.Empty,
                    TurnCount = s.TurnCount < 1 ? 1 : s.TurnCount,
                    GroundingSources = s.GroundingSources ?? Array.Empty<string>()
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static AgentName Describe(AgentRegistry registry, string agentId) =>
            registry.TryGet(agentId, out AgentProfile profile)
                ? new AgentName(profile.Id, profile.Name)
                : new AgentName(agentId, agentId);

        public static string Shorten(string text, int max) =>
            text.Length > max ? text.Substring(0, max) + "…" : text;
    }

    public sealed record VerdictOutcome(RecentDispatch? Dispatch, bool EscalatedNow, string? Error, string? FileError)
    {
        public bool Ok => Dispatch != null && Error == null;
    }

    /// <summary>
    /// ADR-12/13 shared Dev-QA verdict logic for CockpitUI (/pass, /fail, /verdict) and McpTools
    /// (update_dispatch_verdict): atomic SQLite update with the 3-strike circuit breaker, escalation entry in
    /// qa_report.md, and latest_dispatch.json refresh so the other side picks it up (ADR-14).
    /// </summary>
    public sealed class DispatchVerdictService
    {
        public const int StrikeLimit = 3;

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly SqliteMemoryStore _store;
        private readonly AgentRegistry _registry;
        private readonly string _source;

        public DispatchVerdictService(SqliteMemoryStore store, AgentRegistry registry, string handoffDirectory, string source)
        {
            _store = store;
            _registry = registry;
            _source = source;
            LatestDispatchPath = Path.Combine(handoffDirectory, LatestDispatchFile.FileName);
            QaReportPath = Path.Combine(handoffDirectory, "qa_report.md");
        }

        public string LatestDispatchPath { get; }

        public string QaReportPath { get; }

        /// <summary>PASS / PASSED -> PASSED; FAIL / NEEDS_WORK / NEEDSWORK -> NEEDS_WORK; anything else -> null.</summary>
        public static string? NormalizeVerdict(string? verdict)
        {
            string v = (verdict ?? string.Empty).Trim().ToUpperInvariant().Replace("-", "_");
            return v switch
            {
                "PASS" or "PASSED" => "PASSED",
                "FAIL" or "NEEDS_WORK" or "NEEDSWORK" => "NEEDS_WORK",
                _ => null
            };
        }

        /// <param name="dispatchId">Null or &lt;= 0 = newest dispatch in agent_dispatch_logs.</param>
        public VerdictOutcome Apply(long? dispatchId, string verdict, string? evidenceNotes)
        {
            string? normalized = NormalizeVerdict(verdict);
            if (normalized == null) return new VerdictOutcome(null, false, "verdict must be PASSED or NEEDS_WORK.", null);

            long id = dispatchId is > 0 ? dispatchId.Value : _store.GetLatestDispatchId() ?? 0;
            if (id <= 0) return new VerdictOutcome(null, false, "Chưa có lượt điều phối nào trong agent_dispatch_logs.", null);

            string notes = (evidenceNotes ?? string.Empty).Trim();
            (RecentDispatch? row, string? previousStatus) = _store.ApplyVerdict(id, normalized, notes, StrikeLimit);
            if (row == null) return new VerdictOutcome(null, false, $"Dispatch #{id} not found.", null);

            // ADR-19: report only on the transition from another status to ESCALATED (strike 3, or reopening a PASSED
            // dispatch that already has >= 3 strikes), and never a second [3-STRIKE ESCALATION] line for the same id.
            bool escalatedNow = normalized == "NEEDS_WORK" && row.Status == "ESCALATED" && previousStatus != "ESCALATED" &&
                                !ReportHasEscalation(row.Id);
            if (escalatedNow) AppendEscalation(row);

            _store.WriteAuditLog("dispatch_verdict", ActionRiskLevel.Yellow_ReversibleTune,
                JsonSerializer.Serialize(new { dispatchId = row.Id, verdict = normalized, status = row.Status, retryCount = row.RetryCount, source = _source }),
                $"Dispatch#{row.Id}={row.Status}");

            long dailyTotal = _store.GetDailyTokenUsage().TotalTokens;
            GeminiQuotaGuard.UpdateGlobalDailyTokens(dailyTotal);
            string? fileError = LatestDispatchFile.Write(LatestDispatchPath,
                LatestDispatchFile.FromRecord(row, _registry, _source, GeminiQuotaGuard.GlobalSessionTotalTokens, dailyTotal));
            return new VerdictOutcome(row, escalatedNow, null, fileError);
        }

        /// <summary>
        /// TASK-11: a /qa cache hit already applied the verdict in SQLite and moved the dispatch to ESCALATED. Do the rest
        /// of what <see cref="Apply"/> does on that transition (one [3-STRIKE ESCALATION] line, dispatch_verdict audit)
        /// on a background task so the cache-hit sync path stays under 2 ms.
        /// </summary>
        public Task RecordCachedEscalationInBackground(RecentDispatch row, string verdict) => Task.Run(() =>
        {
            try
            {
                if (!ReportHasEscalation(row.Id)) AppendEscalation(row);
                _store.WriteAuditLog("dispatch_verdict", ActionRiskLevel.Yellow_ReversibleTune,
                    JsonSerializer.Serialize(new { dispatchId = row.Id, verdict, status = row.Status, retryCount = row.RetryCount, source = _source, cacheHit = true }),
                    $"Dispatch#{row.Id}={row.Status}");
            }
            catch (Exception ex)
            {
                try
                {
                    _store.WriteAuditLog("dispatch_escalation_report", ActionRiskLevel.Yellow_ReversibleTune,
                        JsonSerializer.Serialize(new { dispatchId = row.Id, cacheHit = true }), $"Failed: {ex.Message}");
                }
                catch (Exception) { }
            }
        });

        public static string EscalationMarker(long dispatchId) => $"### [3-STRIKE ESCALATION] Dispatch #{dispatchId} |";

        private bool ReportHasEscalation(long dispatchId)
        {
            try
            {
                if (!File.Exists(QaReportPath)) return false;
                string marker = EscalationMarker(dispatchId);
                using var stream = new FileStream(QaReportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith(marker, StringComparison.Ordinal)) return true;
                }
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>ADR-13 report line (one line, appended UTF-8 without BOM). Timestamp = time of escalation.</summary>
        private void AppendEscalation(RecentDispatch row)
        {
            string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
            string line =
                $"{EscalationMarker(row.Id)} {row.UpdatedAtUtc} | Squad: {row.SquadId} | " +
                $"Task: {OneLine(row.TaskSummary)} | Evidence: {OneLine(row.EvidenceNotes)}";
            try
            {
                File.AppendAllText(QaReportPath, Environment.NewLine + line + Environment.NewLine, Utf8NoBom);
            }
            catch (Exception ex)
            {
                _store.WriteAuditLog("dispatch_escalation_report", ActionRiskLevel.Yellow_ReversibleTune,
                    JsonSerializer.Serialize(new { dispatchId = row.Id }), $"AppendFailed: {ex.Message}");
            }
        }

        /// <summary>Card / feed label (TASK-05 §3.C.2).</summary>
        public static string StatusLabel(string status, int retryCount) => status switch
        {
            "PASSED" => $"PASSED (Retries: {retryCount})",
            "NEEDS_WORK" => $"NEEDS_WORK (Strike {retryCount}/{StrikeLimit})",
            "ESCALATED" => $"ESCALATED - {StrikeLimit} STRIKES!",
            _ => status
        };
    }
}
