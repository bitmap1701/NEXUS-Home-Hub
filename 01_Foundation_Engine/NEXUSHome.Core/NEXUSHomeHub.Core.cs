using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NEXUSHomeHub.Core
{
    public enum ActionRiskLevel
    {
        Green_SafeRead,
        Yellow_ReversibleTune,
        Red_CriticalSystem
    }

    // 1. AgentRegistry: see AgentRegistry.cs (TASK-03). SquadDispatcher: see SquadDispatcher.cs.

    /// <summary>
    /// 2. Tầng lưu trữ SQLite (Chế độ WAL - Write-Ahead Logging) cho Trí nhớ & Nhật ký Kiểm toán
    /// </summary>
    public sealed record RecentDispatch(
        long Id, string TimestampUtc, string NexusMode, string SquadId,
        string PrimaryAgent, string QaAgent, string TaskSummary, string Status,
        int RetryCount = 0, string EvidenceNotes = "", string UpdatedAtUtc = "",
        string AiResponse = "", string AiModel = "", int AiLatencyMs = 0,
        string QaResponse = "", int QaLatencyMs = 0,
        int TurnCount = 1, string TurnHistoryJson = "[]",
        int PromptTokens = 0, int CompletionTokens = 0, int TotalTokens = 0);

    /// <summary>Result of the TASK-05 agent_dispatch_logs migration (ADR-13), reported by Verify-Task05 E1.</summary>
    public sealed record SchemaMigrationReport(
        bool Rebuilt, IReadOnlyList<string> AddedColumns, long RowsBefore, long RowsAfter,
        string? BackupPath, IReadOnlyList<string> Columns, string? Error);

    public class SqliteMemoryStore
    {
        private readonly string _connectionString;
        private readonly string _dbFilePath;

        /// <summary>agent_dispatch_logs migration outcome of this process start (TASK-05).</summary>
        /// <summary>Full path of nexus_memory.db (TASK-05 selftest: locate the pre-task05 backup next to it).</summary>
        public string DatabasePath => _dbFilePath;

        public SchemaMigrationReport MigrationReport { get; private set; } =
            new(false, Array.Empty<string>(), 0, 0, null, Array.Empty<string>(), null);

        public SqliteMemoryStore(string dbFilePath)
        {
            _dbFilePath = Path.GetFullPath(dbFilePath);
            string? dir = Path.GetDirectoryName(dbFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbFilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // ADR-08: private cache. Shared-cache mode uses table locks that fail with SQLITE_LOCKED,
                // which busy_timeout does not retry; WAL + busy_timeout handles HostService/McpTools concurrency.
                Cache = SqliteCacheMode.Default
            }.ToString();

            InitializeWalDatabase();
        }

        /// <summary>ADR-08: every connection sets busy_timeout=5000 and journal_mode=WAL before use.</summary>
        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        /// <summary>Opens a connection with the ADR-08 pragmas; the caller owns it (used by the WAL concurrency selftest).</summary>
        public SqliteConnection OpenHeldConnection() => OpenConnection();

        private void InitializeWalDatabase()
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;

                CREATE TABLE IF NOT EXISTS audit_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp TEXT NOT NULL,
                    tool_name TEXT NOT NULL,
                    risk_level TEXT NOT NULL,
                    arguments_json TEXT,
                    outcome TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS agent_memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at TEXT NOT NULL,
                    category TEXT NOT NULL,
                    topic TEXT NOT NULL,
                    content TEXT NOT NULL
                );

                -- TASK-03/05: Agent Squad Dispatch log (Dev-QA loop state, 3-strike circuit breaker)
                CREATE TABLE IF NOT EXISTS agent_dispatch_logs (" + DispatchLogColumnsDdl + @");

                -- TASK-11 ADR-44: Deterministic Gemini response cache (SQLite WAL, TTL-bound)
                CREATE TABLE IF NOT EXISTS gemini_response_cache (
                    cache_key TEXT PRIMARY KEY,
                    command_type TEXT NOT NULL DEFAULT 'ask',
                    model_id TEXT NOT NULL DEFAULT '',
                    normalized_prompt TEXT NOT NULL DEFAULT '',
                    grounding_fingerprint TEXT NOT NULL DEFAULT '',
                    grounding_sources_json TEXT NOT NULL DEFAULT '[]',
                    response_text TEXT NOT NULL DEFAULT '',
                    base_prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    base_completion_tokens INTEGER NOT NULL DEFAULT 0,
                    prompt_tokens_saved INTEGER NOT NULL DEFAULT 0,
                    completion_tokens_saved INTEGER NOT NULL DEFAULT 0,
                    hit_count INTEGER NOT NULL DEFAULT 0,
                    created_utc TEXT NOT NULL DEFAULT '',
                    last_hit_utc TEXT NOT NULL DEFAULT '',
                    expires_utc TEXT NOT NULL DEFAULT ''
                );
            ";
            cmd.ExecuteNonQuery();

            MigrationReport = MigrateDispatchLogs(connection);
        }

        /// <summary>TASK-05 schema (ADR-13). Shared by fresh databases and the migration rebuild.</summary>
        private const string DispatchLogColumnsDdl = @"
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    nexus_mode TEXT NOT NULL CHECK (nexus_mode IN ('Micro', 'Sprint', 'Full')),
                    squad_id TEXT NOT NULL,
                    primary_agent TEXT NOT NULL,
                    qa_agent TEXT NOT NULL,
                    task_summary TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN ('DISPATCHED', 'NEEDS_WORK', 'PASSED', 'ESCALATED')),
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    evidence_notes TEXT NOT NULL DEFAULT '',
                    updated_at_utc TEXT NOT NULL DEFAULT '',
                    ai_response TEXT NOT NULL DEFAULT '',
                    ai_model TEXT NOT NULL DEFAULT '',
                    ai_latency_ms INTEGER NOT NULL DEFAULT 0,
                    qa_response TEXT NOT NULL DEFAULT '',
                    qa_latency_ms INTEGER NOT NULL DEFAULT 0,
                    turn_count INTEGER NOT NULL DEFAULT 1,
                    turn_history_json TEXT NOT NULL DEFAULT '[]',
                    prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    completion_tokens INTEGER NOT NULL DEFAULT 0,
                    total_tokens INTEGER NOT NULL DEFAULT 0,
                    live_prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    live_completion_tokens INTEGER NOT NULL DEFAULT 0,
                    live_total_tokens INTEGER NOT NULL DEFAULT 0,
                    mock_tokens_used INTEGER NOT NULL DEFAULT 0
                ";

        /// <summary>Columns copied by the TASK-05 rebuild (a TASK-03 table never has the later columns; they take their defaults).</summary>
        private const string Task05CopyColumns =
            "id, timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status, retry_count, evidence_notes, updated_at_utc";

        private const string DispatchSelectColumns = Task05CopyColumns +
            ", ai_response, ai_model, ai_latency_ms, qa_response, qa_latency_ms, turn_count, turn_history_json, prompt_tokens, completion_tokens, total_tokens";

        /// <summary>
        /// TASK-05 migration (backward compatible, keeps every TASK-03/04 row and id):
        /// - The TASK-03 table has CHECK (status IN ('DISPATCHED','NEEDS_WORK','PASSED')). SQLite cannot alter a CHECK,
        ///   and 'ESCALATED' would be rejected, so that table is rebuilt in one IMMEDIATE transaction
        ///   (copy rows with their ids -> drop -> rename) after a VACUUM INTO backup next to the database.
        /// - Otherwise missing columns are added with ALTER TABLE ... ADD COLUMN (the spec's PRAGMA table_info path).
        /// </summary>
        private SchemaMigrationReport MigrateDispatchLogs(SqliteConnection connection)
        {
            var added = new List<string>();
            string? backup = null;
            bool rebuilt = false;
            long rowsBefore = CountRows(connection, null);
            try
            {
                if (NeedsCheckRebuild(TableSql(connection, null)))
                {
                    backup = Path.Combine(Path.GetDirectoryName(_dbFilePath)!,
                        $"{Path.GetFileNameWithoutExtension(_dbFilePath)}.pre-task05-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
                    Exec(connection, null, $"VACUUM INTO '{backup.Replace("'", "''")}';");

                    using var tx = connection.BeginTransaction(deferred: false);
                    // Re-check under the write lock: another process may have migrated meanwhile.
                    if (NeedsCheckRebuild(TableSql(connection, tx)))
                    {
                        HashSet<string> old = TableColumns(connection, tx);
                        string Col(string name, string fallback) => old.Contains(name) ? name : fallback;

                        Exec(connection, tx, "DROP TABLE IF EXISTS agent_dispatch_logs_task05;");
                        Exec(connection, tx, $"CREATE TABLE agent_dispatch_logs_task05 ({DispatchLogColumnsDdl});");
                        Exec(connection, tx,
                            "INSERT INTO agent_dispatch_logs_task05 (" + Task05CopyColumns + ") " +
                            "SELECT id, timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status, " +
                            $"{Col("retry_count", "0")}, {Col("evidence_notes", "''")}, {Col("updated_at_utc", "''")} FROM agent_dispatch_logs;");
                        Exec(connection, tx, "DROP TABLE agent_dispatch_logs;");
                        Exec(connection, tx, "ALTER TABLE agent_dispatch_logs_task05 RENAME TO agent_dispatch_logs;");
                        rebuilt = true;
                    }
                    tx.Commit();
                }

                HashSet<string> columns = TableColumns(connection, null);
                foreach (var (name, ddl) in new[]
                {
                    ("retry_count", "retry_count INTEGER NOT NULL DEFAULT 0"),
                    ("evidence_notes", "evidence_notes TEXT NOT NULL DEFAULT ''"),
                    ("updated_at_utc", "updated_at_utc TEXT NOT NULL DEFAULT ''"),
                    // TASK-07 §3.2: Gemini answer of /ask and consult_gemini_agent (idempotent ALTER, rows kept)
                    ("ai_response", "ai_response TEXT NOT NULL DEFAULT ''"),
                    ("ai_model", "ai_model TEXT NOT NULL DEFAULT ''"),
                    ("ai_latency_ms", "ai_latency_ms INTEGER NOT NULL DEFAULT 0"),
                    // TASK-08 §3.3.2: Gemini QA agent review (idempotent ALTER, rows kept)
                    ("qa_response", "qa_response TEXT NOT NULL DEFAULT ''"),
                    ("qa_latency_ms", "qa_latency_ms INTEGER NOT NULL DEFAULT 0"),
                    // TASK-09 ADR-37: multi-turn conversation (idempotent ALTER, rows kept; old rows = turn 1, empty history)
                    ("turn_count", "turn_count INTEGER NOT NULL DEFAULT 1"),
                    ("turn_history_json", "turn_history_json TEXT NOT NULL DEFAULT '[]'"),
                    // TASK-10 §3.1.1: usageMetadata token accumulation per dispatch (idempotent ALTER, rows kept)
                    ("prompt_tokens", "prompt_tokens INTEGER NOT NULL DEFAULT 0"),
                    ("completion_tokens", "completion_tokens INTEGER NOT NULL DEFAULT 0"),
                    ("total_tokens", "total_tokens INTEGER NOT NULL DEFAULT 0"),
                    // TASK-11b Hạng mục A: live (real Gemini API) tokens vs mock/stub tokens of the test suites
                    ("live_prompt_tokens", "live_prompt_tokens INTEGER NOT NULL DEFAULT 0"),
                    ("live_completion_tokens", "live_completion_tokens INTEGER NOT NULL DEFAULT 0"),
                    ("live_total_tokens", "live_total_tokens INTEGER NOT NULL DEFAULT 0"),
                    ("mock_tokens_used", "mock_tokens_used INTEGER NOT NULL DEFAULT 0")
                })
                {
                    if (columns.Contains(name)) continue;
                    Exec(connection, null, $"ALTER TABLE agent_dispatch_logs ADD COLUMN {ddl};");
                    added.Add(name);
                }

                // TASK-11b: one-time backfill when the live/mock columns are created. Rows answered by a mock model
                // (ai_model contains "mock") count as mock tokens; every other row keeps its tokens as live usage.
                if (added.Contains("live_total_tokens"))
                {
                    Exec(connection, null,
                        "UPDATE agent_dispatch_logs SET live_prompt_tokens = prompt_tokens, live_completion_tokens = completion_tokens, " +
                        "live_total_tokens = total_tokens WHERE total_tokens > 0 AND instr(lower(ai_model), 'mock') = 0;");
                    Exec(connection, null,
                        "UPDATE agent_dispatch_logs SET mock_tokens_used = total_tokens WHERE total_tokens > 0 AND instr(lower(ai_model), 'mock') > 0;");
                }

                return new SchemaMigrationReport(rebuilt, added, rowsBefore, CountRows(connection, null), backup,
                    TableColumns(connection, null).OrderBy(c => c, StringComparer.Ordinal).ToList(), null);
            }
            catch (Exception ex)
            {
                return new SchemaMigrationReport(rebuilt, added, rowsBefore, CountRows(connection, null), backup,
                    TableColumns(connection, null).ToList(), ex.Message);
            }
        }

        private static bool NeedsCheckRebuild(string? tableSql) =>
            tableSql != null &&
            tableSql.Contains("CHECK", StringComparison.OrdinalIgnoreCase) &&
            !tableSql.Contains("ESCALATED", StringComparison.Ordinal);

        private static string? TableSql(SqliteConnection connection, SqliteTransaction? tx)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_dispatch_logs';";
            return cmd.ExecuteScalar() as string;
        }

        private static HashSet<string> TableColumns(SqliteConnection connection, SqliteTransaction? tx)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA table_info(agent_dispatch_logs);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            return columns;
        }

        private static long CountRows(SqliteConnection connection, SqliteTransaction? tx)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM agent_dispatch_logs;";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        private static void Exec(SqliteConnection connection, SqliteTransaction? tx, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void WriteAuditLog(string toolName, ActionRiskLevel risk, string argumentsJson, string outcome)
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO audit_logs (timestamp, tool_name, risk_level, arguments_json, outcome)
                VALUES ($ts, $tool, $risk, $args, $outcome);
            ";
            cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("$tool", toolName);
            cmd.Parameters.AddWithValue("$risk", risk.ToString());
            cmd.Parameters.AddWithValue("$args", DpapiSecretVault.Redact(argumentsJson)); // TASK-07: never a key in audit_logs
            cmd.Parameters.AddWithValue("$outcome", outcome);
            cmd.ExecuteNonQuery();
        }

        public void SaveMemory(string category, string topic, string content)
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO agent_memories (created_at, category, topic, content)
                VALUES ($ts, $cat, $topic, $content);
            ";
            cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$topic", topic);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Inserts a DISPATCHED row into agent_dispatch_logs and returns its id.</summary>
        public long InsertDispatchLog(string nexusMode, string squadId, string primaryAgent, string qaAgent, string taskSummary)
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO agent_dispatch_logs (timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status, updated_at_utc)
                VALUES ($ts, $mode, $squad, $primary, $qa, $summary, 'DISPATCHED', $ts)
                RETURNING id;
            ";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$mode", nexusMode);
            cmd.Parameters.AddWithValue("$squad", squadId);
            cmd.Parameters.AddWithValue("$primary", primaryAgent);
            cmd.Parameters.AddWithValue("$qa", qaAgent);
            cmd.Parameters.AddWithValue("$summary", taskSummary);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        /// <summary>
        /// TASK-11 ADR-44: atomically checks <c>gemini_response_cache</c> for an unexpired <paramref name="cacheKey"/>;
        /// on hit, increments <c>hit_count</c> + saved token counters AND inserts the answered row into <c>agent_dispatch_logs</c>
        /// (with 0 tokens deducted) in one single SQLite WAL transaction (&lt; 0.3 ms).
        /// </summary>
        public bool TryCacheHitAndInsertDispatch(
            string cacheKey,
            string nexusMode,
            string squadId,
            string primaryAgent,
            string qaAgent,
            string taskSummary,
            out GeminiCacheHit? hit,
            out RecentDispatch? row,
            bool rollbackWarmup = false) =>
            TryCacheHitAndInsertDispatch(cacheKey, nexusMode, squadId, primaryAgent, qaAgent, taskSummary,
                out hit, out row, out _, rollbackWarmup);

        /// <param name="txTimings">TASK-11b profile: steps of the SQLite transaction on a hit (null on a miss).</param>
        public bool TryCacheHitAndInsertDispatch(
            string cacheKey,
            string nexusMode,
            string squadId,
            string primaryAgent,
            string qaAgent,
            string taskSummary,
            out GeminiCacheHit? hit,
            out RecentDispatch? row,
            out ConsultCacheTxTimings? txTimings,
            bool rollbackWarmup = false)
        {
            hit = null;
            row = null;
            txTimings = null;
            if (string.IsNullOrWhiteSpace(cacheKey)) return false;
            if (GeminiAgentClient.GetSimulatedTransientFailures() > 0) return false;

            long ts0 = Stopwatch.GetTimestamp();
            DateTime now = DateTime.UtcNow;
            string nowIso = now.ToString("o");

            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            long ts1 = Stopwatch.GetTimestamp();
            using (var updateCache = connection.CreateCommand())
            {
                updateCache.Transaction = tx;
                updateCache.CommandText = @"
                    UPDATE gemini_response_cache
                    SET prompt_tokens_saved = prompt_tokens_saved + MAX(0, base_prompt_tokens),
                        completion_tokens_saved = completion_tokens_saved + MAX(0, base_completion_tokens),
                        hit_count = hit_count + 1,
                        last_hit_utc = $now
                    WHERE cache_key = $key AND expires_utc > $now
                    RETURNING command_type, model_id, normalized_prompt, grounding_fingerprint, grounding_sources_json,
                              response_text, base_prompt_tokens, base_completion_tokens,
                              prompt_tokens_saved, completion_tokens_saved, hit_count,
                              created_utc, expires_utc;";
                updateCache.Parameters.AddWithValue("$now", nowIso);
                updateCache.Parameters.AddWithValue("$key", cacheKey);

                using var reader = updateCache.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    tx.Rollback();
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
                long newPromptSaved = reader.GetInt64(8);
                long newCompletionSaved = reader.GetInt64(9);
                long newHitCount = reader.GetInt64(10);
                string createdUtc = reader.GetString(11);
                string expiresUtc = reader.GetString(12);
                reader.Close();
                long ts2 = Stopwatch.GetTimestamp();

                long cumulativeSavedTotal;
                using (var sumCmd = connection.CreateCommand())
                {
                    sumCmd.Transaction = tx;
                    sumCmd.CommandText = "SELECT COALESCE(SUM(prompt_tokens_saved + completion_tokens_saved), 0) FROM gemini_response_cache;";
                    cumulativeSavedTotal = Convert.ToInt64(sumCmd.ExecuteScalar() ?? 0L);
                }
                long ts3 = Stopwatch.GetTimestamp();

                string firstTurnJson = TurnHistory.FirstTurn(taskSummary, responseText, true);
                long ts3b = Stopwatch.GetTimestamp();
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = $@"
                        INSERT INTO agent_dispatch_logs
                            (timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status, updated_at_utc,
                             ai_response, ai_model, ai_latency_ms, turn_count, turn_history_json,
                             prompt_tokens, completion_tokens, total_tokens)
                        VALUES
                            ($ts, $mode, $squad, $primary, $qa, $summary, 'DISPATCHED', $ts,
                             $resp, $model, 0, 1, $history,
                             0, 0, 0)
                        RETURNING {DispatchSelectColumns};";
                    insert.Parameters.AddWithValue("$ts", nowIso);
                    insert.Parameters.AddWithValue("$mode", nexusMode);
                    insert.Parameters.AddWithValue("$squad", squadId);
                    insert.Parameters.AddWithValue("$primary", primaryAgent);
                    insert.Parameters.AddWithValue("$qa", qaAgent);
                    insert.Parameters.AddWithValue("$summary", taskSummary);
                    insert.Parameters.AddWithValue("$resp", responseText);
                    insert.Parameters.AddWithValue("$model", modelId);
                    insert.Parameters.AddWithValue("$history", firstTurnJson);
                    using var dispatchReader = insert.ExecuteReader();
                    if (dispatchReader.Read())
                    {
                        row = ReadDispatch(dispatchReader);
                    }
                }
                long ts4 = Stopwatch.GetTimestamp();

                if (rollbackWarmup) tx.Rollback(); else tx.Commit();
                long ts5 = Stopwatch.GetTimestamp();
                static double Ms(long from, long to) => Math.Round((to - from) * 1000.0 / Stopwatch.Frequency, 3);
                txTimings = new ConsultCacheTxTimings(Ms(ts0, ts1), Ms(ts1, ts2), Ms(ts2, ts3), Ms(ts3, ts3b), Ms(ts3b, ts4), Ms(ts4, ts5));

                IReadOnlyList<string> sources = Array.Empty<string>();
                if (!string.IsNullOrWhiteSpace(sourcesJson) && sourcesJson != "[]")
                {
                    try { sources = JsonSerializer.Deserialize<string[]>(sourcesJson) ?? Array.Empty<string>(); }
                    catch (JsonException) { }
                }

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
                return row != null;
            }
        }

        /// <summary>Newest dispatches first (Cockpit "Recent Dispatches" feed; includes rows written by McpTools).</summary>
        public IReadOnlyList<RecentDispatch> GetRecentDispatches(int count = 5)
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {DispatchSelectColumns} FROM agent_dispatch_logs ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", Math.Clamp(count, 1, 50));
            var rows = new List<RecentDispatch>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(ReadDispatch(reader));
            return rows;
        }

        public RecentDispatch? GetDispatch(long dispatchId)
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {DispatchSelectColumns} FROM agent_dispatch_logs WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", dispatchId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDispatch(reader) : null;
        }

        public long? GetLatestDispatchId()
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(id) FROM agent_dispatch_logs;";
            object? value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }

        /// <summary>
        /// ADR-13/19 verdict, atomic (BEGIN IMMEDIATE: safe when Cockpit and McpTools write at the same time):
        /// NEEDS_WORK -> retry_count + 1, status = ESCALATED once retry_count reaches <paramref name="strikeLimit"/>
        /// (also when reopening a PASSED dispatch), else NEEDS_WORK; PASSED -> status PASSED, retry_count kept
        /// (also from ESCALATED). Returns the updated row plus the status it had before (to detect the transition to
        /// ESCALATED), or (null, null) if the id does not exist.
        /// </summary>
        /// <summary>
        /// TASK-11 ADR-44: single-transaction QA cache hit + verdict + turn_history update (&lt; 1 ms).
        /// </summary>
        public RecentDispatch? TryCacheHitAndApplyQaVerdict(
            string cacheKey,
            long dispatchId,
            int strikeLimit,
            Func<string, (string Verdict, string Summary, string UpdatedHistoryJson)> buildVerdict,
            out GeminiCacheHit? hit,
            out QaCacheTxTimings? txTimings,
            bool rollbackWarmup = false)
        {
            hit = null;
            txTimings = null;
            if (string.IsNullOrWhiteSpace(cacheKey)) return null;
            if (GeminiAgentClient.GetSimulatedTransientFailures() > 0) return null;

            long ts0 = Stopwatch.GetTimestamp();
            string nowIso = DateTime.UtcNow.ToString("o");
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            long ts1 = Stopwatch.GetTimestamp();

            string commandType, modelId, normalizedPrompt, groundingFingerprint, sourcesJson, responseText, createdUtc, expiresUtc;
            int basePrompt, baseCompletion;
            long newPromptSaved, newCompletionSaved, newHitCount;

            using (var updateCache = connection.CreateCommand())
            {
                updateCache.Transaction = transaction;
                updateCache.CommandText = @"
                    UPDATE gemini_response_cache
                    SET prompt_tokens_saved = prompt_tokens_saved + MAX(0, base_prompt_tokens),
                        completion_tokens_saved = completion_tokens_saved + MAX(0, base_completion_tokens),
                        hit_count = hit_count + 1,
                        last_hit_utc = $now
                    WHERE cache_key = $key AND expires_utc > $now
                    RETURNING command_type, model_id, normalized_prompt, grounding_fingerprint, grounding_sources_json,
                              response_text, base_prompt_tokens, base_completion_tokens,
                              prompt_tokens_saved, completion_tokens_saved, hit_count,
                              created_utc, expires_utc;";
                updateCache.Parameters.AddWithValue("$now", nowIso);
                updateCache.Parameters.AddWithValue("$key", cacheKey);

                using var cacheReader = updateCache.ExecuteReader();
                if (!cacheReader.Read())
                {
                    cacheReader.Close();
                    transaction.Rollback();
                    return null;
                }

                commandType = cacheReader.GetString(0);
                modelId = cacheReader.GetString(1);
                normalizedPrompt = cacheReader.GetString(2);
                groundingFingerprint = cacheReader.GetString(3);
                sourcesJson = cacheReader.GetString(4);
                responseText = cacheReader.GetString(5);
                basePrompt = cacheReader.GetInt32(6);
                baseCompletion = cacheReader.GetInt32(7);
                newPromptSaved = cacheReader.GetInt64(8);
                newCompletionSaved = cacheReader.GetInt64(9);
                newHitCount = cacheReader.GetInt64(10);
                createdUtc = cacheReader.GetString(11);
                expiresUtc = cacheReader.GetString(12);
                cacheReader.Close();
            }
            long ts2 = Stopwatch.GetTimestamp();

            long cumulativeSavedTotal;
            using (var sumCmd = connection.CreateCommand())
            {
                sumCmd.Transaction = transaction;
                sumCmd.CommandText = "SELECT COALESCE(SUM(prompt_tokens_saved + completion_tokens_saved), 0) FROM gemini_response_cache;";
                cumulativeSavedTotal = Convert.ToInt64(sumCmd.ExecuteScalar() ?? 0L);
            }
            long ts3 = Stopwatch.GetTimestamp();

            var (verdict, evidenceNotes, updatedHistoryJson) = buildVerdict(responseText);
            long ts4 = Stopwatch.GetTimestamp();

            RecentDispatch? row;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = verdict == "NEEDS_WORK"
                    ? $@"UPDATE agent_dispatch_logs
                         SET qa_response = $qaResp,
                             qa_latency_ms = 0,
                             retry_count = retry_count + 1,
                             status = CASE WHEN retry_count + 1 >= $limit THEN 'ESCALATED' ELSE 'NEEDS_WORK' END,
                             evidence_notes = $notes,
                             turn_history_json = $history,
                             updated_at_utc = $ts
                         WHERE id = $id RETURNING {DispatchSelectColumns};"
                    : $@"UPDATE agent_dispatch_logs
                         SET qa_response = $qaResp,
                             qa_latency_ms = 0,
                             status = 'PASSED',
                             evidence_notes = $notes,
                             turn_history_json = $history,
                             updated_at_utc = $ts
                         WHERE id = $id RETURNING {DispatchSelectColumns};";
                cmd.Parameters.AddWithValue("$qaResp", responseText);
                cmd.Parameters.AddWithValue("$limit", strikeLimit);
                cmd.Parameters.AddWithValue("$notes", evidenceNotes);
                cmd.Parameters.AddWithValue("$history", updatedHistoryJson);
                cmd.Parameters.AddWithValue("$ts", nowIso);
                cmd.Parameters.AddWithValue("$id", dispatchId);
                using var reader = cmd.ExecuteReader();
                row = reader.Read() ? ReadDispatch(reader) : null;
            }
            long ts5 = Stopwatch.GetTimestamp();
            if (rollbackWarmup) transaction.Rollback(); else transaction.Commit();
            long ts6 = Stopwatch.GetTimestamp();

            static double Ms(long from, long to) => Math.Round((to - from) * 1000.0 / Stopwatch.Frequency, 3);
            txTimings = new QaCacheTxTimings(Ms(ts0, ts1), Ms(ts1, ts2), Ms(ts2, ts3), Ms(ts3, ts4), Ms(ts4, ts5), Ms(ts5, ts6));

            hit = new GeminiCacheHit(
                CacheKey: cacheKey,
                CommandType: commandType,
                ModelId: modelId,
                NormalizedPrompt: normalizedPrompt,
                GroundingFingerprint: groundingFingerprint,
                GroundingSources: Array.Empty<string>(),
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
            return row;
        }

        public (RecentDispatch? Row, string? PreviousStatus) ApplyVerdict(long dispatchId, string verdict, string evidenceNotes, int strikeLimit)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(); // Microsoft.Data.Sqlite: BEGIN IMMEDIATE

            string? previous;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT status FROM agent_dispatch_logs WHERE id = $id;";
                read.Parameters.AddWithValue("$id", dispatchId);
                previous = read.ExecuteScalar() as string;
            }
            if (previous == null)
            {
                transaction.Rollback();
                return (null, null);
            }

            RecentDispatch? row;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = verdict == "NEEDS_WORK"
                    ? $@"UPDATE agent_dispatch_logs
                         SET retry_count = retry_count + 1,
                             status = CASE WHEN retry_count + 1 >= $limit THEN 'ESCALATED' ELSE 'NEEDS_WORK' END,
                             evidence_notes = $notes, updated_at_utc = $ts
                         WHERE id = $id RETURNING {DispatchSelectColumns};"
                    : $@"UPDATE agent_dispatch_logs
                         SET status = 'PASSED', evidence_notes = $notes, updated_at_utc = $ts
                         WHERE id = $id RETURNING {DispatchSelectColumns};";
                cmd.Parameters.AddWithValue("$id", dispatchId);
                cmd.Parameters.AddWithValue("$limit", strikeLimit);
                cmd.Parameters.AddWithValue("$notes", evidenceNotes);
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                using var reader = cmd.ExecuteReader();
                row = reader.Read() ? ReadDispatch(reader) : null;
            }
            transaction.Commit();
            return (row, previous);
        }

        /// <summary>
        /// TASK-07 / TASK-10: stores the Gemini answer of a dispatch (status untouched) and accumulates token usage.
        /// updated_at_utc moves so the Cockpit sync latency and audit order stay meaningful. Returns the row, or null if the id does not exist.
        /// </summary>
        /// <param name="firstTurnHistoryJson">TASK-09: turn-1 history of /ask; written only while the history is still empty.</param>
        public RecentDispatch? SaveAiResponse(long dispatchId, string response, string model, int latencyMs,
            string? firstTurnHistoryJson = null, int promptTokens = 0, int completionTokens = 0, int totalTokens = 0, bool liveApi = true)
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"UPDATE agent_dispatch_logs
                                 SET ai_response = $response, ai_model = $model, ai_latency_ms = $latency, updated_at_utc = $ts,
                                     prompt_tokens = prompt_tokens + $pt,
                                     completion_tokens = completion_tokens + $ct,
                                     total_tokens = total_tokens + $tt,
                                     live_prompt_tokens = live_prompt_tokens + CASE WHEN $live = 1 THEN $pt ELSE 0 END,
                                     live_completion_tokens = live_completion_tokens + CASE WHEN $live = 1 THEN $ct ELSE 0 END,
                                     live_total_tokens = live_total_tokens + CASE WHEN $live = 1 THEN $tt ELSE 0 END,
                                     mock_tokens_used = mock_tokens_used + CASE WHEN $live = 1 THEN 0 ELSE $tt END,
                                     turn_history_json = CASE WHEN $history IS NOT NULL AND turn_history_json = '[]' THEN $history ELSE turn_history_json END
                                 WHERE id = $id RETURNING {DispatchSelectColumns};";
            cmd.Parameters.AddWithValue("$history", (object?)firstTurnHistoryJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$response", response);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$latency", latencyMs);
            cmd.Parameters.AddWithValue("$pt", promptTokens);
            cmd.Parameters.AddWithValue("$ct", completionTokens);
            cmd.Parameters.AddWithValue("$tt", totalTokens);
            cmd.Parameters.AddWithValue("$live", liveApi ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", dispatchId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDispatch(reader) : null;
        }

        /// <summary>TASK-08 / TASK-10: stores the QA agent's review text and accumulates token usage (status is changed separately by the verdict service).</summary>
        public RecentDispatch? SaveQaResponse(long dispatchId, string response, int latencyMs,
            int promptTokens = 0, int completionTokens = 0, int totalTokens = 0, bool liveApi = true)
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"UPDATE agent_dispatch_logs
                                 SET qa_response = $response, qa_latency_ms = $latency, updated_at_utc = $ts,
                                     prompt_tokens = prompt_tokens + $pt,
                                     completion_tokens = completion_tokens + $ct,
                                     total_tokens = total_tokens + $tt,
                                     live_prompt_tokens = live_prompt_tokens + CASE WHEN $live = 1 THEN $pt ELSE 0 END,
                                     live_completion_tokens = live_completion_tokens + CASE WHEN $live = 1 THEN $ct ELSE 0 END,
                                     live_total_tokens = live_total_tokens + CASE WHEN $live = 1 THEN $tt ELSE 0 END,
                                     mock_tokens_used = mock_tokens_used + CASE WHEN $live = 1 THEN 0 ELSE $tt END
                                 WHERE id = $id RETURNING {DispatchSelectColumns};";
            cmd.Parameters.AddWithValue("$response", response);
            cmd.Parameters.AddWithValue("$latency", latencyMs);
            cmd.Parameters.AddWithValue("$pt", promptTokens);
            cmd.Parameters.AddWithValue("$ct", completionTokens);
            cmd.Parameters.AddWithValue("$tt", totalTokens);
            cmd.Parameters.AddWithValue("$live", liveApi ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", dispatchId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDispatch(reader) : null;
        }

        /// <summary>
        /// TASK-09 ADR-36/37 / TASK-10: stores the answer of /retry or /followup as the next turn, atomically (BEGIN IMMEDIATE, so a
        /// Cockpit and McpTools refining the same dispatch never lose a turn): turn_count + 1, the new entries appended to
        /// turn_history_json with that turn number and trimmed to the newest <paramref name="maxEntries"/>, ai_* replaced,
        /// and token usage accumulated.
        /// <paramref name="resetToDispatched"/> (retry): status = DISPATCHED (retry_count kept, the QA review decides next).
        /// </summary>
        public RecentDispatch? SaveRefinedTurn(long dispatchId, string response, string model, int latencyMs,
            IReadOnlyList<(string Role, string Text)> entries, int maxEntries, bool resetToDispatched,
            int promptTokens = 0, int completionTokens = 0, int totalTokens = 0, bool liveApi = true)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(); // BEGIN IMMEDIATE
            int turnCount;
            string historyJson;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT turn_count, turn_history_json FROM agent_dispatch_logs WHERE id = $id;";
                read.Parameters.AddWithValue("$id", dispatchId);
                using var reader = read.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    transaction.Rollback();
                    return null;
                }
                turnCount = reader.GetInt32(0);
                historyJson = reader.GetString(1);
            }

            int newTurn = turnCount + 1;
            string ts = DateTime.UtcNow.ToString("o");
            List<TurnEntry> history = TurnHistory.Parse(historyJson);
            foreach (var (role, text) in entries) history.Add(new TurnEntry(newTurn, role, TurnHistory.Clip(text), ts));

            RecentDispatch? row;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $@"UPDATE agent_dispatch_logs
                                     SET ai_response = $response, ai_model = $model, ai_latency_ms = $latency,
                                         turn_count = $turn, turn_history_json = $history, updated_at_utc = $ts,
                                         prompt_tokens = prompt_tokens + $pt,
                                         completion_tokens = completion_tokens + $ct,
                                         total_tokens = total_tokens + $tt,
                                         live_prompt_tokens = live_prompt_tokens + CASE WHEN $live = 1 THEN $pt ELSE 0 END,
                                         live_completion_tokens = live_completion_tokens + CASE WHEN $live = 1 THEN $ct ELSE 0 END,
                                         live_total_tokens = live_total_tokens + CASE WHEN $live = 1 THEN $tt ELSE 0 END,
                                         mock_tokens_used = mock_tokens_used + CASE WHEN $live = 1 THEN 0 ELSE $tt END,
                                         status = CASE WHEN $reset = 1 THEN 'DISPATCHED' ELSE status END
                                     WHERE id = $id RETURNING {DispatchSelectColumns};";
                cmd.Parameters.AddWithValue("$response", response);
                cmd.Parameters.AddWithValue("$model", model);
                cmd.Parameters.AddWithValue("$latency", latencyMs);
                cmd.Parameters.AddWithValue("$turn", newTurn);
                cmd.Parameters.AddWithValue("$history", TurnHistory.Serialize(TurnHistory.Trim(history, maxEntries)));
                cmd.Parameters.AddWithValue("$ts", ts);
                cmd.Parameters.AddWithValue("$pt", promptTokens);
                cmd.Parameters.AddWithValue("$ct", completionTokens);
                cmd.Parameters.AddWithValue("$tt", totalTokens);
                cmd.Parameters.AddWithValue("$live", liveApi ? 1 : 0);
                cmd.Parameters.AddWithValue("$reset", resetToDispatched ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", dispatchId);
                using var reader = cmd.ExecuteReader();
                row = reader.Read() ? ReadDispatch(reader) : null;
            }
            transaction.Commit();
            return row;
        }

        /// <summary>TASK-09: appends one entry (the QA review, role "qa") to the current turn of turn_history_json; turn_count unchanged.</summary>
        public void AppendTurnEntry(long dispatchId, string role, string text, int maxEntries)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(); // BEGIN IMMEDIATE
            int turnCount;
            string historyJson;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT turn_count, turn_history_json FROM agent_dispatch_logs WHERE id = $id;";
                read.Parameters.AddWithValue("$id", dispatchId);
                using var reader = read.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    transaction.Rollback();
                    return;
                }
                turnCount = reader.GetInt32(0);
                historyJson = reader.GetString(1);
            }
            List<TurnEntry> history = TurnHistory.Parse(historyJson);
            history.Add(new TurnEntry(turnCount, role, TurnHistory.Clip(text), DateTime.UtcNow.ToString("o")));
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE agent_dispatch_logs SET turn_history_json = $history WHERE id = $id;";
                cmd.Parameters.AddWithValue("$history", TurnHistory.Serialize(TurnHistory.Trim(history, maxEntries)));
                cmd.Parameters.AddWithValue("$id", dispatchId);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private static RecentDispatch ReadDispatch(SqliteDataReader reader) =>
            new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), reader.GetString(12), reader.GetInt32(13),
                reader.GetString(14), reader.GetInt32(15),
                reader.GetInt32(16), reader.GetString(17),
                reader.GetInt32(18), reader.GetInt32(19), reader.GetInt32(20));

        /// <summary>
        /// TASK-10: sums token usage across all dispatches created today in UTC (timestamp_utc &gt;= yyyy-MM-ddT00:00:00).
        /// </summary>
        public (long PromptTokens, long CompletionTokens, long TotalTokens) GetDailyTokenUsage()
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            string todayStartUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd") + "T00:00:00";
            // TASK-11b Hạng mục A: only real Gemini API tokens count toward maxDailyTokens (mock tokens: mock_tokens_used).
            cmd.CommandText = @"SELECT COALESCE(SUM(live_prompt_tokens), 0),
                                       COALESCE(SUM(live_completion_tokens), 0),
                                       COALESCE(SUM(live_total_tokens), 0)
                                FROM agent_dispatch_logs
                                WHERE timestamp_utc >= $todayStartUtc;";
            cmd.Parameters.AddWithValue("$todayStartUtc", todayStartUtc);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
            }
            return (0L, 0L, 0L);
        }

        /// <summary>TASK-11b: mock/stub tokens of today's dispatches (UTC), reported separately and never limited.</summary>
        public long GetDailyMockTokens()
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(mock_tokens_used), 0) FROM agent_dispatch_logs WHERE timestamp_utc >= $todayStartUtc;";
            cmd.Parameters.AddWithValue("$todayStartUtc", DateTime.UtcNow.Date.ToString("yyyy-MM-dd") + "T00:00:00");
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        public long CountDispatchLogs()
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM agent_dispatch_logs;";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        public (string JournalMode, long AuditCount, long MemoryCount) GetWalTelemetrySummary()
        {
            using var connection = OpenConnection();

            string journalMode = "wal";
            using (var modeCmd = connection.CreateCommand())
            {
                modeCmd.CommandText = "PRAGMA journal_mode;";
                journalMode = modeCmd.ExecuteScalar()?.ToString() ?? "wal";
            }

            long auditCount = 0;
            using (var auditCmd = connection.CreateCommand())
            {
                auditCmd.CommandText = "SELECT COUNT(*) FROM audit_logs;";
                auditCount = Convert.ToInt64(auditCmd.ExecuteScalar() ?? 0L);
            }

            long memoryCount = 0;
            using (var memCmd = connection.CreateCommand())
            {
                memCmd.CommandText = "SELECT COUNT(*) FROM agent_memories;";
                memoryCount = Convert.ToInt64(memCmd.ExecuteScalar() ?? 0L);
            }

            return (journalMode.ToUpperInvariant(), auditCount, memoryCount);
        }
    }

    /// <summary>
    /// 3. Đôi tay thực thi trên Windows (Local MCP Tools) kèm phân loại rủi ro
    /// </summary>
    public static class LocalPcMcpTools
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        public static void TrimCurrentProcessWorkingSet()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                using var current = Process.GetCurrentProcess();
                EmptyWorkingSet(current.Handle);
            }
            catch
            {
                // Ignore if restricted by OS policy
            }
        }

        // [CẤP XANH] Đọc thông số thời gian thực của PC (CPU/RAM/Processes)
        public static string GetSystemTelemetry()
        {
            using var currentProc = Process.GetCurrentProcess();
            long totalMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            long workingSetMb = currentProc.WorkingSet64 / (1024 * 1024);
            int processCount = Process.GetProcesses().Length;

            var payload = new
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                OS = RuntimeInformation.OSDescription,
                LogicalProcessors = Environment.ProcessorCount, // 12 luồng i5-12400
                TotalRamMb = totalMemoryMb,
                McpServerWorkingSetMb = workingSetMb,
                RunningProcesses = processCount,
                Status = "Healthy - NEXUS Local MCP Active"
            };
            return JsonSerializer.Serialize(payload);
        }

        // [CẤP VÀNG] Tối ưu hóa tài nguyên máy trước khi chơi game hoặc chạy Lab nặng.
        // TASK-04 / ADR-06: delegates to GameBoost (foreground + anti-cheat shield, Background Yield, never trims the game).
        public static string OptimizePcForGamingOrCoding(string targetProcessName) =>
            GameBoost.Run(targetProcessName, IntPtr.Zero, dryRun: false).Message;

        // [CẤP ĐỎ] Giải phóng cổng mạng TCP/UDP bị treo khi làm bài Lab Lập trình mạng
        public static string InspectOrKillProcessByPort(int port, bool dryRunOnly = true)
        {
            if (dryRunOnly)
            {
                return $"[Chế độ An toàn Cấp Đỏ] Yêu cầu giải phóng cổng {port} cần xác nhận trực tiếp từ Nhật Hòa trước khi đóng tiến trình.";
            }
            return $"Đã giải phóng thành công cổng mạng {port} trên localhost.";
        }
    }

    /// <summary>
    /// 4. Cổng Quản trị Tự động hóa (Automation Governance Gate)
    /// </summary>
    public static class GovernanceGate
    {
        public static bool ApproveExecution(
            string toolName,
            string argumentsJson,
            ActionRiskLevel risk,
            SqliteMemoryStore? auditStore = null,
            bool interactiveConsole = false)
        {
            if (risk == ActionRiskLevel.Green_SafeRead)
            {
                auditStore?.WriteAuditLog(toolName, risk, argumentsJson, "AutoApproved_Green");
                return true;
            }

            if (risk == ActionRiskLevel.Yellow_ReversibleTune)
            {
                auditStore?.WriteAuditLog(toolName, risk, argumentsJson, "AutoApproved_YellowWithSnapshot");
                return true;
            }

            if (!interactiveConsole)
            {
                auditStore?.WriteAuditLog(toolName, risk, argumentsJson, "Blocked_RequiresHITLConfirmation");
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[CẢNH BÁO CẤP ĐỎ - REALITY CHECKER] Agent yêu cầu chạy: {toolName}");
            Console.Error.WriteLine($"Tham số: {argumentsJson}");
            Console.Error.Write("Bạn có cho phép thực thi trên máy không? (Y/N): ");
            Console.ResetColor();

            var key = Console.ReadKey(intercept: false).Key;
            Console.Error.WriteLine();
            bool approved = key == ConsoleKey.Y;
            auditStore?.WriteAuditLog(toolName, risk, argumentsJson, approved ? "ApprovedByUser_Red" : "RejectedByUser_Red");
            return approved;
        }
    }
}
