using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Linq;
using System.Text.Encodings.Web;
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

            // TASK-06 ADR-21: same composition root as HostService (config, SQLite WAL + migration, registry, dispatcher,
            // ADR-19 verdict state machine). Both sides sync through latest_dispatch.json (ADR-14).
            NexusRuntime runtime = NexusRuntime.Create("McpTools");
            string logPath = Path.Combine(runtime.HandoffDirectory, "mcp_debug.log");
            SqliteMemoryStore memoryStore = runtime.Store;
            AgentRegistry agentRegistry = runtime.Registry;
            SquadDispatcher squadDispatcher = runtime.Dispatcher;
            DispatchVerdictService verdictService = runtime.Verdicts;
            string latestDispatchPath = runtime.LatestDispatchPath;

            NexusConfigLoadResult configLoad = runtime.Config.LastResult;
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] NEXUS Local MCP Server started (DB: {runtime.DatabasePath}, " +
                $"config ok={configLoad.Ok} fingerprint={runtime.Config.Fingerprint}{(configLoad.Error != null ? " error=" + configLoad.Error : string.Empty)}).\n");

            // TASK-03 §A.1: make sure the 5 squad primaries + Reality Checker exist in 02_Agents_Registry (Yellow: file writes, never overwrites).
            foreach (string action in agentRegistry.SeedCoreAgents(SquadDispatcher.CoreAgentSeeds))
            {
                memoryStore.WriteAuditLog("seed_core_agents", ActionRiskLevel.Yellow_ReversibleTune, "{}", action);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SEED: {action}\n");
            }

            // Warm the metadata index before the first request so dispatch stays under the 50 ms budget.
            var indexWatch = Stopwatch.StartNew();
            int agentCount = agentRegistry.Count;
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Agent index: {agentCount} agents in {indexWatch.Elapsed.TotalMilliseconds:F1} ms from {string.Join(" | ", agentRegistry.Roots)}\n");

            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                // Windows PowerShell 5.1 (and some other Windows clients) prefix stdin with a UTF-8 BOM (U+FEFF);
                // without this the first request of every session failed to parse and was silently dropped.
                line = line.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? idNode = null;
                JsonNode? request;
                try
                {
                    request = JsonNode.Parse(line);
                }
                catch (JsonException ex)
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PARSE ERROR: {ex.Message}\n");
                    var parseError = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = null,
                        ["error"] = new JsonObject { ["code"] = -32700, ["message"] = $"Parse error: {ex.Message}" }
                    };
                    Console.WriteLine(parseError.ToJsonString());
                    continue;
                }

                try
                {
                    string? method = request?["method"]?.ToString();
                    idNode = request?["id"]?.DeepClone();

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
                                                    ["description"] = "Tiền tố tên tiến trình ưu tiên (ví dụ: DeltaForceClient, devenv). Không thấy tiến trình thì không dọn RAM."
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
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "list_agent_squads",
                                        ["description"] = "[CẤP XANH] Liệt kê 5 Squad cá nhân hóa của Nhật Hòa và các Agent hiện có trong 02_Agents_Registry / kho kỹ năng.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["includeAgents"] = new JsonObject { ["type"] = "boolean", ["description"] = "Kèm danh sách Agent (mặc định true)" },
                                                ["maxAgents"] = new JsonObject { ["type"] = "integer", ["description"] = "Số Agent tối đa trả về (mặc định 300)" }
                                            }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "dispatch_agent_squad",
                                        ["description"] = "[CẤP XANH] Chọn Squad + Primary Agent + QA Agent cho một nhiệm vụ, ghi log DISPATCHED vào SQLite WAL và trả về SystemInstruction 6 phần.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["taskDescription"] = new JsonObject { ["type"] = "string", ["description"] = "Mô tả nhiệm vụ" },
                                                ["nexusMode"] = new JsonObject
                                                {
                                                    ["type"] = "string",
                                                    ["enum"] = new JsonArray { "Micro", "Sprint", "Full" },
                                                    ["description"] = "Chế độ NEXUS (mặc định Micro)"
                                                },
                                                ["preferredAgentId"] = new JsonObject { ["type"] = "string", ["description"] = "Agent id (slug) hoặc đường dẫn .md tương đối trong 02_Agents_Registry" }
                                            },
                                            ["required"] = new JsonArray { "taskDescription" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "consult_gemini_agent",
                                        ["description"] = "[CẤP XANH] Điều phối Squad + Primary Agent rồi hỏi Gemini với SystemInstruction của agent; lưu ai_response vào SQLite WAL và latest_dispatch.json (Cockpit trên MSI G272 tự hiện câu trả lời). Chưa có khóa / lỗi mạng: vẫn DISPATCHED kèm thông báo chẩn đoán.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["taskDescription"] = new JsonObject { ["type"] = "string", ["description"] = "Câu hỏi / nhiệm vụ" },
                                                ["nexusMode"] = new JsonObject
                                                {
                                                    ["type"] = "string",
                                                    ["enum"] = new JsonArray { "Micro", "Sprint", "Full" },
                                                    ["description"] = "Chế độ NEXUS (mặc định theo nexus_config.json)"
                                                },
                                                ["preferredAgentId"] = new JsonObject { ["type"] = "string", ["description"] = "Agent id (slug) muốn chỉ định" }
                                            },
                                            ["required"] = new JsonArray { "taskDescription" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "run_gemini_qa_review",
                                        ["description"] = "[CẤP VÀNG] QA Agent của squad (persona .md) kiểm định câu trả lời của Primary Agent + bằng chứng bằng Gemini, rồi tự chốt phán quyết PASSED / NEEDS_WORK (3 lần trượt → ESCALATED, ghi qa_report.md). Chưa có khóa / lỗi mạng: không đổi trạng thái.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["dispatchId"] = new JsonObject { ["type"] = "integer", ["description"] = "Dispatch cần kiểm định (bỏ trống = mới nhất)" },
                                                ["additionalEvidence"] = new JsonObject { ["type"] = "string", ["description"] = "Kết quả build/test/đo đạc bổ sung" }
                                            }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "get_nexus_config",
                                        ["description"] = "[CẤP XANH] Cấu hình nexus_config.json đang áp dụng (fingerprint) và hồ sơ SLA dispatch hiện tại (Desktop / LiveCombat).",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject()
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "update_dispatch_verdict",
                                        ["description"] = "[CẤP VÀNG] Chốt kết quả nghiệm thu (NEEDS_WORK | PASSED) của một lượt dispatch vào SQLite WAL. NEEDS_WORK lần thứ 3 → ESCALATED (cầu chì 3 lần thử, ghi qa_report.md). Cockpit nhận qua latest_dispatch.json.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["dispatchId"] = new JsonObject { ["type"] = "integer" },
                                                ["verdict"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "NEEDS_WORK", "PASSED" } },
                                                ["evidenceNotes"] = new JsonObject { ["type"] = "string" }
                                            },
                                            ["required"] = new JsonArray { "dispatchId", "verdict", "evidenceNotes" }
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

                        ToolResult result = toolName switch
                        {
                            "list_agent_squads" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteListAgentSquads(agentRegistry, squadDispatcher, memoryStore, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            "dispatch_agent_squad" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteDispatch(squadDispatcher, toolArgs, latestDispatchPath, runtime.Config.Current.DefaultNexusMode)
                                    : ToolResult.Fail("Blocked."),

                            "consult_gemini_agent" =>
                                GovernanceGate.ApproveExecution(toolName, DpapiSecretVault.Redact(argsJson), ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteConsult(runtime.Consult, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            "run_gemini_qa_review" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Yellow_ReversibleTune, memoryStore)
                                    ? ExecuteQaReview(runtime.Consult, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            "get_nexus_config" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteGetConfig(runtime.Config)
                                    : ToolResult.Fail("Blocked."),

                            "update_dispatch_verdict" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Yellow_ReversibleTune, memoryStore)
                                    ? ExecuteUpdateVerdict(verdictService, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            _ => ToolResult.Ok(toolName switch
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
                            })
                        };

                        var resultObject = new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = result.Text
                                }
                            }
                        };
                        if (result.IsError) resultObject["isError"] = true;

                        var response = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idNode,
                            ["result"] = resultObject
                        };
                        Console.WriteLine(response.ToJsonString());
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex.Message}\n");
                    if (idNode != null)
                    {
                        // Answer the request so MCP clients do not hang waiting for a response.
                        var error = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idNode.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message }
                        };
                        Console.WriteLine(error.ToJsonString());
                    }
                }
            }
        }

        private static readonly JsonSerializerOptions ToolJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        private readonly record struct ToolResult(string Text, bool IsError)
        {
            public static ToolResult Ok(string text) => new(text, false);
            public static ToolResult Fail(string text) => new(text, true);
        }

        private static string? ReadString(JsonNode? args, string name)
        {
            JsonNode? node = args?[name];
            if (node is JsonValue value && value.TryGetValue(out string? text)) return text;
            return node?.ToString();
        }

        private static bool TryReadLong(JsonNode? args, string name, out long number)
        {
            number = 0;
            JsonNode? node = args?[name];
            if (node is not JsonValue value) return false;
            if (value.TryGetValue(out long l)) { number = l; return true; }
            if (value.TryGetValue(out int i)) { number = i; return true; }
            if (value.TryGetValue(out double d) && d == Math.Floor(d)) { number = (long)d; return true; }
            return value.TryGetValue(out string? s) && long.TryParse(s, out number);
        }

        private static ToolResult ExecuteListAgentSquads(AgentRegistry registry, SquadDispatcher dispatcher, SqliteMemoryStore store, JsonNode? args)
        {
            bool includeAgents = args?["includeAgents"] is JsonValue flag && flag.TryGetValue(out bool b) ? b : true;
            int maxAgents = TryReadLong(args, "maxAgents", out long requested) ? (int)Math.Clamp(requested, 0, 1000) : 300;

            var payload = new
            {
                RegistryRoots = registry.Roots,
                TotalAgents = registry.Count,
                DispatchLogCount = store.CountDispatchLogs(),
                Squads = SquadDispatcher.Squads.Select(s => new
                {
                    s.SquadId,
                    s.Name,
                    PrimaryAgent = dispatcher.DescribeAgent(s.Primary.AgentId),
                    QaAgent = dispatcher.DescribeAgent(s.QaAgentId),
                    s.Keywords
                }),
                Agents = includeAgents
                    ? registry.GetAll().Take(maxAgents).Select(a => new { a.Id, a.Name, a.Division, a.Summary })
                    : null
            };
            return ToolResult.Ok(JsonSerializer.Serialize(payload, ToolJson));
        }

        /// <summary>TASK-07 §3.4: dispatch + Gemini (same AgentConsultService as Cockpit /ask), answered when done.</summary>
        private static ToolResult ExecuteConsult(AgentConsultService consult, JsonNode? args)
        {
            string task = ReadString(args, "taskDescription") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(task)) return ToolResult.Fail("taskDescription is required.");

            ConsultStart start = consult.Start(task, ReadString(args, "nexusMode"), ReadString(args, "preferredAgentId"));
            if (start.Package == null) return ToolResult.Fail(start.Error ?? "Dispatch failed.");

            DispatchPackage package = start.Package;
            ConsultOutcome outcome = consult.CompleteAsync(package, task).GetAwaiter().GetResult();
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                dispatchId = package.DispatchId,
                squadId = package.SquadId,
                squadName = package.SquadName,
                primaryAgent = package.PrimaryAgent,
                qaAgent = package.QaAgent,
                nexusMode = package.NexusMode,
                status = outcome.Row?.Status ?? "DISPATCHED",
                dispatchElapsedMs = package.ElapsedMs,
                aiOk = outcome.AiOk,
                aiResponse = outcome.AiResponse,
                aiModel = outcome.AiModel,
                aiLatencyMs = outcome.AiLatencyMs,
                keySource = outcome.KeySource,
                latestDispatchFileError = outcome.FileError ?? start.FileError
            }, ToolJson));
        }

        /// <summary>TASK-08 §3.3.5: same QA engine as Cockpit /qa (StartQa writes "QA REVIEWING" for the Cockpit first).</summary>
        private static ToolResult ExecuteQaReview(AgentConsultService consult, JsonNode? args)
        {
            long? dispatchId = null;
            if (args?["dispatchId"] != null)
            {
                if (!TryReadLong(args, "dispatchId", out long id) || id <= 0) return ToolResult.Fail("dispatchId must be a positive integer.");
                dispatchId = id;
            }
            string? evidence = ReadString(args, "additionalEvidence");

            QaStart start = consult.StartQa(dispatchId, evidence);
            if (start.Row == null) return ToolResult.Fail(start.Error ?? "QA start failed.");

            QaOutcome outcome = consult.RunQaReviewAsync(start.DispatchId, evidence).GetAwaiter().GetResult();
            if (outcome.Error != null && !outcome.Reviewed && outcome.QaResponse.Length == 0) return ToolResult.Fail(outcome.Error);
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                dispatchId = outcome.DispatchId,
                qaAgent = outcome.QaAgent,
                reviewed = outcome.Reviewed,
                verdict = outcome.Verdict,
                status = outcome.Status,
                retryCount = outcome.RetryCount,
                escalatedNow = outcome.EscalatedNow,
                qaResponse = outcome.QaResponse,
                qaLatencyMs = outcome.QaLatencyMs,
                keySource = outcome.KeySource,
                error = outcome.Error
            }, ToolJson));
        }

        private static ToolResult ExecuteGetConfig(NexusConfigService config)
        {
            DispatchSlaProfile sla = config.DetectSla();
            NexusConfigLoadResult load = config.LastResult;
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                configPath = config.ConfigPath,
                fingerprint = config.Fingerprint,
                loadOk = load.Ok,
                loadError = load.Error,
                warnings = load.Warnings,
                config = config.Current,
                sla
            }, ToolJson));
        }

        private static ToolResult ExecuteDispatch(SquadDispatcher dispatcher, JsonNode? args, string latestDispatchPath, string defaultMode)
        {
            string? task = ReadString(args, "taskDescription");
            // TASK-06 ADR-20: no nexusMode in the call -> nexus_config.json "defaultNexusMode".
            DispatchOutcome outcome = dispatcher.Dispatch(task ?? string.Empty, ReadString(args, "nexusMode") ?? defaultMode, ReadString(args, "preferredAgentId"));
            if (outcome.Package == null) return ToolResult.Fail(outcome.Error ?? "Dispatch failed.");

            // ADR-14: the Cockpit watches this file. A write failure does not undo the dispatch (row is in SQLite).
            LatestDispatchFile.Write(latestDispatchPath, LatestDispatchFile.FromDispatch(outcome.Package, task ?? string.Empty, "McpTools"));
            return ToolResult.Ok(JsonSerializer.Serialize(outcome.Package, ToolJson));
        }

        private static ToolResult ExecuteUpdateVerdict(DispatchVerdictService verdicts, JsonNode? args)
        {
            if (!TryReadLong(args, "dispatchId", out long dispatchId) || dispatchId <= 0)
                return ToolResult.Fail("dispatchId must be a positive integer.");

            string? verdict = DispatchVerdictService.NormalizeVerdict(ReadString(args, "verdict"));
            if (verdict == null)
                return ToolResult.Fail("verdict must be NEEDS_WORK or PASSED.");

            string evidence = ReadString(args, "evidenceNotes") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(evidence))
                return ToolResult.Fail("evidenceNotes is required (no verdict without evidence).");

            VerdictOutcome outcome = verdicts.Apply(dispatchId, verdict, evidence);
            if (!outcome.Ok || outcome.Dispatch == null)
                return ToolResult.Fail(outcome.Error ?? $"Dispatch #{dispatchId} not found.");

            RecentDispatch row = outcome.Dispatch;
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                dispatchId = row.Id,
                status = row.Status,
                retryCount = row.RetryCount,
                escalated = row.Status == "ESCALATED",
                escalatedNow = outcome.EscalatedNow,
                updatedAtUtc = row.UpdatedAtUtc,
                saved = true,
                latestDispatchFileError = outcome.FileError
            }, ToolJson));
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
