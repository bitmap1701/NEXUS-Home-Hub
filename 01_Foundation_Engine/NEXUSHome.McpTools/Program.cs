using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NEXUSHomeHub.Core;

namespace NEXUSHome.McpTools
{
    class Program
    {
        static void Main(string[] args)
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = utf8NoBom;
            Console.OutputEncoding = utf8NoBom;

            string vaultPath = Environment.GetEnvironmentVariable("NEXUS_VAULT_PATH")
                ?? @"C:\Agent\NEXUS_Home_Hub\03_Local_Data_Vault";
            string agentsPath = Environment.GetEnvironmentVariable("NEXUS_AGENTS_PATH")
                ?? @"C:\Agent\.agents\skills";

            string dbPath = Path.Combine(vaultPath, "structured_db", "nexus_memory.db");
            string logPath = Path.Combine(vaultPath, "handoff", "mcp_debug.log");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var memoryStore = new SqliteMemoryStore(dbPath);
            var agentRegistry = new AgentRegistry(agentsPath);

            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] NEXUS Local MCP Server started (DB: {dbPath}).\n");

            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var request = JsonNode.Parse(line);
                    string? method = request?["method"]?.ToString();
                    JsonNode? idNode = request?["id"]?.DeepClone();

                    if (method == "initialize")
                    {
                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idNode,
                            ["result"] = new JsonObject
                            {
                                ["protocolVersion"] = "2024-11-05",
                                ["capabilities"] = new JsonObject
                                {
                                    ["tools"] = new JsonObject()
                                },
                                ["serverInfo"] = new JsonObject
                                {
                                    ["name"] = "NEXUS-Local-MCP",
                                    ["version"] = "1.0.0"
                                }
                            }
                        };
                        Console.WriteLine(response.ToJsonString());
                    }
                    else if (method == "notifications/initialized")
                    {
                        // No response required for notification
                    }
                    else if (method == "tools/list")
                    {
                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idNode,
                            ["result"] = new JsonObject
                            {
                                ["tools"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "get_system_telemetry",
                                        ["description"] = "[CẤP XANH] Đọc thông số CPU i5-12400, RAM và tiến trình trên máy của Nhật Hòa.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject()
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "list_available_agents",
                                        ["description"] = "[CẤP XANH] Liệt kê danh sách các Agent chuyên gia trong kho kỹ năng cục bộ.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["maxCount"] = new JsonObject
                                                {
                                                    ["type"] = "integer",
                                                    ["description"] = "Số lượng Agent tối đa cần lấy"
                                                }
                                            }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "optimize_pc_for_gaming_or_coding",
                                        ["description"] = "[CẤP VÀNG] Dọn Working Set RAM các tiến trình nền và nâng Priority cho game/IDE.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["targetProcessName"] = new JsonObject
                                                {
                                                    ["type"] = "string",
                                                    ["description"] = "Tên tiến trình ưu tiên (ví dụ: TslGame, DeltaForce, devenv)"
                                                }
                                            },
                                            ["required"] = new JsonArray { "targetProcessName" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "save_nexus_memory",
                                        ["description"] = "[CẤP XANH] Lưu ghi chú hoặc trạng thái dự án vào SQLite WAL (nexus_memory.db).",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["category"] = new JsonObject { ["type"] = "string" },
                                                ["topic"] = new JsonObject { ["type"] = "string" },
                                                ["content"] = new JsonObject { ["type"] = "string" }
                                            },
                                            ["required"] = new JsonArray { "category", "topic", "content" }
                                        }
                                    }
                                }
                            }
                        };
                        Console.WriteLine(response.ToJsonString());
                    }
                    else if (method == "tools/call")
                    {
                        string? toolName = request?["params"]?["name"]?.ToString();
                        JsonNode? toolArgs = request?["params"]?["arguments"];
                        string argsJson = toolArgs?.ToJsonString() ?? "{}";

                        string resultText = toolName switch
                        {
                            "get_system_telemetry" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? LocalPcMcpTools.GetSystemTelemetry()
                                    : "Blocked.",

                            "list_available_agents" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? JsonSerializer.Serialize(agentRegistry.ListAvailableAgents(toolArgs?["maxCount"]?.GetValue<int>() ?? 50))
                                    : "Blocked.",

                            "optimize_pc_for_gaming_or_coding" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Yellow_ReversibleTune, memoryStore)
                                    ? LocalPcMcpTools.OptimizePcForGamingOrCoding(toolArgs?["targetProcessName"]?.ToString() ?? "devenv")
                                    : "Blocked.",

                            "save_nexus_memory" => ExecuteSaveMemory(memoryStore, toolArgs),

                            _ => $"Unknown tool: {toolName}"
                        };

                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idNode,
                            ["result"] = new JsonObject
                            {
                                ["content"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = resultText
                                    }
                                }
                            }
                        };
                        Console.WriteLine(response.ToJsonString());
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex.Message}\n");
                }
            }
        }

        private static string ExecuteSaveMemory(SqliteMemoryStore store, JsonNode? args)
        {
            string category = args?["category"]?.ToString() ?? "general";
            string topic = args?["topic"]?.ToString() ?? "note";
            string content = args?["content"]?.ToString() ?? "";
            store.SaveMemory(category, topic, content);
            store.WriteAuditLog("save_nexus_memory", ActionRiskLevel.Green_SafeRead, args?.ToJsonString() ?? "{}", "SavedToSqliteWal");
            return $"Đã lưu vào SQLite WAL (nexus_memory.db) -> [{category}] {topic}";
        }
    }
}
