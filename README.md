# NEXUS Home Hub — Ngôi Nhà AI Trên PC (Windows 11 Local Agentic OS Overlay)

> **Dự án Liên minh Đa Tác Nhân (Twin-Engine Setup)**: **Gemini Web (Architect & Reality Checker)** ↔ **Google Antigravity (Local IDE Executor)**  
> **Chủ sở hữu**: Huỳnh Nhật Hòa

---

## 1. Tổng quan Kiến trúc 4 Tầng (`NEXUS Home Hub`)

`NEXUS Home Hub` là hệ thống điều phối AI chạy nền trên Windows 11, kết nối **Gemini API (Live WebSocket + Function Calling)** với **Kho 283 Chuyên gia (`The Agency`)** và **Hệ điều hành Windows** thông qua giao thức **Local Model Context Protocol (MCP)**, **Named Pipe JSONL (`NEXUS_Home_Cockpit_Pipe`)**, và giao diện **Zero-Blur Tactical HUD (`NEXUSHome.CockpitUI`)**.

```text
C:\Agent\NEXUS_Home_Hub\
├── GEMINI.md                        # Quy tắc phối hợp chuẩn giữa Gemini Web & Antigravity
├── README.md                        # Tài liệu tổng quan kiến trúc & trạng thái dự án
├── NEXUS_GITHUB_HANDOFF.md          # Hồ sơ bàn giao & báo cáo nghiệm thu gửi Gemini
├── 01_Foundation_Engine\            # [PHẦN MÓNG & COCKPIT] Solution & Mã nguồn C# (.NET)
│   ├── NEXUSHomeHub.slnx            # Solution chính gồm 4 Projects
│   ├── NEXUSHome.Core\              # AgentRegistry, GovernanceGate 3 cấp, SqliteMemoryStore (WAL 5000ms), LocalPcMcpTools
│   ├── NEXUSHome.McpTools\          # Local MCP Server chuẩn JSON-RPC stdio (UTF8Encoding không BOM)
│   ├── NEXUSHome.CockpitUI\         # Giao diện WPF 3-Pane MFD Vector 1px tối ưu cho Intel UHD 730 & MSI G272 144Hz
│   └── NEXUSHome.HostService\       # WinExe Host chạy ngầm: System Tray, Named Pipe Server & AulaHero68HotkeyListener
├── 02_Agents_Registry\              # Liên kết trực tiếp tới C:\Agent\.agents\skills (283 Agents)
└── 03_Local_Data_Vault\             # [KHO DỮ LIỆU 4 TẦNG] Lưu trữ cục bộ 100% trên SSD NVMe
    ├── hot_cache\                   # Cấu hình & snapshot của In-Memory Hot Cache
    ├── structured_db\               # CSDL SQLite (WAL Mode): nexus_memory.db
    ├── vector_index\                # Chỉ mục tìm kiếm ngữ nghĩa
    ├── zettelkasten\                # Thẻ ghi chú nguyên tử Markdown
    └── handoff\                     # Hộp thư bàn giao tự động giữa Gemini & Antigravity
        ├── current_task.md          # Đặc tả task hiện tại
        ├── qa_report.md             # Báo cáo kết quả build/test gửi Gemini Reality Checker
        └── task02_benchmark.json    # Số liệu đo đạc phần cứng thực tế
```

---

## 2. Cấu hình Phần cứng & Phân luồng Tài nguyên
- **CPU / RAM**: Intel Core i5-12400 (6 nhân / 12 luồng) | 16GB DDR4 Dual-Channel.
- **Màn hình chính (Gaming & Coding)**: LG UltraGear 200Hz chạy trên card rời **Galax GTX 1660** (giữ 100% hiệu năng cho game Delta Force, FC Online và IDE).
- **Màn hình phụ (AI Cockpit)**: MSI Optix G272 (27" 1920x1080 144Hz) chạy trên nhân đồ họa tích hợp **Intel UHD 730** (24 EUs, Vector 1px `#3338BDF8`, nền `#EB090D16`, không dùng `BlurEffect`/`DropShadowEffect`).
- **Bàn phím**: **AULA HERO 68 HE** (65% Layout, 8000Hz Polling, Rapid Trigger) — Lắng nghe phím tắt qua P/Invoke `RegisterHotKey` (`MOD_NOREPEAT`) kèm chốt chặn chống dội phím `150ms` Debounce Gate:
  - `Alt + Space`: Bật/tắt Cockpit UI (`ToggleCockpit`) — Độ trễ phản hồi trung bình **`6.45 ms`** (`< 16 ms`).
  - `Ctrl + Shift + Space`: Gọi nhanh Agent (`QuickAgentPtt`).
  - `Alt + Q`: Chuyển chế độ MFD (`CycleMfdMode`).
  - `Alt + Backspace` (hoặc `Esc`): Ẩn khẩn cấp (`PanicHide`) — Thu hồi RAM Working Set về **`15.15 MB`**, `0.0% CPU / 0.0% GPU`.

---

## 3. Chốt chặn An toàn 3 Cấp (`GovernanceGate`)
1. **Cấp Xanh (`Green_SafeRead`)**: Đọc thông số CPU/RAM/Processes (`get_system_telemetry`), truy vấn bộ nhớ SQLite WAL, đọc danh sách 283 Agents — Tự động thực thi.
2. **Cấp Vàng (`Yellow_ReversibleTune`)**: Tối ưu hóa Working Set RAM & nâng độ ưu tiên CPU (`optimize_pc_for_gaming_or_coding`) — Thực thi kèm ghi log kiểm toán (`Audit Log`) vào SQLite WAL.
3. **Cấp Đỏ (`Red_CriticalSystem`)**: Giải phóng cổng mạng TCP/UDP (`kill_process_by_port`), xóa file, can thiệp hệ thống — Bắt buộc xác nhận **Human-in-the-Loop (HITL)** trước khi thực thi.

---

## 4. Hướng dẫn Biên dịch & Chạy

### Biên dịch toàn bộ Solution ở chế độ Release (`0 Warnings, 0 Errors`):
```powershell
dotnet build C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHomeHub.slnx -c Release -warnaserror
```

### Chạy dịch vụ ngầm `NEXUSHome.HostService` (System Tray + Hotkey + Cockpit UI):
```powershell
Start-Process "C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe"
```
