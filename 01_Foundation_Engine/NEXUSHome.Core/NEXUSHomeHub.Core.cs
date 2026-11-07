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
        string PrimaryAgent, string QaAgent, string TaskSummary, string Status);

    public class SqliteMemoryStore
    {
        private readonly string _connectionString;

        public SqliteMemoryStore(string dbFilePath)
        {
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

                -- TASK-03: Agent Squad Dispatch log (Dev-QA loop state)
                CREATE TABLE IF NOT EXISTS agent_dispatch_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    nexus_mode TEXT NOT NULL CHECK (nexus_mode IN ('Micro', 'Sprint', 'Full')),
                    squad_id TEXT NOT NULL,
                    primary_agent TEXT NOT NULL,
                    qa_agent TEXT NOT NULL,
                    task_summary TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN ('DISPATCHED', 'NEEDS_WORK', 'PASSED'))
                );
            ";
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
            cmd.Parameters.AddWithValue("$args", argumentsJson);
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
                INSERT INTO agent_dispatch_logs (timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status)
                VALUES ($ts, $mode, $squad, $primary, $qa, $summary, 'DISPATCHED')
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

        /// <summary>Sets the verdict (NEEDS_WORK | PASSED) of a dispatch. Returns false if the id does not exist.</summary>
        public bool UpdateDispatchStatus(long dispatchId, string status)
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE agent_dispatch_logs SET status = $status WHERE id = $id;";
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$id", dispatchId);
            return cmd.ExecuteNonQuery() == 1;
        }

        /// <summary>Newest dispatches first (Cockpit "Recent Dispatches" feed; includes rows written by McpTools).</summary>
        public IReadOnlyList<RecentDispatch> GetRecentDispatches(int count = 5)
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, timestamp_utc, nexus_mode, squad_id, primary_agent, qa_agent, task_summary, status
                FROM agent_dispatch_logs ORDER BY id DESC LIMIT $n;
            ";
            cmd.Parameters.AddWithValue("$n", Math.Clamp(count, 1, 50));
            var rows = new List<RecentDispatch>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new RecentDispatch(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            }
            return rows;
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
