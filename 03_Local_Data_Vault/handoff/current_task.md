# TASK-01: Khởi tạo Solution C# (.NET) & SQLite WAL Database + Local MCP Server

## 1. Context Summary
- Khởi tạo nền móng (Foundation Engine) cho **NEXUS Home Hub** tại `C:\Agent\NEXUS_Home_Hub`.
- Kết nối kho kỹ năng `C:\Agent\.agents\skills` thông qua `AgentRegistry`.
- Thiết lập cơ sở dữ liệu cục bộ **SQLite (chế độ WAL - Write-Ahead Logging)** để lưu trữ lịch sử phiên, cấu hình tối ưu game/app và nhật ký kiểm toán (`Audit Log`).
- Xây dựng **Local MCP Server (`NEXUSHome.McpTools`)** giao tiếp qua chuẩn JSON-RPC 2.0 `stdio`.

## 2. Completed Artifacts
- `GEMINI.md`: Quy tắc vận hành liên minh Gemini Web ↔ Antigravity.
- `01_Foundation_Engine/NEXUSHome.Core`:
  - `AgentRegistry`: Nạp động hồ sơ các Agent từ `C:\Agent\.agents\skills`.
  - `GovernanceGate`: Cổng kiểm duyệt an toàn 3 cấp (Xanh / Vàng / Đỏ).
  - `LocalPcMcpTools`: Đọc telemetry hệ thống, dọn Working Set RAM (`psapi.dll`), nâng ưu tiên CPU và tra cứu cổng mạng.
  - `SqliteMemoryStore`: Khởi tạo `nexus_memory.db` với `PRAGMA journal_mode=WAL;` và bảng `audit_logs`, `agent_memories`.
- `01_Foundation_Engine/NEXUSHome.McpTools`:
  - `Program.cs`: JSON-RPC 2.0 `stdio` MCP Server cung cấp các công cụ `get_system_telemetry`, `list_available_agents`, `optimize_pc_for_gaming_or_coding`, `log_agent_memory`.

## 3. Constraints
- Chiếm dụng RAM nền tối thiểu (< 80MB), CPU nền ~0% khi rỗi.
- Mọi thao tác Cấp Vàng và Cấp Đỏ đều phải được ghi nhận vào bảng `audit_logs` trong SQLite WAL.

## 4. Verification Steps
1. Chạy `dotnet build` trên toàn bộ Solution `NEXUSHomeHub.sln` đạt **0 Error, 0 Warning**.
2. Kiểm tra phản hồi JSON-RPC 2.0 (`initialize`, `tools/list`, `tools/call`) của `NEXUSHome.McpTools`.
