using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace NEXUSHomeHub.Core
{
    /// <summary>ADR-44: one cached Gemini response row retrieved from SQLite WAL (<c>gemini_response_cache</c>).</summary>
    public sealed record GeminiCacheHit(
        string CacheKey,
        string CommandType,
        string ModelId,
        string NormalizedPrompt,
        string GroundingFingerprint,
        IReadOnlyList<string> GroundingSources,
        string ResponseText,
        int BasePromptTokens,
        int BaseCompletionTokens,
        long PromptTokensSaved,
        long CompletionTokensSaved,
        long HitCount,
        string CreatedUtc,
        string LastHitUtc,
        string ExpiresUtc,
        long CumulativeSavedTotalTokens)
    {
        public long SavedTotalTokens => PromptTokensSaved + CompletionTokensSaved;
    }

    /// <summary>ADR-44: summary statistics of <c>gemini_response_cache</c> in SQLite WAL.</summary>
    public sealed record GeminiCacheStats(
        int TotalEntries,
        int ActiveEntries,
        long TotalHits,
        long PromptTokensSaved,
        long CompletionTokensSaved,
        long SavedTotalTokens,
        int DefaultTtlSeconds)
    {
        [JsonPropertyName("total_entries")]
        public int TotalEntriesSnake => TotalEntries;

        [JsonPropertyName("active_entries")]
        public int ActiveEntriesSnake => ActiveEntries;

        [JsonPropertyName("hit_count")]
        public long HitCountSnake => TotalHits;

        [JsonPropertyName("prompt_tokens_saved")]
        public long PromptTokensSavedSnake => PromptTokensSaved;

        [JsonPropertyName("completion_tokens_saved")]
        public long CompletionTokensSavedSnake => CompletionTokensSaved;

        [JsonPropertyName("saved_total_tokens")]
        public long SavedTotalTokensSnake => SavedTotalTokens;

        [JsonPropertyName("ttl_seconds")]
        public int TtlSecondsSnake => DefaultTtlSeconds;
    }

    /// <summary>
    /// TASK-11 ADR-44: Deterministic Response Cache stored in SQLite WAL (<c>gemini_response_cache</c>).
    /// Zero Managed Heap bloat: all cached responses live in SQLite WAL and are queried by primary key index in &lt; 0.3 ms.
    /// </summary>
    public sealed class GeminiResponseCache
    {
        public const int DefaultTtlSecondsValue = 3600;

        private readonly SqliteMemoryStore _store;

        public GeminiResponseCache(SqliteMemoryStore store, int defaultTtlSeconds = DefaultTtlSecondsValue)
        {
            _store = store;
            DefaultTtlSeconds = defaultTtlSeconds > 0 ? defaultTtlSeconds : DefaultTtlSecondsValue;
        }

        public int DefaultTtlSeconds { get; set; }

        /// <summary>Collapses runs of whitespace into a single space and trims ends for deterministic key hashing.</summary>
        public static string NormalizePrompt(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;
            return string.Join(' ', prompt.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// ADR-44: SHA-256 hex (lowercase) of <c>model_id | normalized_prompt | grounding_fingerprint</c>.
        /// </summary>
        public static string ComputeKey(string modelId, string normalizedPrompt, string? groundingFingerprint = null)
        {
            string normModel = (modelId ?? string.Empty).Trim().ToLowerInvariant();
            string normPrompt = NormalizePrompt(normalizedPrompt);
            string normGrounding = (groundingFingerprint ?? string.Empty).Trim().ToLowerInvariant();
            string payload = $"{normModel}|{normPrompt}|{normGrounding}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Resolves the canonical model identifier used for cache key computation.
        /// In mock mode, includes the mock environment string so distinct test mocks never collide.
        /// In live mode, uses the configured model name so pre-fallback and post-fallback calls share the same key.
        /// </summary>
        public static string ResolveModelIdForCache(NexusConfig config, bool isQa = false)
        {
            if (isQa)
            {
                string? qaMock = Environment.GetEnvironmentVariable(GeminiAgentClient.QaMockEnvironmentVariable);
                if (!string.IsNullOrEmpty(qaMock))
                    return $"qa-mock:{qaMock.Trim()}";
                return $"qa:{config.GeminiModel.Trim().ToLowerInvariant()}";
            }

            string? mock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            if (!string.IsNullOrEmpty(mock))
                return $"mock:{mock.Trim()}";
            return config.GeminiModel.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Looks up an unexpired cache entry by <paramref name="cacheKey"/>; if found, atomically increments
        /// <c>hit_count</c>, <c>prompt_tokens_saved</c>, and <c>completion_tokens_saved</c> and returns the updated row.
        /// </summary>
        public bool TryHit(string cacheKey, out GeminiCacheHit? hit, bool rollbackWarmup = false)
        {
            hit = null;
            if (string.IsNullOrWhiteSpace(cacheKey)) return false;
            if (GeminiAgentClient.GetSimulatedTransientFailures() > 0) return false;

            DateTime now = DateTime.UtcNow;
            string nowIso = now.ToString("o");

            using var connection = _store.OpenHeldConnection();
            using var tx = connection.BeginTransaction();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = @"
                    SELECT command_type, model_id, normalized_prompt, grounding_fingerprint, grounding_sources_json,
                           response_text, base_prompt_tokens, base_completion_tokens,
                           prompt_tokens_saved, completion_tokens_saved, hit_count,
                           created_utc, last_hit_utc, expires_utc
                    FROM gemini_response_cache
                    WHERE cache_key = $key;";
                select.Parameters.AddWithValue("$key", cacheKey);

                using var reader = select.ExecuteReader();
                if (!reader.Read())
                {
                    tx.Commit();
                    return false;
                }

                string expiresUtc = reader.GetString(13);
                if (DateTime.TryParse(expiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime exp) &&
                    exp <= now)
                {
                    reader.Close();
                    tx.Commit();
                    return false;
                }

                string commandType = reader.GetString(0);
                string modelId = reader.GetString(1);
                string normalizedPrompt = reader.GetString(2);
                string groundingFingerprint = reader.GetString(3);
                string sourcesJson = reader.GetString(4);
                string responseText = reader.GetString(5);
                int basePrompt = reader.GetInt32(6);
                int baseCompletion = reader.GetInt32(7);
                long newPromptSaved = reader.GetInt64(8) + Math.Max(0, basePrompt);
                long newCompletionSaved = reader.GetInt64(9) + Math.Max(0, baseCompletion);
                long newHitCount = reader.GetInt64(10) + 1;
                string createdUtc = reader.GetString(11);
                reader.Close();

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = tx;
                    update.CommandText = @"
                        UPDATE gemini_response_cache
                        SET prompt_tokens_saved = $ps,
                            completion_tokens_saved = $cs,
                            hit_count = $hc,
                            last_hit_utc = $now
                        WHERE cache_key = $key;";
                    update.Parameters.AddWithValue("$ps", newPromptSaved);
                    update.Parameters.AddWithValue("$cs", newCompletionSaved);
                    update.Parameters.AddWithValue("$hc", newHitCount);
                    update.Parameters.AddWithValue("$now", nowIso);
                    update.Parameters.AddWithValue("$key", cacheKey);
                    update.ExecuteNonQuery();
                }

                long cumulativeSavedTotal;
                using (var sumCmd = connection.CreateCommand())
                {
                    sumCmd.Transaction = tx;
                    sumCmd.CommandText = "SELECT COALESCE(SUM(prompt_tokens_saved + completion_tokens_saved), 0) FROM gemini_response_cache;";
                    cumulativeSavedTotal = Convert.ToInt64(sumCmd.ExecuteScalar() ?? 0L);
                }

                if (rollbackWarmup) tx.Rollback(); else tx.Commit();

                IReadOnlyList<string> sources = ParseSourcesJson(sourcesJson);
                hit = new GeminiCacheHit(
                    CacheKey: cacheKey,
                    CommandType: commandType,
                    ModelId: modelId,
                    NormalizedPrompt: normalizedPrompt,
                    GroundingFingerprint: groundingFingerprint,
                    GroundingSources: sources,
                    ResponseText: responseText,
                    BasePromptTokens: basePrompt,
                    BaseCompletionTokens: baseCompletion,
                    PromptTokensSaved: newPromptSaved,
                    CompletionTokensSaved: newCompletionSaved,
                    HitCount: newHitCount,
                    CreatedUtc: createdUtc,
                    LastHitUtc: nowIso,
                    ExpiresUtc: expiresUtc,
                    CumulativeSavedTotalTokens: cumulativeSavedTotal);
                return true;
            }
        }

        /// <summary>
        /// Stores or refreshes a successful Gemini response in <c>gemini_response_cache</c>.
        /// When refreshing an existing key (e.g. via <c>/ask!</c>), cumulative <c>hit_count</c> and saved token counters are preserved.
        /// </summary>
        public void Put(
            string cacheKey,
            string commandType,
            string modelId,
            string normalizedPrompt,
            string? groundingFingerprint,
            IReadOnlyList<string>? groundingSources,
            string responseText,
            int promptTokens,
            int completionTokens,
            int? ttlSeconds = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(responseText)) return;
            if (GeminiAgentClient.GetSimulatedTransientFailures() > 0) return;

            DateTime now = DateTime.UtcNow;
            string nowIso = now.ToString("o");
            int ttl = ttlSeconds is > 0 ? ttlSeconds.Value : DefaultTtlSeconds;
            string expiresIso = now.AddSeconds(ttl).ToString("o");
            string sourcesJson = groundingSources is { Count: > 0 }
                ? JsonSerializer.Serialize(groundingSources)
                : "[]";

            using var connection = _store.OpenHeldConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO gemini_response_cache
                    (cache_key, command_type, model_id, normalized_prompt, grounding_fingerprint, grounding_sources_json,
                     response_text, base_prompt_tokens, base_completion_tokens,
                     prompt_tokens_saved, completion_tokens_saved, hit_count,
                     created_utc, last_hit_utc, expires_utc)
                VALUES
                    ($key, $cmd, $model, $prompt, $fp, $sources,
                     $resp, $bpt, $bct,
                     0, 0, 0,
                     $now, $now, $exp)
                ON CONFLICT(cache_key) DO UPDATE SET
                    command_type = excluded.command_type,
                    model_id = excluded.model_id,
                    normalized_prompt = excluded.normalized_prompt,
                    grounding_fingerprint = excluded.grounding_fingerprint,
                    grounding_sources_json = excluded.grounding_sources_json,
                    response_text = excluded.response_text,
                    base_prompt_tokens = excluded.base_prompt_tokens,
                    base_completion_tokens = excluded.base_completion_tokens,
                    created_utc = excluded.created_utc,
                    expires_utc = excluded.expires_utc;";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            cmd.Parameters.AddWithValue("$cmd", commandType ?? "ask");
            cmd.Parameters.AddWithValue("$model", modelId ?? string.Empty);
            cmd.Parameters.AddWithValue("$prompt", NormalizePrompt(normalizedPrompt));
            cmd.Parameters.AddWithValue("$fp", (groundingFingerprint ?? string.Empty).Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$sources", sourcesJson);
            cmd.Parameters.AddWithValue("$resp", responseText);
            cmd.Parameters.AddWithValue("$bpt", Math.Max(0, promptTokens));
            cmd.Parameters.AddWithValue("$bct", Math.Max(0, completionTokens));
            cmd.Parameters.AddWithValue("$now", nowIso);
            cmd.Parameters.AddWithValue("$exp", expiresIso);
            cmd.ExecuteNonQuery();
        }

        public GeminiCacheStats GetStats()
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            using var connection = _store.OpenHeldConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN expires_utc > $now THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(hit_count), 0),
                       COALESCE(SUM(prompt_tokens_saved), 0),
                       COALESCE(SUM(completion_tokens_saved), 0)
                FROM gemini_response_cache;";
            cmd.Parameters.AddWithValue("$now", nowIso);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return new GeminiCacheStats(0, 0, 0, 0, 0, 0, DefaultTtlSeconds);
            }

            int total = reader.GetInt32(0);
            int active = reader.GetInt32(1);
            long hits = reader.GetInt64(2);
            long promptSaved = reader.GetInt64(3);
            long completionSaved = reader.GetInt64(4);
            return new GeminiCacheStats(
                TotalEntries: total,
                ActiveEntries: active,
                TotalHits: hits,
                PromptTokensSaved: promptSaved,
                CompletionTokensSaved: completionSaved,
                SavedTotalTokens: promptSaved + completionSaved,
                DefaultTtlSeconds: DefaultTtlSeconds);
        }

        public int Clear()
        {
            using var connection = _store.OpenHeldConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM gemini_response_cache;";
            return cmd.ExecuteNonQuery();
        }

        public void DeleteKey(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return;
            using var connection = _store.OpenHeldConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM gemini_response_cache WHERE cache_key = $key;";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            cmd.ExecuteNonQuery();
        }

        private static IReadOnlyList<string> ParseSourcesJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return Array.Empty<string>();
            try
            {
                string[]? arr = JsonSerializer.Deserialize<string[]>(json);
                return arr ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>ADR-45: one matched section extracted from <c>03_Local_Data_Vault/</c>.</summary>
    public sealed record VaultGroundingSection(
        string File,
        string Section,
        string SourceRef,
        int Score,
        string Content)
    {
        public int CharCount => Content.Length;
    }

    /// <summary>ADR-45: result of a selective local vault grounding search (Top-3 sections, &lt;= 1500 chars total).</summary>
    public sealed record VaultGroundingResult(
        string Query,
        IReadOnlyList<VaultGroundingSection> Sections,
        IReadOnlyList<string> GroundingSources,
        int TotalChars,
        string GroundingFingerprint,
        string GroundedContextBlock,
        double ElapsedMs)
    {
        [JsonPropertyName("grounding_sources")]
        public IReadOnlyList<string> GroundingSourcesSnake => GroundingSources;

        [JsonPropertyName("grounding_fingerprint")]
        public string GroundingFingerprintSnake => GroundingFingerprint;

        [JsonPropertyName("total_chars")]
        public int TotalCharsSnake => TotalChars;

        [JsonPropertyName("elapsed_ms")]
        public double ElapsedMsSnake => ElapsedMs;
    }

    /// <summary>
    /// TASK-11 ADR-45: Selective Local Vault Grounding (<c>VaultGroundingEngine</c>).
    /// Streams <c>.md</c> and <c>.json</c> files in <c>03_Local_Data_Vault/</c>, splits Markdown by <c>#</c>/<c>##</c>/<c>###</c>
    /// headings, scores deterministic keyword matches, and returns at most Top-3 sections (&lt;= 1 500 characters total).
    /// Zero persistent heap index: files are streamed on demand via <see cref="StreamReader"/> in &lt; 2 ms.
    /// </summary>
    public sealed class VaultGroundingEngine
    {
        public const int MaxTopSections = 3;
        public const int MaxTotalAttachedChars = 1500;

        private readonly string _vaultRoot;
        private readonly object _memoLock = new();
        private string _lastKey = string.Empty;
        private long _lastTick;
        private long _lastNotesStamp;
        private VaultGroundingResult? _lastResult;
        private readonly string _quickNotesPath;

        public VaultGroundingEngine(string vaultRoot)
        {
            _vaultRoot = Path.GetFullPath(vaultRoot);
            _quickNotesPath = Path.Combine(_vaultRoot, LocalNoteService.FolderName, LocalNoteService.FileName);
        }

        public string VaultRoot => _vaultRoot;

        /// <summary>
        /// TASK-14 ADR-59: drop the 5 s memo of the last query. <see cref="LocalNoteService"/> calls this after every /note,
        /// so the next /ground, /g or search_local_vault in this process reads the new note.
        /// </summary>
        public void Invalidate()
        {
            lock (_memoLock)
            {
                _lastResult = null;
                _lastKey = string.Empty;
            }
        }

        /// <summary>
        /// Last-write stamp of quick_notes.md (0 = missing). Part of the memo check, so a note written by the other process
        /// (McpTools vs HostService) also ends the memo. One file-attribute read, no content read.
        /// </summary>
        private long QuickNotesStamp()
        {
            try
            {
                var info = new FileInfo(_quickNotesPath);
                return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        public VaultGroundingResult Search(string query, int maxSections = MaxTopSections, int maxTotalChars = MaxTotalAttachedChars)
        {
            var sw = Stopwatch.StartNew();
            string cleanQuery = GeminiResponseCache.NormalizePrompt(query);
            int capSections = Math.Clamp(maxSections, 1, MaxTopSections);
            int capChars = Math.Clamp(maxTotalChars, 120, MaxTotalAttachedChars);

            if (cleanQuery.Length == 0 || !Directory.Exists(_vaultRoot))
            {
                sw.Stop();
                return new VaultGroundingResult(cleanQuery, Array.Empty<VaultGroundingSection>(), Array.Empty<string>(),
                    0, string.Empty, string.Empty, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
            }

            string memoKey = $"{cleanQuery}|{capSections}|{capChars}";
            long nowTick = Stopwatch.GetTimestamp();
            long notesStamp = QuickNotesStamp();
            lock (_memoLock)
            {
                if (_lastResult != null &&
                    _lastKey.Equals(memoKey, StringComparison.Ordinal) &&
                    _lastNotesStamp == notesStamp &&
                    (nowTick - _lastTick) < Stopwatch.Frequency * 5L)
                {
                    sw.Stop();
                    return _lastResult with { ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3) };
                }
            }

            string[] terms = ExtractQueryTerms(cleanQuery);
            string queryLower = cleanQuery.ToLowerInvariant();

            var candidates = new List<(string RelFile, string Section, int Score, string RawBody)>();

            foreach (string filePath in EnumerateVaultFiles(_vaultRoot))
            {
                string relFile = Path.GetRelativePath(_vaultRoot, filePath).Replace('\\', '/');
                string ext = Path.GetExtension(filePath);
                try
                {
                    if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractMarkdownSections(filePath, relFile, terms, queryLower, candidates);
                    }
                    else if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractJsonSection(filePath, relFile, terms, queryLower, candidates);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip locked or inaccessible transient files without failing grounding.
                }
            }

            // TASK-11b Hạng mục D: deterministic tie-break (Score DESC, RelativePath ASC (ordinal), SectionIndex ASC).
            // Candidates are appended file by file in reading order, so the list position is the section index inside a file.
            var topRaw = candidates
                .Select((c, index) => (c.RelFile, c.Section, c.Score, c.RawBody, SectionIndex: index))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.RelFile, StringComparer.Ordinal)
                .ThenBy(c => c.SectionIndex)
                .Take(capSections)
                .ToList();

            // TASK-14 ADR-59: a quick note (/note) is a fact the user wrote down on purpose, but one short line rarely
            // outscores a long handoff section on raw keyword counts. If no quick note made the Top-N, the best quick note
            // that matches at least half of the query terms takes the last slot (still deterministic: same files, same result).
            if (topRaw.Count > 0 && !topRaw.Any(c => IsQuickNote(c.RelFile)))
            {
                int needTerms = (terms.Length + 1) / 2;
                var bestNote = candidates
                    .Select((c, index) => (c.RelFile, c.Section, c.Score, c.RawBody, SectionIndex: index))
                    .Where(c => c.Score > 0 && IsQuickNote(c.RelFile) && CountMatchedTerms(c.Section, c.RawBody, terms) >= needTerms)
                    .OrderByDescending(c => c.Score)
                    .ThenByDescending(c => c.SectionIndex) // equal score: the newest note (notes are appended)
                    .Take(1)
                    .ToList();
                if (bestNote.Count == 1)
                {
                    if (topRaw.Count >= capSections) topRaw.RemoveAt(topRaw.Count - 1);
                    topRaw.Add(bestNote[0]);
                }
            }

            var selected = new List<VaultGroundingSection>(topRaw.Count);
            var contextBuilder = new StringBuilder();
            int remainingChars = capChars;

            for (int i = 0; i < topRaw.Count && remainingChars > 40; i++)
            {
                var item = topRaw[i];
                string sourceRef = $"[{item.RelFile}#{item.Section}]";
                string prefix = (selected.Count > 0 ? "\n\n" : string.Empty) + $"{sourceRef}\n";
                if (prefix.Length >= remainingChars) break;

                int allowedContentChars = Math.Min(remainingChars - prefix.Length, 480);
                if (allowedContentChars <= 16) break;

                string snippet = BuildSnippet(item.RawBody, terms, allowedContentChars);
                if (snippet.Length == 0) continue;

                contextBuilder.Append(prefix).Append(snippet);
                remainingChars = capChars - contextBuilder.Length;
                selected.Add(new VaultGroundingSection(item.RelFile, item.Section, sourceRef, item.Score, snippet));
            }

            string contextBlock = contextBuilder.ToString();
            if (contextBlock.Length > capChars)
            {
                contextBlock = contextBlock.Substring(0, capChars);
            }

            string[] sources = selected.Select(s => s.SourceRef).ToArray();
            int totalChars = contextBlock.Length;
            string fingerprint = selected.Count == 0
                ? string.Empty
                : ComputeFingerprint(selected);

            sw.Stop();
            var result = new VaultGroundingResult(
                Query: cleanQuery,
                Sections: selected,
                GroundingSources: sources,
                TotalChars: totalChars,
                GroundingFingerprint: fingerprint,
                GroundedContextBlock: contextBlock,
                ElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 3));
            lock (_memoLock)
            {
                _lastKey = memoKey;
                _lastTick = Stopwatch.GetTimestamp();
                _lastNotesStamp = notesStamp;
                _lastResult = result;
            }
            return result;
        }

        private static bool IsQuickNote(string relFile) =>
            relFile.Equals(LocalNoteService.RelativePath, StringComparison.OrdinalIgnoreCase);

        private static int CountMatchedTerms(string heading, string body, string[] terms)
        {
            int matched = 0;
            foreach (string term in terms)
            {
                if (heading.Contains(term, StringComparison.OrdinalIgnoreCase) || body.Contains(term, StringComparison.OrdinalIgnoreCase)) matched++;
            }
            return matched;
        }

        /// <summary>Builds the grounded prompt sent to the Primary Agent when executing <c>/ground</c> (<c>/g</c>).</summary>
        public static string BuildGroundedPrompt(string userQuery, VaultGroundingResult grounding)
        {
            if (grounding.Sections.Count == 0 || string.IsNullOrWhiteSpace(grounding.GroundedContextBlock))
            {
                return userQuery.Trim();
            }

            return
                "## Ngữ cảnh trích xuất từ 03_Local_Data_Vault (ADR-45 Selective Grounding)\n" +
                grounding.GroundedContextBlock +
                "\n\n## Câu hỏi / Yêu cầu\n" +
                userQuery.Trim() +
                "\n\n(Hãy trả lời ngắn gọn dựa trên ngữ cảnh cục bộ ở trên và trích dẫn nguồn dạng [file#section].)";
        }

        private static IEnumerable<string> EnumerateVaultFiles(string vaultRoot)
        {
            var stack = new Stack<string>();
            stack.Push(vaultRoot);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string dirName = Path.GetFileName(dir);
                if (dirName.Equals("structured_db", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                for (int i = subDirs.Length - 1; i >= 0; i--)
                {
                    stack.Push(subDirs[i]);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    string ext = Path.GetExtension(name);
                    if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Skip transient verification/selftest dumps, live IPC state, SQLite files, temp files of atomic writes
                    // (ADR-14) and corrupt config backups (TASK-11b Hạng mục D: explicit default exclusion list).
                    if (name.StartsWith("task0", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("task1", StringComparison.OrdinalIgnoreCase) ||
                        (name.StartsWith("task", StringComparison.OrdinalIgnoreCase) &&
                         (name.Contains("_selftest_", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith("_verification.json", StringComparison.OrdinalIgnoreCase))) ||
                        name.StartsWith("latest_dispatch.json", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(".db", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(".invalid-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }

        private static void ExtractMarkdownSections(
            string filePath,
            string relFile,
            string[] terms,
            string queryLower,
            List<(string RelFile, string Section, int Score, string RawBody)> output)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string currentHeading = "Overview";
            var sb = new StringBuilder();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (TryParseHeading(line, out string heading))
                {
                    FlushSection(relFile, currentHeading, sb, terms, queryLower, output);
                    currentHeading = heading;
                    sb.Clear();
                }
                else
                {
                    if (sb.Length < 1200)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(line);
                    }
                }
            }
            FlushSection(relFile, currentHeading, sb, terms, queryLower, output);
        }

        private static void ExtractJsonSection(
            string filePath,
            string relFile,
            string[] terms,
            string queryLower,
            List<(string RelFile, string Section, int Score, string RawBody)> output)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            char[] buffer = new char[2048];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return;

            string body = new string(buffer, 0, read).Trim();
            int score = ScoreSection(relFile, "config", body, terms, queryLower);
            if (score > 0)
            {
                output.Add((relFile, "config", score, body));
            }
        }

        private static void FlushSection(
            string relFile,
            string heading,
            StringBuilder bodyBuilder,
            string[] terms,
            string queryLower,
            List<(string RelFile, string Section, int Score, string RawBody)> output)
        {
            string body = bodyBuilder.ToString().Trim();
            if (body.Length == 0 && heading == "Overview") return;

            string cleanHeading = NormalizeSectionHeading(heading);
            string fullText = body.Length > 0 ? $"{cleanHeading}\n{body}" : cleanHeading;
            int score = ScoreSection(relFile, cleanHeading, fullText, terms, queryLower);
            if (score > 0)
            {
                output.Add((relFile, cleanHeading, score, fullText));
            }
        }

        private static bool TryParseHeading(string line, out string heading)
        {
            heading = string.Empty;
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith('#')) return false;

            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is < 1 or > 3) return false;
            if (hashes < trimmed.Length && trimmed[hashes] != ' ') return false;

            heading = trimmed.Substring(hashes).Trim();
            return heading.Length > 0;
        }

        private static string NormalizeSectionHeading(string heading)
        {
            string clean = heading.Replace("`", string.Empty).Trim();
            if (clean.Length > 72) clean = clean.Substring(0, 72).TrimEnd();
            return clean.Length > 0 ? clean : "Section";
        }

        private static int ScoreSection(string relFile, string heading, string body, string[] terms, string queryLower)
        {
            if (terms.Length == 0) return 0;

            int score = 0;
            int matchedTerms = 0;

            foreach (string term in terms)
            {
                bool matchedThisTerm = false;
                if (heading.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 6;
                    matchedThisTerm = true;
                }
                if (relFile.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                    matchedThisTerm = true;
                }

                int idx = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    matchedThisTerm = true;
                    score += 2;
                    int extra = 0;
                    int next = idx + term.Length;
                    while (extra < 3 && next < body.Length &&
                           (idx = body.IndexOf(term, next, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        extra++;
                        score += 1;
                        next = idx + term.Length;
                    }
                }

                if (matchedThisTerm) matchedTerms++;
            }

            if (matchedTerms == 0) return 0;
            if (matchedTerms > 1) score += matchedTerms * 3;
            if (queryLower.Length >= 4 && heading.Contains(queryLower, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (queryLower.Length >= 4 && body.Contains(queryLower, StringComparison.OrdinalIgnoreCase)) score += 5;

            return score;
        }

        private static string BuildSnippet(string rawBody, string[] terms, int maxChars)
        {
            string flat = string.Join('\n',
                rawBody.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0));
            if (flat.Length <= maxChars) return flat;

            int bestIndex = -1;
            foreach (string term in terms)
            {
                int idx = flat.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestIndex < 0 || idx < bestIndex))
                {
                    bestIndex = idx;
                }
            }

            if (bestIndex <= maxChars / 2)
            {
                return flat.Substring(0, maxChars - 1).TrimEnd() + "…";
            }

            int start = Math.Max(0, bestIndex - (maxChars / 3));
            if (start + maxChars >= flat.Length)
            {
                start = Math.Max(0, flat.Length - maxChars + 1);
                return "…" + flat.Substring(start).TrimStart();
            }

            return "…" + flat.Substring(start, maxChars - 2).Trim() + "…";
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "là", "gì", "và", "của", "cho", "trong", "với", "các", "như", "thế", "nào", "khi", "để", "có", "không",
            "tại", "sao", "vì", "về", "từ", "theo", "trên", "dưới", "này", "đó", "một", "những", "được", "cần",
            "the", "and", "for", "with", "what", "how", "why", "from", "into", "that", "this", "are", "was"
        };

        private static string[] ExtractQueryTerms(string query)
        {
            var separators = new[]
            {
                ' ', '\t', '\r', '\n', ',', '.', ';', ':', '?', '!', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\', '|', '+', '=', '*'
            };
            return query
                .ToLowerInvariant()
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2 && !StopWords.Contains(t))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string ComputeFingerprint(IReadOnlyList<VaultGroundingSection> sections)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sections.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(sections[i].SourceRef).Append(':').Append(sections[i].Content.Length);
            }
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
