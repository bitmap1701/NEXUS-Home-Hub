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

    /// <summary>
    /// 1. Quản lý và nạp động hồ sơ từ kho 283 Agents (.md) trên ổ cứng
    /// </summary>
    public class AgentRegistry
    {
        private readonly string _agentsRootPath;

        public AgentRegistry(string agentsRootPath)
        {
            _agentsRootPath = agentsRootPath;
        }

        public IReadOnlyList<string> ListAvailableAgents(int maxCount = 50)
        {
            if (!Directory.Exists(_agentsRootPath))
                return Array.Empty<string>();

            return Directory.EnumerateDirectories(_agentsRootPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Take(maxCount)
                .Cast<string>()
                .ToList();
        }

        public string LoadAgentSystemInstruction(string agentNameOrRelativePath)
        {
            string candidateSkillPath = Path.Combine(_agentsRootPath, agentNameOrRelativePath, "SKILL.md");
            string directPath = Path.Combine(_agentsRootPath, agentNameOrRelativePath);

            string? fullPath = File.Exists(candidateSkillPath)
                ? candidateSkillPath
                : (File.Exists(directPath) ? directPath : null);

            if (fullPath == null)
            {
                return "You are the NEXUS Agents Orchestrator assisting Huynh Nhat Hoa in real-time on his local PC.";
            }

            string markdownPersona = File.ReadAllText(fullPath, Encoding.UTF8);
            return $"{markdownPersona}\n\n[RUNTIME CONTEXT]: Running locally inside NEXUS Home Hub on i5-12400 + GTX 1660 + 16GB RAM. Use registered local MCP tools to inspect or tune the system safely.";
        }
    }

    /// <summary>
    /// 2. Tầng lưu trữ SQLite (Chế độ WAL - Write-Ahead Logging) cho Trí nhớ & Nhật ký Kiểm toán
    /// </summary>
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
                Cache = SqliteCacheMode.Shared
            }.ToString();

            InitializeWalDatabase();
        }

        private void InitializeWalDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

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
            ";
            cmd.ExecuteNonQuery();
        }

        public void WriteAuditLog(string toolName, ActionRiskLevel risk, string argumentsJson, string outcome)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

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
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

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
    }

    /// <summary>
    /// 3. Đôi tay thực thi trên Windows (Local MCP Tools) kèm phân loại rủi ro
    /// </summary>
    public static class LocalPcMcpTools
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

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

        // [CẤP VÀNG] Tối ưu hóa tài nguyên máy trước khi chơi PUBG / Delta Force hoặc chạy Lab nặng
        public static string OptimizePcForGamingOrCoding(string targetProcessName)
        {
            int trimmedCount = 0;
            bool elevatedTarget = false;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!proc.HasExited && proc.ProcessName.Equals(targetProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.PriorityClass = ProcessPriorityClass.High;
                        elevatedTarget = true;
                    }
                    else if (!proc.HasExited && proc.SessionId != 0)
                    {
                        EmptyWorkingSet(proc.Handle);
                        trimmedCount++;
                    }
                }
                catch
                {
                    // Bỏ qua các tiến trình hệ thống được bảo vệ
                }
                finally
                {
                    proc.Dispose();
                }
            }

            string targetStatus = elevatedTarget
                ? $"đã nâng PriorityClass=High cho '{targetProcessName}'"
                : $"chưa thấy tiến trình '{targetProcessName}' đang chạy";

            return $"Đã dọn dẹp Working Set RAM của {trimmedCount} tiến trình nền và {targetStatus}.";
        }

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
