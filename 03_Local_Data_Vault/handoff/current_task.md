# TASK-03: Agent Squad Dispatch Engine & Dynamic Persona Router (Hướng A)

## 1. Context Summary (Bối cảnh & Quyết định Kiến trúc)
- **Trạng thái trước đó**: Phase 1 (SQLite WAL + Local MCP Server, RAM 34MB) và Phase 2 (HostService Hotkey + CockpitUI Monitor Routing trên nhánh `claude/task-02-monitor-routing`) đã hoàn thành khung cơ sở.
- **Quyết định Gate 2 (§3)**:
  1. IPC giữa `HostService` và `CockpitUI` dùng Named Pipes (`NEXUS_Home_Cockpit_Pipe`, JSONL).
  2. `CockpitUI` mặc định dùng `RenderMode.SoftwareOnly` + tùy chọn `GpuPreference=1` (Intel UHD 730) để bảo toàn 100% tài nguyên GTX 1660.
  3. Monitor Routing áp dụng 3-Tier Fallback (EDID/Config -> First Non-Primary -> Primary Top-Right HUD) kèm `PerMonitorV2` DPI.
  4. Cửa sổ `CockpitUI` bắt buộc gắn cờ Win32 `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` ở chế độ HUD để không làm văng game Fullscreen.
  5. Giữ kiến trúc tách rời tiến trình (`HostService` làm Supervisor quản lý vòng đời `CockpitUI`).
- **Mục tiêu TASK-03 (Hướng A — Agent Squad Dispatch)**: Xây dựng bộ máy nạp động 283 Agents từ `02_Agents_Registry`, phân bổ vào 5 Squad cá nhân hóa của Nhật Hòa, lưu vết phiên điều phối vào SQLite WAL và cung cấp 3 MCP Tools mới để Antigravity / Claude / Gemini gọi trực tiếp.

## 2. Completed Artifacts (Từ TASK-01 & TASK-02)
- `NEXUSHome.McpTools`: JSON-RPC stdio server + SQLite WAL connection.
- `NEXUSHome.HostService` & `NEXUSHome.CockpitUI`: Global Hotkey listener & Multi-monitor routing logic.

## 3. Technical Specs cho TASK-03 (Yêu cầu Cài đặt Chi tiết)

### A. Module `AgentRegistry` & `SquadDispatcher` (C# .NET 8)
1. **Quét & Đánh chỉ mục Nhanh (`AgentRegistry.cs`)**:
   - Quét đệ quy thư mục `02_Agents_Registry` (hỗ trợ cả các phân khu `engineering`, `game-development`, `specialized`, `testing_security`, v.v.).
   - Trích xuất Metadata của từng file `.md` (Agent Id, Tên hiển thị, Phân khu, Tóm tắt sứ mệnh) và nạp vào bộ nhớ đệm nhẹ (dưới 5MB RAM).
   - Nếu thư mục `02_Agents_Registry` chỉ mới có một số file mẫu, tự động khởi tạo thêm 5 hồ sơ Agent hạt nhân đại diện cho 5 Squad nếu chưa tồn tại:
     - **Squad 1 (Core .NET, Socket & DB)**: `engineering/engineering-backend-architect.md` & `testing_security/testing-reality-checker.md`
     - **Squad 2 (UTH Academic & Jira)**: `specialized/pm-sprint-prioritizer.md`
     - **Squad 3 (Gaming & 200Hz Performance)**: `game-development/unreal-systems-engineer.md`
     - **Squad 4 (Smart Room, MCP & Zettelkasten)**: `specialized/agents-orchestrator.md`
     - **Squad 5 (Hardware Finance & Fitness)**: `specialized/finance-tool-evaluator.md`
2. **Định tuyến Biệt đội Thông minh (`SquadDispatcher.cs`)**:
   - Hỗ trợ chọn Agent trực tiếp theo `agentId` HOẶC tự động gợi ý Squad + Primary Agent + QA Agent dựa trên từ khóa nhiệm vụ (`intent` / `taskDescription`) và chế độ vận hành (`NEXUS-Micro`, `NEXUS-Sprint`, `NEXUS-Full`).
   - Đóng gói đầu ra theo đúng **6-Part Agent Anatomy** kết hợp ngữ cảnh phần cứng cố định của Nhật Hòa (i5-12400, GTX 1660, 16GB RAM, LG 200Hz + MSI G272).

### B. Mở rộng Schema SQLite WAL (`nexus_memory.db`)
Tạo thêm (nếu chưa có) bảng `agent_dispatch_logs` trong SQLite WAL:
- `id` (INTEGER PRIMARY KEY AUTOINCREMENT)
- `timestamp_utc` (TEXT NOT NULL)
- `nexus_mode` (TEXT NOT NULL) — `Micro` | `Sprint` | `Full`
- `squad_id` (TEXT NOT NULL) — `Squad1` .. `Squad5`
- `primary_agent` (TEXT NOT NULL)
- `qa_agent` (TEXT NOT NULL)
- `task_summary` (TEXT NOT NULL)
- `status` (TEXT NOT NULL) — `DISPATCHED` | `NEEDS_WORK` | `PASSED`

### C. Bổ sung 3 Công cụ Mới vào `NEXUSHome.McpTools` (JSON-RPC `tools/list` & `tools/call`)
1. `list_agent_squads`:
   - **Rủi ro**: Cấp Xanh (Tự động thực thi).
   - **Chức năng**: Trả về danh sách 5 Squad cá nhân hóa và các Agent hiện có trong `02_Agents_Registry`.
2. `dispatch_agent_squad`:
   - **Rủi ro**: Cấp Xanh (Đọc file `.md` & ghi log điều phối vào SQLite WAL).
   - **Tham số**: `taskDescription` (string, bắt buộc), `nexusMode` (string: `Micro`/`Sprint`/`Full`, mặc định `Micro`), `preferredAgentId` (string, tùy chọn).
   - **Đầu ra**: Trả về gói JSON chứa `SystemInstruction` đầy đủ của Primary Agent, tên `QaAgent` đi kèm, danh sách ràng buộc phần cứng và `dispatchId` vừa lưu trong SQLite.
3. `update_dispatch_verdict`:
   - **Rủi ro**: Cấp Vàng (Cập nhật trạng thái nghiệm thu vào SQLite WAL).
   - **Tham số**: `dispatchId` (integer), `verdict` (`NEEDS_WORK` | `PASSED`), `evidenceNotes` (string).
   - **Chức năng**: Cho phép `Reality Checker` hoặc QA Agent chốt kết quả vòng lặp Dev-QA vào cơ sở dữ liệu.

## 4. Constraints & Hardware/Env (Ràng buộc Kỹ thuật)
- **Kỷ luật Minimal Change**: Không làm hỏng các hàm MCP hiện có (`get_system_telemetry`, v.v.) và giữ nguyên cấu trúc SQLite WAL của Phase 1.
- **Hiệu năng**: Thời gian thực thi `dispatch_agent_squad` (đọc file `.md` + ghi SQLite WAL) phải đạt P95 dưới 50ms trên ổ SSD NVMe; tổng RAM của `McpTools` duy trì dưới 50MB.
- **Bảo mật**: Chống lỗi Path Traversal khi đọc `preferredAgentId` (chỉ cho phép đọc các file `.md` nằm bên trong thư mục gốc `02_Agents_Registry` thông qua kiểm tra `Path.GetFullPath`).

## 5. Verification Steps (Các bước Kiểm thử Nghiệm thu Gate 3)
1. **E1 (Build Check)**: Chạy `dotnet build` toàn Solution đạt 0 lỗi.
2. **E2 (MCP Contract Test)**: Gửi request JSON-RPC `tools/list` qua stdio và xác nhận có đủ 3 công cụ mới (`list_agent_squads`, `dispatch_agent_squad`, `update_dispatch_verdict`).
3. **E3 (Dispatch & SQLite Persistence Test)**: Gọi `dispatch_agent_squad` với `taskDescription = "Tối ưu UDP Socket C# không rớt gói tin"`, xác nhận trả về đúng `Squad 1`, nạp thành công nội dung Markdown và ghi 1 bản ghi `DISPATCHED` vào bảng `agent_dispatch_logs`.
4. **E4 (Path Traversal Security Test)**: Thử truyền `preferredAgentId = "../../GEMINI.md"` và xác nhận hệ thống từ chối truy cập ngoài phạm vi `02_Agents_Registry`.
5. **E5 (Gate 2 Regression & Hardware Sync)**: Áp dụng các quyết định §3 (Named Pipes, `SoftwareOnly`, `WS_EX_NOACTIVATE`) vào `HostService` / `CockpitUI` mà không phát sinh lỗi biên dịch.
