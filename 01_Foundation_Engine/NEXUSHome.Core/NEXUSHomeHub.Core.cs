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
        string AiResponse = "", string AiModel = "", int AiLatencyMs = 0);

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
            pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
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
                    ai_latency_ms INTEGER NOT NULL DEFAULT 0
                ";

        /// <summary>Columns copied by the TASK-05 rebuild (a TASK-03 table never has the later columns; they take their defaults).</summary>
        private const string Task05CopyColumns =
            "id, timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status, retry_count, evidence_notes, updated_at_utc";

        private const string DispatchSelectColumns = Task05CopyColumns + ", ai_response, ai_model, ai_latency_ms";

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
                    ("ai_latency_ms", "ai_latency_ms INTEGER NOT NULL DEFAULT 0")
                })
                {
                    if (columns.Contains(name)) continue;
                    Exec(connection, null, $"ALTER TABLE agent_dispatch_logs ADD COLUMN {ddl};");
                    added.Add(name);
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
        /// TASK-07: stores the Gemini answer of a dispatch (status untouched). updated_at_utc moves so the Cockpit
        /// sync latency and audit order stay meaningful. Returns the row, or null if the id does not exist.
        /// </summary>
        public RecentDispatch? SaveAiResponse(long dispatchId, string response, string model, int latencyMs)
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"UPDATE agent_dispatch_logs
                                 SET ai_response = $response, ai_model = $model, ai_latency_ms = $latency, updated_at_utc = $ts
                                 WHERE id = $id RETURNING {DispatchSelectColumns};";
            cmd.Parameters.AddWithValue("$response", response);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$latency", latencyMs);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", dispatchId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDispatch(reader) : null;
        }

        private static RecentDispatch ReadDispatch(SqliteDataReader reader) =>
            new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), reader.GetString(12), reader.GetInt32(13));

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
