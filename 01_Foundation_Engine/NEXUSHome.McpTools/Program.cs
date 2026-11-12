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
            // TASK-12c: "thợ nén" mode for the HostService (visionWorker = process): one image job on stdin, header + JPEG on
            // stdout, then exit, before any MCP, SQLite or registry setup, so the process stays small and short-lived.
            if (args.Length > 0 && args[0].Equals(VisionWorkerProtocol.Argument, StringComparison.OrdinalIgnoreCase))
            {
                if (!OperatingSystem.IsWindows())
                {
                    Environment.ExitCode = 1;
                    return;
                }
                using Stream stdin = Console.OpenStandardInput();
                using Stream stdout = Console.OpenStandardOutput();
                Environment.ExitCode = VisionWorkerServer.Run(stdin, stdout);
                return;
            }

            // TASK-18 ADR-71: one SAPI job ("recognize" PCM / "synth" text) for the HostService /ptt, then exit.
            if (args.Length > 0 && args[0].Equals(SttWorkerProtocol.Argument, StringComparison.OrdinalIgnoreCase))
            {
                if (!OperatingSystem.IsWindows())
                {
                    Environment.ExitCode = 1;
                    return;
                }
                using Stream stdin = Console.OpenStandardInput();
                using Stream stdout = Console.OpenStandardOutput();
                Environment.ExitCode = SttWorkerServer.Run(args.Length > 1 ? args[1] : "recognize", stdin, stdout);
                return;
            }

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
            _ = runtime.Grounding.Search("warmup", 1, 120);
            _ = runtime.ResponseCache.GetStats();
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
                                        ["description"] = "[CẤP XANH] Điều phối Squad + Primary Agent rồi hỏi Gemini với SystemInstruction của agent; lưu ai_response vào SQLite WAL và latest_dispatch.json (Cockpit trên MSI G272 tự hiện câu trả lời). Hỗ trợ ADR-44 cache hit (< 2 ms). Chưa có khóa / lỗi mạng: vẫn DISPATCHED kèm thông báo chẩn đoán.",
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
                                                ["preferredAgentId"] = new JsonObject { ["type"] = "string", ["description"] = "Agent id (slug) muốn chỉ định" },
                                                ["fresh"] = new JsonObject { ["type"] = "boolean", ["description"] = "Bỏ qua cache (mặc định false)" }
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
                                        ["name"] = "retry_gemini_dispatch",
                                        ["description"] = "[CẤP VÀNG] Vòng lặp tự sửa (ADR-36): Primary Agent đọc nhiệm vụ gốc + câu trả lời trước + nhận xét QA (qa_response / evidence_notes) + gợi ý để viết lại lời giải; status về DISPATCHED (retry_count giữ nguyên), autoQa=true thì QA chấm lại ngay. Bản ghi ESCALATED bị cầu chì 3 lần thử chặn (không gọi Gemini) trừ khi forceEscalated=true.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["dispatchId"] = new JsonObject { ["type"] = "integer", ["description"] = "Dispatch cần sửa (bỏ trống = mới nhất)" },
                                                ["userHint"] = new JsonObject { ["type"] = "string", ["description"] = "Gợi ý bổ sung cho Primary Agent" },
                                                ["autoQa"] = new JsonObject { ["type"] = "boolean", ["description"] = "Gọi QA Agent chấm lại ngay sau khi sửa (mặc định false)" },
                                                ["forceEscalated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Vượt chốt chặn ESCALATED (mặc định false)" }
                                            }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "followup_gemini_dispatch",
                                        ["description"] = "[CẤP XANH; CẤP VÀNG khi autoQa] Hội thoại nhiều lượt (ADR-37): gửi câu hỏi tiếp theo kèm tối đa maxConversationTurns cặp lượt gần nhất của cùng dispatch; câu trả lời thành lượt mới (turn_count + 1, turn_history_json), Cockpit tự cập nhật. Không đổi status trừ khi autoQa=true.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["followUpText"] = new JsonObject { ["type"] = "string", ["description"] = "Câu hỏi / yêu cầu tiếp theo" },
                                                ["dispatchId"] = new JsonObject { ["type"] = "integer", ["description"] = "Dispatch đang hội thoại (bỏ trống = mới nhất)" },
                                                ["autoQa"] = new JsonObject { ["type"] = "boolean", ["description"] = "Gọi QA Agent chấm câu trả lời mới (mặc định false)" }
                                            },
                                            ["required"] = new JsonArray { "followUpText" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "get_gemini_usage_stats",
                                        ["description"] = "[CẤP XANH] Thống kê token sử dụng trong phiên và trong ngày (UTC từ SQLite WAL), hạn mức RPM/TPM/Daily hiện tại, số lần retry HTTP và cấu hình backoff từ nexus_config.json.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject()
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "get_gemini_cache_stats",
                                        ["description"] = "[CẤP XANH] Thống kê bộ đệm phản hồi xác định trên SQLite WAL (ADR-44 gemini_response_cache): tổng số mục, số mục còn hạn TTL, tổng lượt trúng cache (hit_count) và tổng token tiết kiệm được (saved_total_tokens).",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject()
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "search_local_vault",
                                        ["description"] = "[CẤP XANH] Trích xuất tối đa Top-3 phân đoạn liên quan nhất (<= 1 500 ký tự) từ 03_Local_Data_Vault/ bằng bộ lọc từ khóa xác định trên luồng nhẹ (ADR-45 VaultGroundingEngine, < 5 ms).",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Từ khóa / câu hỏi cần tìm trong 03_Local_Data_Vault/" },
                                                ["maxSections"] = new JsonObject { ["type"] = "integer", ["description"] = "Số phân đoạn tối đa (mặc định 3, tối đa 3)" },
                                                ["maxChars"] = new JsonObject { ["type"] = "integer", ["description"] = "Tổng số ký tự tối đa (mặc định 1500, tối đa 1500)" }
                                            },
                                            ["required"] = new JsonArray { "query" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "run_local_utility",
                                        ["description"] = "[CẤP XANH] Tiện ích nội bộ 0 token (ADR-59): calc (biểu thức, Dec/Hex/Bin, bitwise), timer (đếm ngược; sống cùng tiến trình MCP này), note (ghi/tìm quick_notes.md, chặn lộ khóa). Ghi latest_dispatch.json nguyên tử để Cockpit cập nhật < 300 ms.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["utility"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "calc", "timer", "note" } },
                                                ["commandText"] = new JsonObject { ["type"] = "string", ["description"] = "calc: biểu thức (sqrt(144) + 2^8); timer: \"250ms nhãn\" | list | cancel <id|all>; note: nội dung | list [N] | find <từ khóa>" }
                                            },
                                            ["required"] = new JsonArray { "utility", "commandText" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "manage_hud_widgets",
                                        ["description"] = "[CẤP XANH] TASK-13 Right MFD: extract (thẻ code/checklist/utility của phản hồi hiện tại), copy (khối code n → LastCopiedText), pin (#n / n / văn bản → mfd_pins.json, tối đa 5, FIFO, chặn lộ khóa), unpin (id|all), list_pins. Cockpit đang chạy cập nhật Right MFD < 300 ms.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "extract", "copy", "pin", "unpin", "list_pins" } },
                                                ["targetOrText"] = new JsonObject { ["type"] = "string", ["description"] = "copy: số khối code (mặc định 1); pin: #n hoặc n = thẻ thứ n, văn bản = thẻ ghi nhớ, trống = thẻ đầu; unpin: pinId hoặc all" }
                                            },
                                            ["required"] = new JsonArray { "action" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "analyze_visual_input",
                                        ["description"] = "[CẤP XANH] TASK-12 ADR-61 Multimodal Vision: clipboard (ảnh Win+Shift+S) | file (đường dẫn ảnh) | snap (chụp MSI G272, chặn bởi Combat-Mode Shield khi game/anti-cheat chạy và Privacy Window Guard khi có cửa sổ nhạy cảm). Nén JPEG <= 250 KB, cạnh <= 1280 px, gửi Gemini inline_data; Vision SHA-256 cache trả lại 0 token. Ghi latest_dispatch.json (hasVisionAttachment, visionImageBytes, visionImageSha256) để Cockpit cập nhật < 300 ms.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["source"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "clipboard", "snap", "file" } },
                                                ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "Câu hỏi về ảnh (trống = mô tả và chỉ lỗi)" },
                                                ["filePath"] = new JsonObject { ["type"] = "string", ["description"] = "source=file: đường dẫn ảnh (PNG/JPEG/BMP)" },
                                                ["nexusMode"] = new JsonObject { ["type"] = "string", ["description"] = "Micro | Sprint | Full (mặc định theo nexus_config.json)" },
                                                ["fresh"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = bỏ qua Vision cache" }
                                            },
                                            ["required"] = new JsonArray { "source" }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "get_active_window_context",
                                        ["description"] = "[CẤP XANH] TASK-15 ADR-62: ngữ cảnh cửa sổ đang foreground (tên tiến trình, tiêu đề <= 200 ký tự, tùy chọn chữ trong clipboard <= 1 000 ký tự, khối <= 1 200 ký tự) sau 2 khiên: Anti-Cheat Safe Guard (tiến trình chống gian lận / game bảo vệ đang chạy → BLOCKED_ANTICHEAT, không đọc tiêu đề) và Sensitive Window Denylist (trình quản lý mật khẩu, ẩn danh, .env, ngân hàng, OTP… → BLOCKED_SENSITIVE, tiêu đề bị bỏ). Chỉ dùng quyền PROCESS_QUERY_LIMITED_INFORMATION; 0 token.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["includeClipboard"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = kèm chữ trong clipboard (mặc định false)" }
                                            }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "get_network_radar",
                                        ["description"] = "[CẤP XANH] TASK-17 ADR-67: radar mạng của PC Nhật Hòa, chỉ chạy khi được gọi (0 token, không vòng lặp nền): card mạng vật lý có gateway 192.168.1.1 (bỏ qua Radmin VPN 26.x và card ảo), ping ICMP song song tối đa 200 ms/đích tới gateway, NITRO5-WORKER (192.168.1.22, dự phòng Wi-Fi 192.168.1.192; không trả lời = OFFLINE (AWAY)) và TV 192.168.1.97, số cổng TCP LISTENING / ESTABLISHED / UDP và các cổng nghe tiêu biểu. portFilter = kiểm một cổng (FREE / LISTENING / ESTABLISHED / UDP).",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["portFilter"] = new JsonObject { ["type"] = "integer", ["description"] = "Cổng cần kiểm (1–65535), tùy chọn" },
                                                ["ping"] = new JsonObject { ["type"] = "boolean", ["description"] = "false = bỏ phần ping (chỉ card mạng + cổng); mặc định true" }
                                            }
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "notify_shift_complete",
                                        ["description"] = "[CẤP XANH] TASK-16b ADR-66: báo Cockpit là một ca làm việc đã xong (ví dụ Claude Code chạy xong Verify). Ghi config/shift_state.json (UTF-8 không BOM) và bật chỉ báo trên thanh HUD 40 px của Cockpit đang chạy (kèm âm báo nếu shiftCompleteSound = true). Tóm tắt giống khóa bí mật bị thay.",
                                        ["inputSchema"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["agent"] = new JsonObject { ["type"] = "string", ["description"] = "Ai xong ca (mặc định Claude Code)" },
                                                ["gate"] = new JsonObject { ["type"] = "string", ["description"] = "Gate / task, ví dụ Gate 16b" },
                                                ["status"] = new JsonObject { ["type"] = "string", ["description"] = "ALL PASS | FAIL | DONE ..." },
                                                ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "Một câu tóm tắt (tối đa 240 ký tự)" }
                                            },
                                            ["required"] = new JsonArray { "gate", "status" }
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

                            "retry_gemini_dispatch" =>
                                GovernanceGate.ApproveExecution(toolName, DpapiSecretVault.Redact(argsJson), ActionRiskLevel.Yellow_ReversibleTune, memoryStore)
                                    ? ExecuteRefine(runtime.Consult, toolArgs, retry: true)
                                    : ToolResult.Fail("Blocked."),

                            "followup_gemini_dispatch" =>
                                GovernanceGate.ApproveExecution(toolName, DpapiSecretVault.Redact(argsJson),
                                        ReadBool(toolArgs, "autoQa") ? ActionRiskLevel.Yellow_ReversibleTune : ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteRefine(runtime.Consult, toolArgs, retry: false)
                                    : ToolResult.Fail("Blocked."),

                            "get_gemini_usage_stats" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteGetGeminiUsageStats(runtime)
                                    : ToolResult.Fail("Blocked."),

                            "get_gemini_cache_stats" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteGetGeminiCacheStats(runtime)
                                    : ToolResult.Fail("Blocked."),

                            "search_local_vault" =>
                                GovernanceGate.ApproveExecution(toolName, DpapiSecretVault.Redact(argsJson), ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteSearchLocalVault(runtime, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            // TASK-14 ADR-59: the audit row gets the redacted arguments; a key in a note is refused by the Secret Guard.
                            "run_local_utility" =>
                                GovernanceGate.ApproveExecution(toolName, RedactSecrets(argsJson), ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteLocalUtility(runtime, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            // TASK-13: Right MFD widgets (Green); audit arguments redacted like run_local_utility.
                            "manage_hud_widgets" =>
                                GovernanceGate.ApproveExecution(toolName, RedactSecrets(argsJson), ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteHudWidgets(runtime, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            // TASK-12 ADR-61: Green (reads the clipboard / a file / the screen behind the two shields); audit args redacted.
                            "analyze_visual_input" =>
                                GovernanceGate.ApproveExecution(toolName, RedactSecrets(argsJson), ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteAnalyzeVisualInput(runtime, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            // TASK-15 ADR-62: Green (query-limited process name + title behind both shields; no title kept when blocked).
                            "get_active_window_context" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteActiveWindowContext(memoryStore, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            // TASK-17 ADR-67: Green (reads the adapter / port tables, ICMP echoes with a 200 ms cut; writes nothing).
                            "get_network_radar" =>
                                GovernanceGate.ApproveExecution(toolName, argsJson, ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteNetworkRadar(runtime, toolArgs)
                                    : ToolResult.Fail("Blocked."),

                            // TASK-16b ADR-66: Green (writes one small state file in config/ and talks to the local pipe).
                            "notify_shift_complete" =>
                                GovernanceGate.ApproveExecution(toolName, RedactSecrets(argsJson), ActionRiskLevel.Green_SafeRead, memoryStore)
                                    ? ExecuteNotifyShiftComplete(runtime, toolArgs)
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

        /// <summary>TASK-07 §3.4 / TASK-10 / TASK-11: dispatch + Gemini (same AgentConsultService as Cockpit /ask), with ADR-44 cache support.</summary>
        private static ToolResult ExecuteConsult(AgentConsultService consult, JsonNode? args)
        {
            string task = ReadString(args, "taskDescription") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(task)) return ToolResult.Fail("taskDescription is required.");

            bool fresh = ReadBool(args, "fresh");
            string? nexusMode = ReadString(args, "nexusMode");
            string? preferredAgentId = ReadString(args, "preferredAgentId");

            if (!fresh && consult.TryExecuteCachedConsult(task, nexusMode, preferredAgentId, "ask",
                    string.Empty, Array.Empty<string>(), out DispatchPackage? cachedPkg, out ConsultOutcome? cachedOutcome,
                    out _, out _) &&
                cachedPkg != null && cachedOutcome != null)
            {
                return ToolResult.Ok(JsonSerializer.Serialize(new
                {
                    dispatchId = cachedPkg.DispatchId,
                    squadId = cachedPkg.SquadId,
                    squadName = cachedPkg.SquadName,
                    primaryAgent = cachedPkg.PrimaryAgent,
                    qaAgent = cachedPkg.QaAgent,
                    nexusMode = cachedPkg.NexusMode,
                    status = cachedOutcome.Row?.Status ?? "DISPATCHED",
                    dispatchElapsedMs = cachedPkg.ElapsedMs,
                    aiOk = cachedOutcome.AiOk,
                    aiResponse = cachedOutcome.AiResponse,
                    aiModel = cachedOutcome.AiModel,
                    aiLatencyMs = cachedOutcome.AiLatencyMs,
                    promptTokens = cachedOutcome.PromptTokens,
                    completionTokens = cachedOutcome.CompletionTokens,
                    totalTokens = cachedOutcome.TotalTokens,
                    httpRetryCount = cachedOutcome.HttpRetryCount,
                    cacheHit = true,
                    cache_hit = true,
                    promptTokensSaved = cachedOutcome.PromptTokensSaved,
                    completionTokensSaved = cachedOutcome.CompletionTokensSaved,
                    savedTotalTokens = cachedOutcome.SavedTotalTokens,
                    saved_total_tokens = cachedOutcome.SavedTotalTokens,
                    keySource = cachedOutcome.KeySource,
                    latestDispatchFileError = cachedOutcome.FileError
                }, ToolJson));
            }

            ConsultStart start = consult.Start(task, nexusMode, preferredAgentId);
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
                promptTokens = outcome.PromptTokens,
                completionTokens = outcome.CompletionTokens,
                totalTokens = outcome.TotalTokens,
                httpRetryCount = outcome.HttpRetryCount,
                cacheHit = outcome.CacheHit,
                cache_hit = outcome.CacheHit,
                savedTotalTokens = outcome.SavedTotalTokens,
                saved_total_tokens = outcome.SavedTotalTokens,
                keySource = outcome.KeySource,
                latestDispatchFileError = outcome.FileError ?? start.FileError
            }, ToolJson));
        }

        /// <summary>TASK-08 §3.3.5 / TASK-10: same QA engine as Cockpit /qa (StartQa writes "QA REVIEWING" for the Cockpit first).</summary>
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
                promptTokens = outcome.PromptTokens,
                completionTokens = outcome.CompletionTokens,
                totalTokens = outcome.TotalTokens,
                httpRetryCount = outcome.HttpRetryCount,
                cacheHit = outcome.CacheHit,
                cache_hit = outcome.CacheHit,
                savedTotalTokens = outcome.SavedTotalTokens,
                saved_total_tokens = outcome.SavedTotalTokens,
                keySource = outcome.KeySource,
                error = outcome.Error
            }, ToolJson));
        }

        private static bool ReadBool(JsonNode? args, string name) =>
            args?[name] is JsonValue value && (value.TryGetValue(out bool b) ? b : value.TryGetValue(out string? s) && bool.TryParse(s, out bool parsed) && parsed);

        /// <summary>
        /// TASK-09 §3.2.4 / §3.3.3 / TASK-10: retry_gemini_dispatch and followup_gemini_dispatch use the same AgentConsultService flow as
        /// Cockpit /retry and /f (Start writes "RETRYING" / "FOLLOW-UP" to latest_dispatch.json first), answered when done.
        /// </summary>
        private static ToolResult ExecuteRefine(AgentConsultService consult, JsonNode? args, bool retry)
        {
            long? dispatchId = null;
            if (args?["dispatchId"] != null)
            {
                if (!TryReadLong(args, "dispatchId", out long id) || id <= 0) return ToolResult.Fail("dispatchId must be a positive integer.");
                dispatchId = id;
            }
            bool autoQa = ReadBool(args, "autoQa");
            bool force = retry && ReadBool(args, "forceEscalated");
            string text = (retry ? ReadString(args, "userHint") : ReadString(args, "followUpText")) ?? string.Empty;
            if (!retry && string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("followUpText is required.");

            RefineStart start = retry ? consult.StartRetry(dispatchId, text, force) : consult.StartFollowUp(dispatchId, text);
            if (start.Row == null) return ToolResult.Fail(start.Error ?? "Start failed.");
            if (start.Blocked)
            {
                return ToolResult.Fail(JsonSerializer.Serialize(new
                {
                    dispatchId = start.DispatchId,
                    blocked = true,
                    message = start.Error,
                    status = start.Row.Status,
                    retryCount = start.Row.RetryCount,
                    turnCount = start.Row.TurnCount
                }, ToolJson));
            }

            RefineOutcome outcome = retry
                ? consult.CompleteRetryAsync(start.DispatchId, text, force).GetAwaiter().GetResult()
                : consult.CompleteFollowUpAsync(start.DispatchId, text).GetAwaiter().GetResult();
            QaOutcome? qa = autoQa && outcome.AiOk ? consult.RunQaReviewAsync(start.DispatchId, null).GetAwaiter().GetResult() : null;
            RecentDispatch? row = consult.GetDispatch(start.DispatchId) ?? outcome.Row;
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                dispatchId = start.DispatchId,
                kind = outcome.Kind,
                blocked = outcome.Blocked,
                status = row?.Status,
                retryCount = row?.RetryCount,
                turnCount = row?.TurnCount,
                aiOk = outcome.AiOk,
                aiResponse = row?.AiResponse,
                aiModel = row?.AiModel,
                aiLatencyMs = outcome.AiLatencyMs,
                promptTokens = row?.PromptTokens ?? outcome.PromptTokens,
                completionTokens = row?.CompletionTokens ?? outcome.CompletionTokens,
                totalTokens = row?.TotalTokens ?? outcome.TotalTokens,
                httpRetryCount = outcome.HttpRetryCount,
                contentsSent = outcome.ContentsSent,
                keySource = outcome.KeySource,
                qaVerdict = qa?.Verdict,
                qaResponse = row?.QaResponse,
                escalatedNow = qa?.EscalatedNow,
                turnHistory = row == null ? null : TurnHistory.Parse(row.TurnHistoryJson).Select(e => new { e.Turn, e.Role, chars = e.Text.Length }),
                error = outcome.Error ?? qa?.Error
            }, ToolJson));
        }

        private static ToolResult ExecuteGetGeminiUsageStats(NexusRuntime runtime)
        {
            runtime.Config.Reload();
            GeminiUsageStats stats = runtime.QuotaGuard.GetStats(runtime.Config.Current, runtime.Store);
            LatestDispatchSnapshot? latest = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                stats,
                latestDispatch = latest == null ? null : new
                {
                    latest.DispatchId,
                    latest.Status,
                    latest.TurnCount,
                    latest.PromptTokens,
                    latest.CompletionTokens,
                    latest.TotalTokens,
                    latest.SessionTotalTokens,
                    latest.DailyTotalTokens,
                    latest.CacheHit,
                    latest.SavedTotalTokens,
                    latest.GroundingSources
                },
                config = new
                {
                    runtime.Config.Current.GeminiModel,
                    runtime.Config.Current.MaxRequestsPerMinute,
                    runtime.Config.Current.MaxTokensPerMinute,
                    runtime.Config.Current.MaxDailyTokens,
                    runtime.Config.Current.HttpMaxRetries,
                    runtime.Config.Current.HttpRetryBaseDelayMs
                }
            }, ToolJson));
        }

        /// <summary>TASK-11 ADR-44: MCP get_gemini_cache_stats tool.</summary>
        private static ToolResult ExecuteGetGeminiCacheStats(NexusRuntime runtime)
        {
            var sw = Stopwatch.StartNew();
            GeminiCacheStats stats = runtime.ResponseCache.GetStats();
            sw.Stop();
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                stats,
                totalEntries = stats.TotalEntries,
                total_entries = stats.TotalEntries,
                activeEntries = stats.ActiveEntries,
                active_entries = stats.ActiveEntries,
                totalHits = stats.TotalHits,
                hit_count = stats.TotalHits,
                promptTokensSaved = stats.PromptTokensSaved,
                prompt_tokens_saved = stats.PromptTokensSaved,
                completionTokensSaved = stats.CompletionTokensSaved,
                completion_tokens_saved = stats.CompletionTokensSaved,
                savedTotalTokens = stats.SavedTotalTokens,
                saved_total_tokens = stats.SavedTotalTokens,
                ttlSeconds = stats.DefaultTtlSeconds,
                ttl_seconds = stats.DefaultTtlSeconds
            }, ToolJson));
        }

        /// <summary>TASK-11 ADR-45: MCP search_local_vault tool (&lt; 5 ms).</summary>
        private static ToolResult ExecuteSearchLocalVault(NexusRuntime runtime, JsonNode? args)
        {
            string query = ReadString(args, "query") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query is required.");
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(query))
                return ToolResult.Fail("query contains what looks like an API key: refused.");

            int maxSections = TryReadLong(args, "maxSections", out long ms)
                ? (int)Math.Clamp(ms, 1, VaultGroundingEngine.MaxTopSections)
                : VaultGroundingEngine.MaxTopSections;
            int maxChars = TryReadLong(args, "maxChars", out long mc)
                ? (int)Math.Clamp(mc, 120, VaultGroundingEngine.MaxTotalAttachedChars)
                : VaultGroundingEngine.MaxTotalAttachedChars;

            VaultGroundingResult res = runtime.Grounding.Search(query, maxSections, maxChars);
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                query = res.Query,
                sectionCount = res.Sections.Count,
                sections = res.Sections,
                groundingSources = res.GroundingSources,
                grounding_sources = res.GroundingSources,
                totalChars = res.TotalChars,
                total_chars = res.TotalChars,
                groundingFingerprint = res.GroundingFingerprint,
                grounding_fingerprint = res.GroundingFingerprint,
                groundedContextBlock = res.GroundedContextBlock,
                elapsedMs = res.ElapsedMs,
                elapsed_ms = res.ElapsedMs
            }, ToolJson));
        }

        /// <summary>Audit-safe copy of the MCP arguments: Google keys masked, other secret shapes replaced.</summary>
        private static string RedactSecrets(string json) =>
            LocalNoteService.SecretPattern.Replace(DpapiSecretVault.Redact(json), "[REDACTED]");

        /// <summary>
        /// TASK-14 ADR-59: MCP run_local_utility (Green). Same <see cref="LocalUtilityService"/> as the Cockpit; writes
        /// latest_dispatch.json (source McpTools) so a running Cockpit shows the result through its watcher.
        /// </summary>
        private static ToolResult ExecuteLocalUtility(NexusRuntime runtime, JsonNode? args)
        {
            if (!LocalUtilityService.TryNormalizeUtility(ReadString(args, "utility"), out string utility))
                return ToolResult.Fail("utility must be calc, timer or note.");
            string commandText = ReadString(args, "commandText") ?? string.Empty;

            LocalUtilityResult r = runtime.LocalUtilities.Execute(utility, commandText);
            string json = JsonSerializer.Serialize(new
            {
                utility = r.Utility,
                action = r.Action,
                ok = r.Ok,
                blocked = r.Blocked,
                status = r.Status,
                message = r.Message,
                aiModel = r.AiModel,
                ai_model = r.AiModel,
                promptTokens = r.PromptTokens,
                completionTokens = r.CompletionTokens,
                totalTokens = r.TotalTokens,
                computeMs = r.ComputeMs,
                elapsedMs = r.ElapsedMs,
                latestDispatchUpdatedAtUtc = r.Snapshot?.UpdatedAtUtc,
                latestDispatchFileError = r.FileError,
                data = r.Data
            }, ToolJson);
            return r.Ok ? ToolResult.Ok(json) : ToolResult.Fail(json);
        }

        /// <summary>
        /// TASK-13 MCP manage_hud_widgets. Cards come from latest_dispatch.json (current response); pins are written to
        /// config/mfd_pins.json (atomic, mutex shared with HostService), which the running Cockpit watches.
        /// </summary>
        private static ToolResult ExecuteHudWidgets(NexusRuntime runtime, JsonNode? args)
        {
            string action = (ReadString(args, "action") ?? string.Empty).Trim().ToLowerInvariant();
            string target = (ReadString(args, "targetOrText") ?? string.Empty).Trim();
            HudWidgetService widgets = runtime.HudWidgets;
            MfdPinManager pins = runtime.Pins;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            object payload;
            bool ok;
            switch (action)
            {
                case "extract":
                {
                    IReadOnlyList<HudWidgetCard> cards = widgets.Cards;
                    ok = true;
                    payload = new
                    {
                        action,
                        source = widgets.SourceLabel,
                        indicator = WidgetCardExtractor.Indicator(cards),
                        cards = cards.Select(c => new { c.Index, c.Type, c.Title, c.Language, c.LineCount, c.PreviewText, contentChars = c.Content.Length })
                    };
                    break;
                }
                case "copy":
                {
                    int? index = null;
                    if (target.Length > 0)
                    {
                        if (!int.TryParse(target.TrimStart('#'), out int n)) return ToolResult.Fail("copy: targetOrText must be a code block number (1, 2, ...).");
                        index = n;
                    }
                    (string? text, string label) = widgets.CopyTarget(index);
                    if (text == null) return ToolResult.Fail($"copy: {label}");
                    ClipboardCopyResult copy = ClipboardService.Copy(text);
                    ok = true;
                    payload = new { action, label, text, chars = text.Length, systemClipboard = copy.SystemClipboard, note = copy.SystemClipboardError };
                    break;
                }
                case "pin":
                {
                    MfdPinResult result;
                    string digits = target.TrimStart('#');
                    if (target.Length == 0 || (digits.Length > 0 && digits.All(char.IsDigit)))
                    {
                        (HudWidgetCard? card, string? error) = widgets.PinTarget(target.Length == 0 ? null : int.Parse(digits));
                        if (card == null) return ToolResult.Fail($"pin: {error}");
                        result = pins.Pin(card.Type, card.Title, card.Language, card.Content, "McpTools");
                    }
                    else
                    {
                        result = pins.Pin("note", target, null, target, "McpTools");
                    }
                    ok = result.Ok;
                    payload = new { action, result.Ok, result.Blocked, result.Message, result.Evicted, pins = result.Pins, maxPins = pins.MaxPins };
                    break;
                }
                case "unpin":
                {
                    if (target.Length == 0) return ToolResult.Fail("unpin: targetOrText must be a pinId or all.");
                    MfdPinResult result = pins.Unpin(target);
                    ok = result.Ok;
                    payload = new { action, result.Ok, result.Message, result.Removed, pins = result.Pins, maxPins = pins.MaxPins };
                    break;
                }
                case "list_pins":
                {
                    IReadOnlyList<MfdPin> list = pins.List();
                    ok = true;
                    payload = new { action, message = MfdPinManager.Describe(list, pins.MaxPins), pins = list, maxPins = pins.MaxPins };
                    break;
                }
                default:
                    return ToolResult.Fail("action must be extract, copy, pin, unpin or list_pins.");
            }
            string json = JsonSerializer.Serialize(new { result = payload, elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3), pinsPath = MfdPinManager.FileName }, ToolJson);
            return ok ? ToolResult.Ok(json) : ToolResult.Fail(json);
        }

        /// <summary>
        /// TASK-12 ADR-61 MCP analyze_visual_input: same <see cref="VisionConsultService"/> as Cockpit /img and /snap. A shield
        /// block is a normal answer (blocked = true, nothing captured); the call waits for the background Gemini part.
        /// </summary>
        private static ToolResult ExecuteAnalyzeVisualInput(NexusRuntime runtime, JsonNode? args)
        {
            string source = (ReadString(args, "source") ?? string.Empty).Trim().ToLowerInvariant();
            if (source is not ("clipboard" or "snap" or "file")) return ToolResult.Fail("source must be clipboard | snap | file.");
            string? prompt = ReadString(args, "prompt");
            string? filePath = ReadString(args, "filePath");
            if (source == "file" && string.IsNullOrWhiteSpace(filePath)) return ToolResult.Fail("filePath is required when source = file.");
            VisionResult vision = runtime.Vision.Execute(source, prompt, filePath, ReadString(args, "nexusMode"), null, ReadBool(args, "fresh"));
            ConsultOutcome? outcome = vision.AiTask?.GetAwaiter().GetResult();
            LatestDispatchSnapshot? snapshot = outcome?.Snapshot ?? vision.Snapshot;
            var payload = new
            {
                ok = vision.Ok && (outcome?.AiOk ?? !vision.Blocked),
                blocked = vision.Blocked,
                blockedBy = vision.Guard?.BlockedBy,
                message = vision.Message,
                source = vision.Source,
                cacheHit = outcome?.CacheHit ?? vision.CacheHit,
                memoHit = vision.MemoHit,
                dispatchId = outcome?.DispatchId ?? vision.Package?.DispatchId,
                aiOk = outcome?.AiOk,
                aiResponse = outcome?.AiResponse,
                aiModel = outcome?.AiModel,
                totalTokens = outcome?.TotalTokens,
                savedTotalTokens = outcome?.SavedTotalTokens,
                keySource = outcome?.KeySource,
                hasVisionAttachment = snapshot?.HasVisionAttachment ?? false,
                visionImageBytes = snapshot?.VisionImageBytes,
                visionImageSha256 = snapshot?.VisionImageSha256,
                guard = vision.Guard,
                timings = vision.Timings,
                burst = runtime.Vision.LastBurst,   // TASK-12b: Private WS start / peak (100 ms) / after LOH compaction
                latestDispatchFileError = outcome?.FileError
            };
            string json = JsonSerializer.Serialize(payload, ToolJson);
            return vision.Ok || vision.Blocked ? ToolResult.Ok(json) : ToolResult.Fail(json);
        }

        /// <summary>TASK-17 ADR-67 get_network_radar: /net status (+ optional one-port check) without the Cockpit; 0 tokens.</summary>
        private static ToolResult ExecuteNetworkRadar(NexusRuntime runtime, JsonNode? args)
        {
            int? port = null;
            try
            {
                if (args?["portFilter"] is JsonNode p && p.GetValueKind() == System.Text.Json.JsonValueKind.Number) port = p.GetValue<int>();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                port = null;
            }
            if (port is < 1 or > 65535) return ToolResult.Fail("portFilter phải trong 1–65535.");
            bool ping = args?["ping"] is not JsonNode pingNode || pingNode.GetValueKind() != System.Text.Json.JsonValueKind.False;
            NetRadarSnapshot snapshot = runtime.Network.ScanAsync(ping ? null : Array.Empty<NetworkRadar.Target>(), NetworkRadar.PingTimeoutMs, port)
                .GetAwaiter().GetResult();
            NetPortCheck? check = port.HasValue ? NetworkRadar.CheckPort(port.Value) : null;
            var payload = new
            {
                summary = NetworkRadar.Summary(snapshot),
                card = NetworkRadar.Describe(snapshot),
                nitro5Line = NetworkRadar.Nitro5Line(snapshot),
                snapshot,
                portCheck = check,
                portMessage = check != null ? NetworkRadar.DescribePort(check) : null,
                tokens = 0
            };
            return ToolResult.Ok(JsonSerializer.Serialize(payload, ToolJson));
        }

        /// <summary>TASK-15 ADR-62 get_active_window_context: the foreground window behind both shields (no Cockpit needed).</summary>
        private static ToolResult ExecuteActiveWindowContext(SqliteMemoryStore store, JsonNode? args)
        {
            bool includeClipboard = ReadBool(args, "includeClipboard");
            CtxResult ctx = ActiveWindowContext.Inspect(IntPtr.Zero,
                includeClipboard ? () => ClipboardService.TryReadText(ActiveWindowContext.MaxClipChars, out _) : null);
            store.WriteAuditLog("mcp_ctx", ActionRiskLevel.Green_SafeRead, JsonSerializer.Serialize(new
            {
                status = ctx.Status, process = ctx.ProcessName, rule = ctx.MatchedRule, shield = ctx.Shield,
                titleChars = ctx.WindowTitle?.Length ?? 0, clipChars = ctx.ClipText?.Length ?? 0, guardMs = ctx.GuardMs
            }), ctx.Status);
            var payload = new
            {
                status = ctx.Status,
                message = ctx.Message,
                processName = ctx.ProcessName,
                windowTitle = ctx.WindowTitle,
                clipText = ctx.ClipText,
                contextBlock = ctx.ContextBlock,
                contextChars = ctx.ContextBlock?.Length ?? 0,
                fingerprint = ctx.Fingerprint,
                matchedRule = ctx.MatchedRule,
                shield = ctx.Shield,
                guardMs = ctx.GuardMs,
                totalMs = ctx.TotalMs,
                scanCached = ctx.ScanCached,
                scanMs = ctx.ScanMs,
                tokens = 0
            };
            return ToolResult.Ok(JsonSerializer.Serialize(payload, ToolJson));
        }

        /// <summary>
        /// TASK-16b ADR-66 notify_shift_complete: saves config/shift_state.json, then hands the state to a running Cockpit over the
        /// pipe (SHIFTDONE). No Cockpit running = saved only (the Alt+A panel and the next start-up show it).
        /// </summary>
        private static ToolResult ExecuteNotifyShiftComplete(NexusRuntime runtime, JsonNode? args)
        {
            long t0 = Stopwatch.GetTimestamp();
            ShiftState state = ShiftStateFile.Sanitize(args?["agent"]?.ToString(), args?["gate"]?.ToString(), args?["status"]?.ToString(),
                args?["summary"]?.ToString(), "McpTools");
            string? fileError = ShiftStateFile.Write(runtime.ConfigDirectory, state);
            long t1 = Stopwatch.GetTimestamp();
            string? reply = CockpitPipeClient.Send("SHIFTDONE", ShiftStateFile.ToJson(state));
            double pipeMs = Math.Round((Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency, 3);
            JsonNode? cockpit = null;
            try { cockpit = reply == null ? null : JsonNode.Parse(reply); }
            catch (JsonException) { cockpit = JsonValue.Create(reply); }
            var payload = new
            {
                ok = fileError == null,
                state,
                file = Path.Combine(runtime.ConfigDirectory, ShiftStateFile.FileName),
                fileError,
                cockpitDelivered = reply != null,
                cockpit,
                pipeMs,
                totalMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3)
            };
            string json = JsonSerializer.Serialize(payload, ToolJson);
            return fileError == null ? ToolResult.Ok(json) : ToolResult.Fail(json);
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
