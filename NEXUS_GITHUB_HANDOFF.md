# 📦 HỒ SƠ ĐỒNG BỘ DỰ ÁN TRÊN GITHUB & BÀN GIAO CHO GEMINI (`NEXUS Home Hub`)

> **Hướng dẫn dành cho Gemini Web (Architect & Reality Checker)**: Tài liệu này được cập nhật tự động bởi **Google Antigravity** trực tiếp trên máy tính của **Huỳnh Nhật Hòa** sau khi hoàn tất **TASK-01** và **TASK-02 (`NEXUSHome.HostService` & `NEXUSHome.CockpitUI`)**.

---

## 1. Báo cáo Tiền kiểm Gate 1 -> Gate 2 (Phase 1 Hardening)
1. **SQLite WAL & Busy Timeout tường minh (`01_Foundation_Engine/NEXUSHome.Core/NEXUSHomeHub.Core.cs`)**:
   - Đã thiết lập tường minh trong `InitializeWalSchema()` và `OpenWalConnection()`:
     ```sql
     PRAGMA journal_mode=WAL;
     PRAGMA synchronous=NORMAL;
     PRAGMA busy_timeout=5000;
     ```
   - Đã kiểm tra thực tế qua hàm `GetWalTelemetrySummary()`: `SqliteJournalMode = "WAL"`, `SqliteBusyTimeoutMs = 5000`.
2. **Luồng Stdio Không BOM (`01_Foundation_Engine/NEXUSHome.McpTools/Program.cs`)**:
   - Đã cấu hình cả `Console.InputEncoding` và `Console.OutputEncoding` bằng `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` (0-byte BOM, an toàn tuyệt đối cho JSON-RPC 2.0 stdio).

---

## 2. Cấu trúc Cây Thư Mục Hiện Tại (`C:\Agent\NEXUS_Home_Hub`)

```text
C:\Agent\NEXUS_Home_Hub\
├── .gitignore                            # Chặn bin/obj, file .db/.db-wal/.db-shm và khóa bí mật
├── GEMINI.md                             # Luật phối hợp Liên minh Gemini Web ↔ Antigravity
├── README.md                             # Tài liệu kiến trúc tổng thể cho GitHub Repo
├── NEXUS_GITHUB_HANDOFF.md               # Hồ sơ bàn giao tổng hợp & báo cáo nghiệm thu
├── 01_Foundation_Engine\
│   ├── NEXUSHomeHub.slnx                 # Solution C# gồm 4 Projects
│   ├── NEXUSHome.Core\
│   │   ├── NEXUSHome.Core.csproj         # Thư viện lõi (Microsoft.Data.Sqlite, WAL 5000ms)
│   │   └── NEXUSHomeHub.Core.cs          # AgentRegistry, SqliteMemoryStore, LocalPcMcpTools, GovernanceGate
│   ├── NEXUSHome.McpTools\
│   │   ├── NEXUSHome.McpTools.csproj
│   │   └── Program.cs                    # JSON-RPC 2.0 stdio Local MCP Server (UTF8Encoding(false))
│   ├── NEXUSHome.CockpitUI\
│   │   ├── NEXUSHome.CockpitUI.csproj    # WPF UI Library cho Intel UHD 730 & MSI Optix G272
│   │   ├── CockpitShellWindow.xaml       # Giao diện 3-Pane MFD Vector 1px (Zero-Blur, Zero-DropShadow)
│   │   └── CockpitShellWindow.xaml.cs    # Lazy Warm Window, Auto-Focus Command Box & Virtualized Output
│   └── NEXUSHome.HostService\
│       ├── NEXUSHome.HostService.csproj  # OutputType=WinExe (chạy ngầm không hiện cửa sổ Console)
│       ├── AulaHero68HotkeyListener.cs   # P/Invoke RegisterHotKey + MOD_NOREPEAT + 150ms Debounce Gate
│       ├── CockpitPipeServer.cs          # Async Named Pipe Server (NEXUSHome.Cockpit.v1)
│       └── Program.cs                    # STA Host, System Tray Icon & Bộ đo Benchmark thực tế
├── 02_Agents_Registry\                   # Liên kết tới 283 Agents tại C:\Agent\.agents\skills
└── 03_Local_Data_Vault\
    ├── hot_cache\
    ├── structured_db\                    # nexus_memory.db (WAL Mode)
    ├── vector_index\
    ├── zettelkasten\
    └── handoff\
        ├── current_task.md               # Đặc tả TASK-02
        ├── qa_report.md                  # Báo cáo nghiệm thu chi tiết TASK-02
        └── task02_benchmark.json         # Số liệu đo đạc phần cứng thực tế xuất tự động
```

---

## 3. Chi tiết Kỹ thuật Đã Triển Khai trong TASK-02

### 3.1. Bộ Lắng Nghe Phím Tắt Phần Cứng `AulaHero68HotkeyListener` (`AULA HERO 68 HE` - 65% Layout, 8000Hz Polling)
- **Không dùng `WH_KEYBOARD_LL`**: Sử dụng 100% P/Invoke `RegisterHotKey` / `UnregisterHotKey` (`user32.dll`) gắn vào `HwndSource` message-only window (`HWND_MESSAGE`) trên luồng STA Message Pump.
- **Cờ phần cứng**: Mọi tổ hợp phím đều bật cờ `MOD_NOREPEAT` (`0x4000`) kết hợp với **Debounce Gate `150ms`** tính bằng `Stopwatch.GetTimestamp()` và `Stopwatch.Frequency` để triệt tiêu hoàn toàn hiện tượng dội phím từ trục từ tính Hall Effect (Rapid Trigger 8000Hz).
- **Bảng ánh xạ phím 65% (Không dùng hàng phím F1–F12)**:
  - `Alt + Space` (`MOD_ALT | MOD_NOREPEAT`, `VK_SPACE`): `ToggleCockpit` (Bật/tắt nhanh Cockpit UI).
  - `Ctrl + Shift + Space` (`MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT`, `VK_SPACE`): `QuickAgentPtt` (Mở Cockpit + bật công tắc Voice PTT + điền sẵn `@agent `).
  - `Alt + Q` (`MOD_ALT | MOD_NOREPEAT`, `VK_Q`): `CycleMfdMode` (Chuyển đổi 3 chế độ MFD: `TELEMETRY_WAL` -> `AGENT_SQUAD_283` -> `IGPU_GUARD_MSI_G272`).
  - `Alt + Backspace` (`MOD_ALT | MOD_NOREPEAT`, `VK_BACK`) hoặc phím `Esc` khi đang mở: `PanicHide` (Ẩn khẩn cấp tức thì + gọi `EmptyWorkingSet` thu hồi RAM về mức tối thiểu).

### 3.2. Giao Diện `CockpitShellWindow` (Tối ưu cho iGPU Intel UHD 730 & Màn hình MSI Optix G272 144Hz)
- **Zero-Blur & Zero-Shadow**: Kiểm tra toàn bộ mã XAML không chứa bất kỳ `BlurEffect` hay `DropShadowEffect` nào.
- **Pixel-Aligned Vector Rendering**:
  - Nền chính `#EB090D16`, viền Vector `1px` `#3338BDF8`.
  - Bật `UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`, `TextOptions.TextFormattingMode="Display"`, `TextOptions.TextRenderingMode="ClearType"`.
- **Bố cục 3 Phân vùng (3-Pane Tactical HUD)**:
  1. **Left MFD**: Hiển thị Telemetry thời gian thực của SQLite WAL (`journal_mode=WAL`, `busy_timeout=5000`), số lượng bản ghi `audit_logs` / `agent_memories`, RAM (`WorkingSet64` & `PrivateMemorySize64`), và danh sách Squad 283 Agents.
  2. **Center HUD**: Ô nhập lệnh `CommandInputBox` tự động nhận `Focus()` + `Keyboard.Focus()` ngay khi nhấn `Alt + Space`, bên dưới là `ListBox` kết quả đầu ra bật `VirtualizingStackPanel.IsVirtualizing="True"` và `VirtualizationMode="Recycling"`.
  3. **Right MFD**: 4 công tắc điều khiển nhanh (`ToggleGameBoost`, `ToggleVoicePtt`, `ToggleMcpGuard`, `ToggleZeroBlur`).
- **Named Pipe Server (`NEXUSHome.Cockpit.v1`)**: Cho phép Local MCP Server hoặc CLI bên ngoài gửi lệnh điều khiển bất đồng bộ (`TOGGLE`, `SHOW`, `HIDE`, `STATUS`, `SHUTDOWN`).

---

## 4. Kết Quả Nghiệm Thu Thực Tế (`Reality Checker Gate 2 Evidence`)

### 4.1. Biên dịch Release với `-warnaserror`
Lệnh thực thi:
```powershell
dotnet build C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHomeHub.slnx -c Release -warnaserror
```
Kết quả:
```text
  NEXUSHome.Core -> C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.Core\bin\Release\net8.0\NEXUSHome.Core.dll
  NEXUSHome.McpTools -> C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.dll
  NEXUSHome.CockpitUI -> C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.CockpitUI\bin\Release\net8.0-windows\NEXUSHome.CockpitUI.dll
  NEXUSHome.HostService -> C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 4.2. Đo đạc Phần cứng Thực tế (`03_Local_Data_Vault/handoff/task02_benchmark.json`)
Số liệu đo trực tiếp từ tiến trình `NEXUSHome.HostService.exe --benchmark` (Release build) trên máy của Huỳnh Nhật Hòa:

| Chỉ số Đo đạc (Metric) | Giá trị Thực tế | Mục tiêu (Target) | Đánh giá |
| :--- | :--- | :--- | :--- |
| **SQLite Journal Mode & Busy Timeout** | `WAL` (`5000 ms`) | `WAL` (`5000 ms`) | **ĐẠT (PASS)** |
| **Độ trễ `WM_HOTKEY` (`Alt+Space`) -> Hiện & Focus (Warm Run 1)** | **`7.914 ms`** | `< 16.0 ms` | **ĐẠT VƯỢT CHUẨN** |
| **Độ trễ `WM_HOTKEY` (`Alt+Space`) -> Hiện & Focus (Warm Run 2)** | **`4.527 ms`** | `< 16.0 ms` | **ĐẠT VƯỢT CHUẨN** |
| **Độ trễ `WM_HOTKEY` (`Alt+Space`) -> Hiện & Focus (Warm Run 3)** | **`6.897 ms`** | `< 16.0 ms` | **ĐẠT VƯỢT CHUẨN** |
| **Độ trễ Trung bình (`AverageWarmWakeupLatencyMs`)** | **`6.446 ms`** | `< 16.0 ms` (1 khung hình 60Hz) | **NHANH HƠN 2.5 LẦN** |
| **RAM khi Cockpit ĐANG MỞ (`WorkingSet64` / `PrivateMemorySize64`)** | **`192.28 MB` / `131.29 MB`** | Nhẹ, ổn định | **ĐẠT (PASS)** |
| **RAM khi Cockpit ĐANG ẨN (`WorkingSet64` / `PrivateMemorySize64`)** | **`15.15 MB` / `132.30 MB`** | Thu hồi tối đa Working Set | **CHỈ CHIẾM 15.15 MB RAM VẬT LÝ** |
| **Tải CPU & GPU khi Cockpit ĐANG ẨN** | **`0.0% CPU` / `0.0% GPU`** | `0.0% CPU` / `0.0% GPU` | **ĐẠT TUYỆT ĐỐI** |

---

## 5. Đề xuất Chuyển giao sang TASK-03 (Gửi Gemini Architect & Reality Checker)
Toàn bộ mã nguồn và báo cáo đo đạc của **TASK-02** đã được commit và đẩy lên **`https://github.com/bitmap1701/NEXUS-Home-Hub`**.
Đề nghị **Gemini Web** thực hiện nghiệm thu Gate 2 và ban hành chỉ thị **TASK-03** (tích hợp luồng **Gemini Multimodal Live WebSocket / Audio PCM 16kHz + Screen Capture** hoặc **Semantic Memory Vector & Agent Squad Dispatch** vào `CockpitPipeServer` và `CockpitShellWindow`).
