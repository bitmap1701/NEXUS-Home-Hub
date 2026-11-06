# 📦 HỒ SƠ ĐỒNG BỘ DỰ ÁN TRÊN GITHUB & BÀN GIAO CHO GEMINI (`NEXUS Home Hub`)

> **Hướng dẫn dành cho Gemini Web**: Tài liệu này được tạo tự động bởi **Google Antigravity** trực tiếp trên máy tính của **Huỳnh Nhật Hòa** sau khi đọc toàn bộ 4 lượt thiết kế từ link chia sẻ Gemini và hoàn tất triển khai **Phase 1 (TASK-01: Foundation Engine, SQLite WAL & Local MCP Server)**.

---

## 1. Báo cáo Hiện trạng Thực tế trên Máy Tính (`Reality Check`)
Antigravity đã kiểm tra môi trường thực tế trên PC và điều chỉnh các thông số vật lý cho khớp 100% với máy thật:
1. **Đường dẫn Workspace Thực tế**: Máy hiện không chia ổ `D:\`, toàn bộ dự án đã được quy hoạch chuẩn xác tại **`C:\Agent\NEXUS_Home_Hub`**.
2. **Kho 283 Agents (`The Agency`)**: Đã được cài đặt sẵn và kích hoạt tại **`C:\Agent\.agents\skills`** (bao gồm đầy đủ `agency-agents-orchestrator`, `agency-desktop-app-engineer`, `agency-mcp-builder`, `agency-reality-checker`, `agency-database-optimizer`, v.v.).
3. **.NET SDK**: Máy đang chạy **.NET SDK `10.0.401`** (tương thích hoàn toàn với target `net8.0` / `net10.0`).
4. **Git Repository**: Đã khởi tạo Git repo tại `C:\Agent\NEXUS_Home_Hub` kèm `.gitignore` bảo vệ kho dữ liệu nhạy cảm.

---

## 2. Cấu trúc Cây Thư Mục Đã Khởi Tạo (`C:\Agent\NEXUS_Home_Hub`)

```text
C:\Agent\NEXUS_Home_Hub\
├── .gitignore                       # Chặn bin/obj, file .db thực thi và khóa bí mật
├── GEMINI.md                        # Luật phối hợp Liên minh Gemini Web ↔ Antigravity
├── README.md                        # Tài liệu kiến trúc tổng thể cho GitHub Repo
├── NEXUS_GITHUB_HANDOFF.md          # File tổng hợp bàn giao này (để gửi trực tiếp cho Gemini)
├── 01_Foundation_Engine\
│   ├── NEXUSHomeHub.sln             # Solution C# chính
│   ├── NEXUSHome.Core\
│   │   ├── NEXUSHome.Core.csproj    # Thư viện lõi tích hợp Microsoft.Data.Sqlite
│   │   └── NEXUSHomeHub.Core.cs     # AgentRegistry, SqliteMemoryStore (WAL), LocalPcMcpTools, GovernanceGate
│   └── NEXUSHome.McpTools\
│       ├── NEXUSHome.McpTools.csproj
│       └── Program.cs               # JSON-RPC 2.0 stdio Local MCP Server
├── 02_Agents_Registry\              # Kết nối trực tiếp với C:\Agent\.agents\skills
└── 03_Local_Data_Vault\
    ├── hot_cache\
    ├── structured_db\               # Nơi sinh file nexus_memory.db (WAL Mode)
    ├── vector_index\
    ├── zettelkasten\
    └── handoff\
        ├── current_task.md          # Đặc tả TASK-01
        └── qa_report.md             # Báo cáo nghiệm thu gửi Gemini Reality Checker
```

---

## 3. Mã Nguồn Lõi Đã Triển Khai & Build Thành Công

### 3.1. `01_Foundation_Engine/NEXUSHome.Core/NEXUSHomeHub.Core.cs`
Đã tích hợp đầy đủ 4 thành phần nền móng:
- **`AgentRegistry`**: Quét và nạp động `SKILL.md` trực tiếp từ `C:\Agent\.agents\skills`.
- **`SqliteMemoryStore`**: Tự động bật `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;` và khởi tạo 2 bảng `audit_logs` (nhật ký kiểm toán mọi lệnh AI chạy trên máy) và `agent_memories` (trí nhớ dài hạn).
- **`LocalPcMcpTools`**:
  - `[CẤP XANH] GetSystemTelemetry()`: Đọc số luồng i5-12400, RAM khả dụng, Working Set của MCP Server và tổng số tiến trình.
  - `[CẤP VÀNG] OptimizePcForGamingOrCoding(targetProcessName)`: Gọi `psapi.dll` (`EmptyWorkingSet`) dọn RAM tiến trình nền và nâng `ProcessPriorityClass.High` cho tiến trình mục tiêu (`TslGame`, `DeltaForce`, `devenv`).
  - `[CẤP ĐỎ] InspectOrKillProcessByPort(port, dryRunOnly)`: Kiểm tra/giải phóng cổng mạng TCP/UDP.
- **`GovernanceGate`**: Tự động ghi `AuditLog` vào SQLite WAL cho mọi thao tác Cấp Xanh/Vàng và chặn bắt buộc xác nhận Human-in-the-Loop đối với Cấp Đỏ.

### 3.2. `01_Foundation_Engine/NEXUSHome.McpTools/Program.cs`
Local MCP Server chuẩn JSON-RPC 2.0 qua `stdio` hỗ trợ các phương thức:
- `initialize` (`protocolVersion: 2024-11-05`)
- `tools/list` (`get_system_telemetry`, `list_available_agents`, `optimize_pc_for_gaming_or_coding`, `save_nexus_memory`)
- `tools/call` (Thực thi hàm C# Native đi qua `GovernanceGate` và lưu vết vào `nexus_memory.db`).

---

## 4. Bằng chứng Nghiệm thu Thực tế (`Reality Checker Evidence`)
- **Lệnh biên dịch (`dotnet build NEXUSHomeHub.slnx`)**:
  ```text
  NEXUSHome.Core -> C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.Core\bin\Debug\net8.0\NEXUSHome.Core.dll
  NEXUSHome.McpTools -> C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.McpTools\bin\Debug\net8.0\NEXUSHome.McpTools.dll
  Build succeeded.
      0 Warning(s)
      0 Error(s)
  ```
- **Kết quả chạy thử Local MCP Server (`JSON-RPC 2.0 stdio`)**:
  - Phản hồi `get_system_telemetry` trên máy thật: `LogicalProcessors: 12` (i5-12400), `TotalRamMb: 16156`, **`McpServerWorkingSetMb: 34`** (chỉ chiếm **34 MB RAM**, vượt chuẩn `< 80 MB`).
  - Phản hồi `list_available_agents`: Kết nối và liệt kê thành công các Agent trong `C:\Agent\.agents\skills`.
  - Cơ sở dữ liệu `03_Local_Data_Vault/structured_db/nexus_memory.db` đã khởi tạo ở chế độ `WAL` và ghi nhận đầy đủ bản ghi `AutoApproved_Green` vào bảng `audit_logs`.

---

## 5. Yêu cầu Tiếp theo gửi Gemini Architect (`TASK-02`)
Hãy đóng vai trò **`Reality Checker`** nghiệm thu kết quả **TASK-01** ở trên, sau đó kích hoạt **`Desktop App Engineer`**, **`XR Cockpit Interaction Specialist`** và **`Realtime Collaboration Engineer`** để xuất đặc tả **`current_task.md` cho TASK-02**:
1. Thiết kế project **`NEXUSHome.HostService`** (Background Service + Global Hotkey `Win32 RegisterHotKey` cho bàn phím **AULA HERO 68 HE**).
2. Thiết kế giao diện **`NEXUSHome.CockpitUI`** (WPF .NET ghim cố định sang màn hình phụ **MSI Optix G272**, ưu tiên chạy trên **Intel UHD 730** để giải phóng 100% **GTX 1660**).
3. Thiết kế module kết nối **Gemini Multimodal Live WebSocket** (thu âm PCM 16kHz + chụp cửa sổ đang mở).
