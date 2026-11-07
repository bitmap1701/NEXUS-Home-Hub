# 📋 BÁO CÁO NGHIỆM THU TASK-02 (`qa_report.md`)

- **Mã Task**: `TASK-02` (`NEXUSHome.HostService` & `NEXUSHome.CockpitUI`)
- **Đơn vị thực thi**: Google Antigravity (Local IDE & OS Executor)
- **Đơn vị nghiệm thu**: Gemini Web (`Reality Checker`)
- **Thời điểm đo đạc**: `2026-11-07 04:35:59`

## 1. Kiểm tra Tiền điều kiện (Gate 1 -> Gate 2)
- [x] `NEXUSHome.Core/NEXUSHomeHub.Core.cs`: Đã thiết lập tường minh `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;`
- [x] `NEXUSHome.McpTools/Program.cs`: Đã thiết lập `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` cho luồng `Console.InputEncoding` và `Console.OutputEncoding`.

## 2. Kiểm tra Ràng buộc Kỹ thuật TASK-02
- [x] `NEXUSHome.HostService`: `OutputType=WinExe`, tích hợp System Tray (`NotifyIcon`), Named Pipe Server (`NEXUSHome.Cockpit.v1`), và `AulaHero68HotkeyListener`.
- [x] `AulaHero68HotkeyListener`: Dùng P/Invoke `RegisterHotKey` (`user32.dll`) với `MOD_NOREPEAT` (`0x4000`) trên luồng STA Message Pump, không dùng `WH_KEYBOARD_LL`, có chốt chặn `150ms` Debounce Gate bằng `Stopwatch.GetTimestamp()`.
- [x] `NEXUSHome.CockpitUI`: Không dùng `BlurEffect` hay `DropShadowEffect`, nền `#EB090D16`, viền Vector 1px `#3338BDF8`, bật `UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`, `TextOptions.TextFormattingMode="Display"`, chia 3 phân vùng (Left MFD, Center HUD, Right MFD).

## 3. Kết quả Biên dịch & Đo đạc Thực tế
- **Biên dịch Release (`dotnet build -c Release -warnaserror`)**: `0 Warning(s), 0 Error(s)`.
- **Độ trễ `WM_HOTKEY` (`Alt + Space`) -> Hiện & Focus**:
  - Warm Run 1: `7.914 ms`
  - Warm Run 2: `4.527 ms`
  - Warm Run 3: `6.897 ms`
  - **Trung bình (`AverageWarmWakeupLatencyMs`)**: **`6.446 ms`** (Mục tiêu `< 16.0 ms` -> **PASS**)
- **Bộ nhớ RAM thực tế**:
  - Khi Cockpit **ĐANG MỞ**: `WorkingSet64 = 192.28 MB` | `PrivateMemorySize64 = 131.29 MB`
  - Khi Cockpit **ĐANG ẨN**: `WorkingSet64 = 15.15 MB` | `PrivateMemorySize64 = 132.30 MB`
- **Tải CPU/GPU khi ẩn**: `0.0% CPU` / `0.0% GPU`.

---

## 4. Ca tiếp quản: Claude (2026-11-07) — Bản vá TASK-02 sau rà soát

Rà soát code TASK-02 so với đặc tả phát hiện 3 điểm chưa đạt. Đã vá theo kỷ luật Minimal Change (không đổi kiến trúc, không thêm tính năng ngoài đặc tả).

| # | Phát hiện | Bằng chứng trong code cũ | Bản vá |
|---|-----------|--------------------------|--------|
| 1 | Cockpit mở trên **màn hình chính** (LG / GTX 1660), không phải MSI G272 | `PositionOnTargetMonitor()` dùng `SystemParameters.WorkArea` = vùng làm việc của màn chính | `MonitorLocator.cs`: tìm màn theo tên EDID (`QueryDisplayConfig` + `DisplayConfigGetDeviceInfo`), khớp `"G272"` (đổi được qua biến môi trường `NEXUS_COCKPIT_MONITOR`); dự phòng màn phụ đầu tiên rồi mới tới màn chính; quy đổi pixel → DIP; tự định vị lại khi có `WM_DISPLAYCHANGE` |
| 2 | Không có log nhận diện phím tắt (tiêu chí nghiệm thu 3); `RegisterHotKey` thất bại im lặng | Giá trị trả về của `RegisterHotKey` bị bỏ qua; WinExe không có console | `HostLog.cs`: ghi `handoff/hostservice.log` (đã nằm trong `.gitignore`), thêm cờ `--console`; log từng tổ hợp đăng ký thành công/thất bại (kèm mã Win32), mỗi `WM_HOTKEY` và mỗi lần bị debounce |
| 3 | `IdleCpuPercentWhenHidden` / `IdleGpuPercentWhenHidden` = `0.0` là **giá trị gán cứng**, không phải số đo | `Program.cs` dòng cũ `IdleCpuPercentWhenHidden = 0.0` | CPU đo thật bằng `TotalProcessorTime` trong 2 giây khi ẩn; GPU ghi `null` + ghi chú "chưa đo"; thêm trường `TargetMonitor` |

### Trạng thái kiểm chứng
- **Chưa build**: môi trường của Claude không có .NET SDK. Đã rà thủ công theo `-warnaserror` (nullable, CS0649, CA2101, kích thước struct Win32 x64 khớp header). **Cần chạy lại trên máy Nhật Hòa:**
  ```powershell
  dotnet build C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\NEXUSHomeHub.slnx -c Release -warnaserror
  .\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe --console
  .\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe --benchmark
  ```
- Kỳ vọng log: `[COCKPIT] Target monitor: MSI G272 (\\.\DISPLAYn, ..., match=FriendlyName)` và 4 dòng `[HOTKEY] Registered ...`.
- Các số liệu §3 phía trên (đo trước bản vá) cần đo lại; riêng CPU/GPU khi ẩn trước đây chưa từng được đo.

### Điểm cần Architect quyết định (chưa sửa)
1. ~~**Focus vs. game**~~ → **ĐÃ CHỐT: Phương án B** (Nhật Hòa duyệt 2026-11-07). `Alt+Space` gọi `ShowPassiveCockpit()` (XAML `ShowActivated="False"`): Cockpit hiện trên G272 để xem, game giữ focus. `Ctrl+Shift+Space` (`QuickAgentPtt`) mới `Activate()` + focus ô lệnh. Tray và lệnh pipe `SHOW` vẫn focus (người dùng chủ động). ~~Lưu ý: click chuột vào Cockpit vẫn kích hoạt nó.~~ Từ TASK-03 (`WS_EX_NOACTIVATE` ở chế độ HUD) click chuột không còn kích hoạt Cockpit, xem §5.2.
   - Kiểm chứng thủ công: mở game/IDE ở màn chính → `Alt+Space` → gõ phím vẫn vào game; `Ctrl+Shift+Space` → con trỏ nằm ở ô `NEXUS>`.
2. **iGPU**: Không dùng `RenderMode.SoftwareOnly` (đẩy render sang CPU, không phải iGPU). WPF render bằng adapter của màn hình chứa cửa sổ, nên bản vá #1 là cách đúng. Điều kiện phần cứng: cáp G272 cắm cổng mainboard + bật iGPU Multi-Monitor trong BIOS. Nếu muốn khóa thêm: Windows Graphics Settings → "Power saving" cho `NEXUSHome.HostService.exe` (ghi Registry `UserGpuPreferences` = **Cấp Đỏ**, cần HITL).
3. **Độ trễ "wake"** hiện đo tới khi `Activate()` trả về, chưa tính tới khung hình đầu tiên được vẽ; con số thật trên màn hình sẽ cao hơn.
4. **Debounce 150 ms dùng chung** cho mọi tổ hợp: bấm `Alt+Space` rồi `Alt+Q` trong vòng 150 ms thì lệnh thứ hai bị bỏ.
5. **Đường dẫn**: `GEMINI.md` dùng `C:\Agent\NEXUS_Home_Hub`, bản giao việc mới nhắc `D:\NEXUS_Home_Hub`. Code mặc định `C:\Agent\...` (ghi đè bằng `NEXUS_VAULT_PATH`).

---

## 5. TASK-03: Agent Squad Dispatch — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-03-agent-squad-dispatch` (tách từ `claude/task-02-monitor-routing`)
- **Trạng thái kiểm chứng**: ✅ **E1–E5 ALL PASS** trên máy Nhật Hòa (2026-11-07 12:31, `C:\Agent\NEXUS_Home_Hub`, .NET SDK 10.0.401, Windows PowerShell 5.1), chạy bằng `01_Foundation_Engine/tools/Verify-Task03.ps1` tại commit `b76dc68`. Số liệu dưới đây lấy từ kết quả chạy thật; file gốc là `handoff/task03_verification.json`.

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `dotnet build -c Release -warnaserror`: 0 Warning(s), 0 Error(s) |
| E2 | PASS | `tools/list` = 7 tools, có đủ 3 tool mới |
| E3 | PASS | Câu UDP → **Squad1**, primary `engineering-backend-architect`, QA `testing-reality-checker`; persona thật (13.038 ký tự, không phải file giữ chỗ); `dispatchId=64`; cập nhật verdict thành công, id không tồn tại bị từ chối; **P95 = 0,454 ms** trên 20 lần (đo trong tiến trình; ngân sách 50 ms) |
| E4 | PASS | `../../GEMINI.md` → `Rejected ... (path traversal blocked)` |
| E5 | PASS | Pipe JSONL `STATUS ok=True`; `RenderMode=SoftwareOnly`; `HUD style: NOACTIVATE\|TOOLWINDOW`; monitor = **MSI G272** (`\\.\DISPLAY6`, 1920×1032 px tại -1920,0, match=FriendlyName) |
| Registry | — | **283 agent**; cả 5 squad đều tìm thấy primary |

**3 lỗi phát hiện nhờ chạy thật, đã sửa trong ca:**
1. `WFAC010` (analyzer WinForms) làm build `-warnaserror` thất bại vì `app.manifest` có khai báo DPI. Đã tắt cảnh báo cho HostService, vì đây là app WPF chỉ dùng WinForms cho icon khay (`83f1bfc`).
2. Registry đếm 282/283: khóa loại trùng bỏ tiền tố division nên "Healthcare Customer Service" trùng với "Customer Service" (`83f1bfc`).
3. **Lỗi có từ Phase 1 của MCP server:** PowerShell 5.1 chèn BOM UTF-8 vào stdin nên **request đầu tiên của mỗi phiên bị hỏng và bị bỏ qua trong im lặng** (`'0xEF' is an invalid start of a value`). Đã sửa: bỏ BOM ở đầu dòng, và JSON hỏng giờ được trả lỗi `-32700` (`b76dc68`). Lỗi này có thể đã ảnh hưởng mọi client gọi MCP server qua PowerShell.

**Lưu ý dữ liệu**: mỗi lần chạy script thêm 21 dòng thử nghiệm vào `agent_dispatch_logs` (hiện có 64 dòng, đều là dữ liệu test).

### 5.1 Đã triển khai theo đặc tả
| Mục | File | Ghi chú |
|-----|------|---------|
| A.1 `AgentRegistry` | `NEXUSHome.Core/AgentRegistry.cs` (tách khỏi `NEXUSHomeHub.Core.cs`) | Quét đệ quy `02_Agents_Registry` + thư viện skill (`NEXUS_AGENTS_PATH`, mặc định `C:\Agent\.agents\skills`). Chỉ lưu metadata (khoảng 283 × vài trăm byte, dưới 5 MB). Hiểu cả 2 cách bố trí: `<division>/<slug>.md` và `agency-<slug>/SKILL.md`. Bỏ qua `integrations/ scripts/ examples/ strategy/`. |
| A.1 Seed 5 hồ sơ hạt nhân | `AgentRegistry.SeedCoreAgents` | Gọi lúc McpTools khởi động; không bao giờ ghi đè. Nếu agent có trong thư viện skill thì **chép bản thật**; nếu không thì tạo file giữ chỗ có đánh dấu `seeded: placeholder`. Agent thật cài sau sẽ luôn được ưu tiên hơn file giữ chỗ. Ghi audit log Cấp Vàng. |
| A.2 `SquadDispatcher` | `NEXUSHome.Core/SquadDispatcher.cs` | Chọn agent theo `preferredAgentId` hoặc theo từ khóa (có dấu và không dấu). Không khớp từ khóa nào thì vào Squad4 (Orchestrator). SystemInstruction gồm 6 phần (xem 5.3). |
| B. `agent_dispatch_logs` | `NEXUSHomeHub.Core.cs` | `CREATE TABLE IF NOT EXISTS`, đúng 8 cột như đặc tả, thêm `CHECK` cho `nexus_mode` và `status`. Bảng Phase 1 giữ nguyên. |
| C. 3 MCP tools | `NEXUSHome.McpTools/Program.cs` | `list_agent_squads` (Xanh), `dispatch_agent_squad` (Xanh), `update_dispatch_verdict` (Vàng, bắt buộc có `evidenceNotes`, từ chối id không tồn tại). Lỗi trả `isError: true`. Thêm: exception giờ trả về JSON-RPC `-32603` thay vì im lặng (trước đây client sẽ treo chờ). |
| Bảo mật | `AgentRegistry.Resolve` | Đường dẫn `.md` tương đối phải nằm trong một root sau `Path.GetFullPath`. Đường dẫn tuyệt đối và `..` thoát ra ngoài đều bị từ chối (E4). Hàm Phase 1 `LoadAgentSystemInstruction` (trước đây đọc được file bất kỳ) giờ cũng đi qua bộ kiểm tra này. |
| Hiệu năng | McpTools | Chỉ mục được nạp sẵn lúc khởi động, nên lần dispatch đầu không phải quét đĩa. Mỗi lần dispatch chỉ đọc 1 file `.md` và ghi 1 dòng vào SQLite. Thời gian `elapsedMs` có trong mỗi kết quả; script tính P95. |

### 5.2 Quyết định Gate 2 (§1 của TASK-03)
| # | Quyết định | Trạng thái |
|---|-----------|-----------|
| 1 | Named Pipe `NEXUS_Home_Cockpit_Pipe`, JSONL | ✅ Đổi tên. Nhận `{"cmd":"STATUS"}` → trả `{"ok":true,"cmd":"STATUS","result":"..."}`. Lệnh dạng chữ thường vẫn dùng được. |
| 2 | `SoftwareOnly` mặc định + tùy chọn `GpuPreference=1` | ✅ Đặt `RenderOptions.ProcessRenderMode` trong HostService (đặt `NEXUS_COCKPIT_RENDER=Hardware` để tắt). `GpuPreference=1` = ghi Registry = **Cấp Đỏ**, nên chỉ cung cấp script `tools/Set-CockpitGpuPreference.ps1` để Nhật Hòa tự chạy (có hỏi xác nhận), agent không tự chạy. |
| 3 | 3-Tier Fallback + `PerMonitorV2` | ✅ `app.manifest` PerMonitorV2 cho HostService. Định vị bằng pixel vật lý (`SetWindowPos`). Tầng 3 = màn chính, góc trên bên phải. |
| 4 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` ở chế độ HUD | ✅ Bật khi `Alt+Space` và lúc khởi tạo; tắt `NOACTIVATE` khi `Ctrl+Shift+Space` để ô lệnh nhận được bàn phím. Click chuột vào HUD giờ cũng không cướp focus. |
| 5 | "Giữ kiến trúc tách rời tiến trình (HostService là Supervisor của CockpitUI)" | ❌ **Chưa làm — hiện trạng không khớp quyết định.** `CockpitUI` đang là *thư viện* chạy **trong cùng tiến trình** `HostService` (xem `NEXUSHome.HostService.csproj` → `ProjectReference`). Chưa từng có kiến trúc tách tiến trình để "giữ". Tách ra là thay đổi kiến trúc lớn, ngoài phạm vi E5, và có thể phá ngân sách wake < 16 ms. Cần Architect xác nhận. |

### 5.3 Điểm diễn giải / sai khác so với đặc tả (minh bạch)
1. **Tên file hạt nhân trong đặc tả không có trong The Agency.** Đã dùng slug thật:

   | Đặc tả | Dùng | Lý do |
   |--------|------|-------|
   | `engineering/engineering-backend-architect.md` | giữ nguyên | có thật |
   | `testing_security/testing-reality-checker.md` | giữ nguyên | có thật |
   | `specialized/pm-sprint-prioritizer.md` | `specialized/product-sprint-prioritizer.md` | agent thật là `product/product-sprint-prioritizer` |
   | `game-development/unreal-systems-engineer.md` | giữ nguyên | có thật |
   | `specialized/agents-orchestrator.md` | giữ nguyên | có thật |
   | `specialized/finance-tool-evaluator.md` | `specialized/testing-tool-evaluator.md` | không có agent "finance-tool-evaluator"; chọn Tool Evaluator (đánh giá phần cứng/công cụ). Phương án khác: `support-finance-tracker` |
2. **QA agent mỗi squad**: đặc tả chỉ nêu QA cho Squad1. Mặc định dùng Reality Checker; riêng Squad3 dùng `testing-performance-benchmarker`, không có thì lùi về Reality Checker.
3. **"6-Part Agent Anatomy" không được định nghĩa trong đặc tả.** Đã diễn giải thành: 1 Identity (persona .md), 2 Mission, 3 Critical Rules (GEMINI.md), 4 Hardware & Environment, 5 Deliverables & QA, 6 Workflow (theo NEXUS mode).
4. **`evidenceNotes`**: schema đặc tả không có cột cho nó, nên lưu vào `audit_logs` kèm verdict, không thêm cột mới.
5. **Ổ `C:\` hay `D:\` — ĐÃ CHỐT**: máy Nhật Hòa **không có ổ `D:`** (`cd D:\` báo `DriveNotFound`); dự án nằm ở `C:\Agent\NEXUS_Home_Hub`, đúng như GEMINI.md. Script PowerShell ghi TASK-03 của Gemini dùng `D:\` nên chưa bao giờ ghi được file; đặc tả TASK-03 được Claude commit vào repo. `NexusPaths` vẫn tự dò thư mục gốc từ vị trí `.exe` (ghi đè được bằng `NEXUS_HOME_ROOT`, `NEXUS_VAULT_PATH`, `NEXUS_AGENTS_PATH`).
6. **Lưu ý hiệu năng của quyết định 2**: `SoftwareOnly` đẩy việc vẽ Cockpit sang **CPU** i5-12400, tức là dùng chung CPU với game. Card rời GTX 1660 không bị đụng, nhưng iGPU UHD 730 cũng không được dùng. Đề nghị đo CPU% của HostService khi Cockpit đang mở trong lúc chơi game (E5 chưa đo việc này).

### 5.4 Kiểm tay E6–E7 và sự cố Game Boost (2026-11-07, 12:35–12:44)
- **E6 focus**: (1) ✅ Đang trong Delta Force, `Alt+Space` → Cockpit hiện trên MSI G272, game vẫn mượt và nhận phím. (2) ✅ Click vào Cockpit → game vẫn bình thường. (3) ⏳ `Ctrl+Shift+Space` → ô `NEXUS>` nhận focus, có sẵn `@agent `; bước gõ `telemetry` + Enter chưa xác nhận.
- **Độ trễ wake**: `Alt+Space` (chế độ xem) **0,74–1,88 ms**; `Ctrl+Shift+Space` (chế độ gõ lệnh) **79,00 ms**, vượt ngân sách 16 ms. Giả thuyết: chuyển focus khỏi game fullscreen cộng với `SetWindowLong`/`SWP_FRAMECHANGED` khi bỏ `WS_EX_NOACTIVATE`. Chưa đo tách riêng từng phần.
- **E7 CPU/RAM**: Task Manager ghi `NEXUSHome.HostService` **0% CPU, 22,7 MB**, khi CPU toàn máy 63% (Delta Force đang chạy). Chưa xác định lúc chụp Cockpit đang hiện hay đang ẩn, nên cần chụp lại khi Cockpit hiện.
- **🔴 Sự cố Game Boost (lỗi có từ Phase 1, trong `LocalPcMcpTools.OptimizePcForGamingOrCoding`)**: khi click trúng công tắc "1. Game Boost (PUBG/Delta)" ở bước E6.2, Cockpit gọi hàm với target cố định `TslGame` (PUBG; Nhật Hòa không còn chơi). Hàm không tìm thấy target nhưng **vẫn gọi `EmptyWorkingSet` cho mọi tiến trình khác**, gồm cả Delta Force. Log: `Đã dọn dẹp Working Set RAM của 212 tiến trình nền`. Task Manager ngay sau đó: `DeltaForceClient-...` **"Not responding", Working Set 424 K** (trước đó khoảng 1,9 GB); Discord, csrss... về 0 K.
  - **Đã sửa**: target so khớp theo tiền tố tên tiến trình; **không thấy target thì không dọn RAM tiến trình nào**; target không bao giờ bị dọn; Cockpit dùng `NEXUS_GAME_PROCESS` (mặc định `DeltaForceClient`), nhãn đổi thành "Game Boost (Delta Force)"; audit log ghi đúng target. **Chưa build lại** sau bản sửa này.
  - **Vẫn còn (cần Architect quyết định)**: công tắc vẫn chạy một thao tác Cấp Vàng chỉ bằng một cú click. Với `WS_EX_NOACTIVATE`, click vào HUD không còn kích hoạt cửa sổ, nên dễ bấm trúng mà không hay. Ngoài ra, dọn Working Set của *toàn bộ* tiến trình nền sẽ gây page fault hàng loạt khi chúng hoạt động lại; nên cân nhắc giới hạn danh sách.

### 5.5 Sau bản sửa Game Boost (`812eb80`, 12:47–12:48)
- **Build lại + Verify-Task03**: ✅ **ALL PASS (E1–E5)**, build `-warnaserror` 0/0, P95 dispatch **1,885 ms** (đo khi Delta Force đang chạy).
- **HostService `--console`**: 4/4 hotkey `Registered`; `Target monitor: MSI G272 (\\.\DISPLAY6, match=FriendlyName)`; `HUD style: NOACTIVATE|TOOLWINDOW (exstyle=0x08080088)`; `RenderMode=SoftwareOnly`.
- **E6.3 gõ lệnh**: ✅ `Ctrl+Shift+Space` → gõ `telemetry` + Enter → Center HUD hiện JSON telemetry. Wake: chế độ xem **3,10 ms**; chế độ gõ lệnh **28,70 ms** (lần trước 79 ms), vẫn > 16 ms.
- **Game Boost với Delta Force**: log `Đã nâng PriorityClass=High cho 0 tiến trình 'DeltaForc...'`. Bản sửa nhận ra game đang chạy (không dọn RAM của game), nhưng **đặt `PriorityClass` thất bại** (exception bị nuốt). Giả thuyết mạnh: tiến trình được **ACE-Guard (anti-cheat)** bảo vệ, thấy trong Task Manager. Hệ quả: Game Boost chỉ còn tác dụng dọn RAM các app nền. Mở handle tới tiến trình game được anti-cheat bảo vệ là rủi ro nên tránh. **Khuyến nghị: tắt Game Boost cho game có anti-cheat**, cần Architect quyết định (xem gate3_handoff §3).
- **E7**: ✅ Task Manager lúc Cockpit hiện (12:48, sau lệnh `telemetry`): `NEXUSHome.HostService` **0% CPU, 36,4 MB**, CPU toàn máy 73% (Delta Force). Lúc ẩn (12:39): 0% / 22,7 MB. Kết luận: `SoftwareOnly` không tranh CPU với game ở mức đo được trong Task Manager.

---

## 6. TASK-04: Nối CockpitUI vào SquadDispatcher + Anti-Cheat Safe Game Boost — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-04-cockpit-dispatch` (tách từ `claude/task-03-agent-squad-dispatch`)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task04 ALL AUTOMATED CHECKS PASS (E1–E4, E6, R)** trên máy Nhật Hòa (2026-11-07 ~13:12, `C:\Agent\NEXUS_Home_Hub`, commit `e11813e`, build lần đầu). E5 kiểm tay ✅ (13:26). Bằng chứng gốc: `handoff/task04_verification.json`.

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0 Warning(s), 0 Error(s); không có `D:\NEXUS_Home_Hub` trong `.cs` |
| E2 | PASS | "Thiết kế C# Async TCP Server với Redis caching" → Squad1 / `engineering-backend-architect` / QA `testing-reality-checker`; **P95 3,002 ms** (ngân sách 10 ms, 20 lần, 0 lỗi); +20 dòng `agent_dispatch_logs`; `latest_dispatch.json` có, không BOM, hợp lệ; `/status`, `/mode sprint`, `/mode micro` chạy, lệnh lạ bị từ chối |
| E3 | PASS (chạy thử) | Không có Delta Force nên target = foreground (`claude`, pid 53308): shielded, không bị đụng, WS 134,8 → 134,8 MB. Tiến trình Whitelist đang chạy: `RobloxPlayerBeta`, `RobloxCrashHandler`, không bị đụng. 220 tiến trình nền là ứng viên trim; không có exception. Nhánh ACE-Guard (Delta Force) chưa được thử thật (`-LiveBoost` khi đang chơi). |
| E4 | PASS | HostService giữ read transaction WAL, 639 ghi + 639 đọc, **0 lỗi**; McpTools cùng lúc **20/20** `dispatch_agent_squad` thành công, 0 lỗi |
| E6 | PASS | 5 lượt `COCKPIT` qua pipe 5/5, mỗi câu đúng squad dự kiến (#167 Squad1, #168 Squad3, #169 Squad2, #170 Squad4, #171 Squad5); **private working set 51,9 MB** (ngân sách 60); `WorkingSet64` 152,7 MB; **CPU nền 0,00%** trong 3 s |
| R | PASS | Verify-Task03 E1–E5 ALL PASS (P95 dispatch 0,356 ms) |
| E5 | PASS (kiểm tay, 13:14–13:26) | `Ctrl+Shift+Space` → "Kiểm tra frame pacing Delta Force trên GTX 1660" → Enter: thẻ Active Squad trên MSI G272 hiện **Squad3** / Unreal Systems Engineer / QA Performance Benchmarker / Dispatch **#172** / **1,77 ms**; Recent Dispatches cập nhật; log `Enter: Passive HUD, focus trả về cửa sổ trước`. Nhật Hòa xác nhận bàn phím về game ngay sau Enter (bước 4 OK) và sau Esc (bước 5 OK). |

### 6.1 Đặc tả và GEMINI.md: nhận từ máy Nhật Hòa, đã sửa ký tự hỏng
- Đặc tả TASK-04 và `GEMINI.md` mới **không có trên GitHub**, chỉ nằm trong thư mục làm việc trên máy (chưa commit). Claude đọc qua kết nối desktop rồi commit vào nhánh.
- **Cả hai file bị hỏng ký tự**: script của Gemini dùng here-string PowerShell nháy kép `@" ... "@`, trong đó backtick của Markdown là ký tự escape. Hệ quả: `` `a `` → BEL, `` `f `` → form feed, `` `0 `` → NUL, `` `n `` → xuống dòng, `` `t `` → Tab, và mọi backtick bọc mã đều bị nuốt. Đã khôi phục:
  | Bị hỏng | Khôi phục |
  |---------|-----------|
  | `gent_dispatch_logs` | `agent_dispatch_logs` |
  | `exus_memory.db`, `exusMode` (sau xuống dòng) | `nexus_memory.db`, `nexusMode` |
  | ` 3_Local_Data_Vault` (NUL) | `03_Local_Data_Vault` |
  | `imestampUtc`, `askSummary` (sau Tab) | `timestampUtc`, `taskSummary` |
  | `ew UTF8Encoding` | `new UTF8Encoding` |
  | Whitelist `czf*`, `udiodg`, `vcontainer` | `fczf*` (FC Online), `audiodg`, `nvcontainer` |
  Backtick định dạng Markdown không khôi phục được (không biết vị trí gốc). Đã thêm vào GEMINI.md quy tắc dùng here-string nháy đơn `@' ... '@`.
- **GEMINI.md bản mới của Gemini đã bỏ mất**: chốt chặn 3 cấp Xanh/Vàng/Đỏ, cầu chì 3 lần thử, quy định giữ khóa API bằng DPAPI, phân vai (gồm Claude/`CLAUDE.md`), thiết bị ngoại vi và stack. Claude đã **giữ nguyên toàn bộ nội dung ADR mới và ghép lại các mục đó** (mục 4–6). Thêm 2 sửa nhỏ: bỏ "PUBG PC" khỏi danh sách game (Nhật Hòa đã nói không còn chơi; `TslGame*` vẫn ở Whitelist), và đánh dấu Registry IFEO PerfOptions là **Cấp Đỏ**. → Cần Architect xác nhận (gate4_handoff §3).

### 6.2 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| ADR-06 Game Boost | `NEXUSHome.Core/GameBoost.cs` (mới) | Shield: foreground PID (`GetForegroundWindow` → `GetWindowThreadProcessId`), target, chính HostService, Whitelist `DeltaForce* TslGame* fczf* Roblox* SGuard* ACE-* BEService*`, và hệ thống `audiodg dwm nvcontainer csrss winlogon fontdrvhost explorer`. **Target có anti-cheat không bao giờ bị mở với quyền ghi** (không thử `PriorityClass=High`); đi thẳng vào **Background Yield**: hạ Normal → BelowNormal cho tiến trình nền > 100 MB, ghi nhớ để khôi phục. Target không anti-cheat (ví dụ `devenv`) thì thử High, gặp Access Denied → Yield. Báo cáo: số tiến trình đã trim, **MB RAM thu hồi**, số tiến trình được yield, trạng thái target (`ACE-Guard Protected (Background Yield Active)` / `Elevated` / `NotRunning`). `LocalPcMcpTools.OptimizePcForGamingOrCoding` (MCP tool) giờ gọi GameBoost. |
| Khôi phục | `GameBoost.RestoreYield` | Lệnh `/boost off`, bỏ tick "Game Boost", và khi HostService thoát. *Ngoài đặc tả*: hạ ưu tiên mà không có đường khôi phục thì không thỏa luật Cấp Vàng (phải đảo ngược được). |
| §3.A Lệnh Cockpit | `NEXUSHome.Core/CockpitCommandProcessor.cs` (mới, không phụ thuộc UI) | `/boost [proc]`, `/boost off`, `/status` (`/telemetry`), `/mode micro\|sprint\|full`; câu tự do → `SquadDispatcher` cùng tiến trình (ADR-05) → `handoff/latest_dispatch.json` (ghi qua file tạm rồi đổi tên, UTF-8 không BOM, có `dispatchId, timestampUtc, taskSummary, nexusMode, squadId, primaryAgent, qaAgent, realityChecker, systemInstructionPreview`). Lệnh cũ `telemetry`, `hide`, `@agent ...` vẫn dùng được; lệnh cũ `tune` bị bỏ, thay bằng `/boost <proc>`. |
| §3.B Giao diện | `CockpitShellWindow.xaml(.cs)` | Thanh trên: `MODE \| HUD: PASSIVE/COMMAND \| RAM \| WAKE \| RENDER`; chữ gợi ý mờ trong ô lệnh; thẻ **Active Squad** (Squad, Primary, QA, Gate Reality Checker, Dispatch ID, Latency, Task); **Recent Dispatches** (5 dòng mới nhất từ `agent_dispatch_logs`, gồm cả dòng do McpTools ghi; làm mới mỗi lần hiện HUD và sau mỗi lệnh). Vẫn `SoftwareOnly`. |
| §2.3 Focus Restoration | `CockpitShellWindow.xaml.cs`, `HudWindowInterop.cs` | `Ctrl+Shift+Space` lưu HWND đang active trước khi lấy focus; Enter (sau khi chạy lệnh) hoặc Esc → về Passive HUD (`WS_EX_NOACTIVATE`) + `SetForegroundWindow(previous)`. **Thay đổi hành vi**: Esc trong chế độ lệnh không còn ẩn Cockpit (theo đặc tả); ẩn khẩn cấp vẫn là `Alt+Backspace`. `/boost` gõ từ ô lệnh bảo vệ cửa sổ trước đó (game), không phải Cockpit. |
| ADR-08 SQLite | `NEXUSHomeHub.Core.cs` | Mọi kết nối mở qua `OpenConnection()`: `PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;`. **Bỏ `Cache=Shared`**: shared-cache dùng khóa cấp bảng, lỗi `SQLITE_LOCKED` mà busy_timeout không thử lại. Thêm `GetRecentDispatches`. |
| ADR-07 Đường dẫn | `NexusPaths.cs` | Đã không còn `D:\NEXUS_Home_Hub` trong `.cs` (cả trong chú thích). Nhận thêm biến môi trường `NEXUS_ROOT` (tên theo ADR-07). Lớp vẫn tên `NexusPaths`; đặc tả gọi là "NexusPathResolver", không đổi tên để giữ Minimal Change. |
| Kiểm thử | `HostService/SelfTest.cs`, pipe `COCKPIT`, `tools/Verify-Task04.ps1` | `NEXUSHome.HostService.exe --selftest dispatch\|boost\|boost-live\|wal-stress` chạy không mở cửa sổ, ghi `task04_selftest_*.json`. Lệnh pipe `{"cmd":"COCKPIT","text":"..."}` chạy lệnh Cockpit trong HostService đang chạy (dùng cho E6). Đây là pipe điều khiển từ bên ngoài, không phải IPC giữa Cockpit và HostService (ADR-05 vẫn đúng: hai phần gọi nhau trực tiếp trong bộ nhớ). |

### 6.3 Diễn giải cần Architect xác nhận
1. **E6 "Working Set < 60 MB"**: có hai cách đo. Task Manager cột "Memory" là *private working set* (TASK-03 đo được 36,4 MB khi Cockpit hiện). `WorkingSet64` (gồm cả DLL dùng chung) là con số MFD Cockpit hiển thị, từng là 76–135 MB. Script **chấm PASS theo private working set** (con số Gemini dẫn ở Gate 3) và in cả `WorkingSet64` để đối chiếu.
2. **E3 mặc định chạy thử (dry run)**: `/boost` thật sẽ trim RAM mọi app nền và hạ ưu tiên app > 100 MB trên máy thật. Script mặc định chỉ lập kế hoạch rồi kiểm tra foreground và Whitelist không nằm trong danh sách bị đụng. `-LiveBoost` chạy thật rồi khôi phục ngay (nên chạy khi Delta Force đang mở để thử nhánh ACE-Guard).
3. **Background Yield nằm trong `/boost`**, không phải chế độ chạy nền liên tục: hạ ưu tiên một lần, khôi phục khi `/boost off`, bỏ tick, hoặc HostService thoát.

### 6.4 Ghi nhận sau kiểm tay (chưa sửa)
- Chế độ gõ lệnh (`Ctrl+Shift+Space`) bật mất **72,48 ms**. Bản thân lượt điều phối chỉ 1,77 ms, nên phần chậm nằm ở bước chuyển focus khỏi game (đã là điểm mở từ Gate 3).
- Thẻ Active Squad: chữ "Gate: Reality Checker" bị cắt do dòng quá dài.
- Thanh trạng thái hiện `RAM: 161 MB` = `WorkingSet64`, trong khi ngân sách E6 chấm theo private working set (51,9 MB). Hai con số dễ gây hiểu nhầm; nên hiển thị cả hai hoặc chọn một (gate4_handoff §3 điểm 2).
- Nhánh ACE-Guard của `/boost` (Delta Force đang chạy) **chưa được chạy thật**.

## 7. TASK-05: Đóng vòng Dev-QA từ CockpitUI, cầu chì 3 lần thử, đồng bộ MCP hai chiều — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-05-dev-qa-loop` (tách từ `claude/task-04-cockpit-dispatch` @`79715c5`)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task05 ALL AUTOMATED CHECKS PASS (E1–E5, E7)** ở lần chạy 2 (máy Nhật Hòa, 2026-11-07 13:57, commit `9ac0282`, Delta Force đã tắt) + **E6 kiểm tay PASS** (Nhật Hòa xác nhận). Lần chạy 1 (13:51, Delta Force + SGuard đang chạy) trượt E7 — §7.5, §7.6. Bằng chứng gốc: `handoff/task05_verification.json`.
- **Đặc tả**: `current_task.md` TASK-05 lấy từ máy Nhật Hòa qua kết nối desktop (**chưa có trên GitHub**, không nhánh nào chứa). Lần này không hỏng ký tự (Gemini đã dùng `@' ... '@`); file trên máy có BOM, Claude lưu lại **không BOM** theo ADR-16.

### 7.1 Phát hiện quan trọng: đặc tả §3.A không đủ để lưu `ESCALATED`
Bảng `agent_dispatch_logs` của TASK-03 có `CHECK (status IN ('DISPATCHED','NEEDS_WORK','PASSED'))`. Chỉ `ALTER TABLE ADD COLUMN` như đặc tả thì cột mới có, nhưng `UPDATE ... SET status='ESCALATED'` bị SQLite từ chối (`CHECK constraint failed`), và SQLite không sửa được CHECK bằng `ALTER`. Đã kiểm chứng bằng `sqlite3`.
**Bản vá** (`SqliteMemoryStore.MigrateDispatchLogs`): nếu CHECK chưa có `ESCALATED` → `VACUUM INTO nexus_memory.pre-task05-<UTC>.db` (bản sao lưu cạnh DB, bị `.gitignore` bỏ qua) → trong một transaction `IMMEDIATE`, kiểm tra lại, tạo bảng mới (schema TASK-05), `INSERT ... SELECT` **giữ nguyên id**, `DROP`, `RENAME`. Các trường hợp còn lại đi đúng đường `PRAGMA table_info` + `ALTER ADD COLUMN` của đặc tả.
Chạy thử trên bản sao 171 dòng: số dòng 171 → 171, `MAX(id)` 172, tổng id 14875 trước = sau; bản sao lưu đủ 171 dòng; id cấp tiếp theo 173 (không tái dùng id); lần khởi động thứ hai không dựng lại bảng.

### 7.2 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| §3.A Migration | `NEXUSHomeHub.Core.cs` | Như 7.1. `SqliteMemoryStore.MigrationReport` ghi lại kết quả (dựng lại hay không, cột thêm, số dòng trước/sau, đường dẫn bản sao lưu, lỗi). Dòng mới có `updated_at_utc` ngay khi `DISPATCHED`. |
| §3.B `UpdateVerdict` dùng chung | `NEXUSHomeHub.Core.cs` (`ApplyVerdict`), `DevQaLoop.cs` (mới: `DispatchVerdictService`, `LatestDispatchFile`) | Một câu `UPDATE ... RETURNING` duy nhất: `retry_count = retry_count + 1`, `status = CASE WHEN retry_count + 1 >= 3 THEN 'ESCALATED' ELSE 'NEEDS_WORK' END` — nguyên tử, an toàn khi Cockpit và McpTools cùng ghi. `PASSED` giữ `retry_count`. Id `null`/`<= 0` → bản ghi mới nhất. Dòng `### [3-STRIKE ESCALATION] Dispatch #<id> \| <updated_at_utc> \| Squad: … \| Task: … \| Evidence: …` được nối vào `qa_report.md` (UTF-8 không BOM) **đúng một lần**, lúc chuyển sang strike 3. Ghi audit log Cấp Vàng. Sau đó ghi đè `latest_dispatch.json` (file tạm + đổi tên) với đủ 10 trường đặc tả + `event`, `source`, `timestampUtc`, `squadName`, `realityChecker`. |
| §3.C.1 Lệnh mới | `CockpitCommandProcessor.cs` | `/pass [ghi chú]`, `/pass #<id> [ghi chú]`, `/fail [lý do]`, `/needswork …`, `/fail #<id> …`, `/verdict <id> <PASSED\|NEEDS_WORK> [ghi chú]` (`/verdict` nhận cả `12` lẫn `#12`; cũng nhận `PASS`/`FAIL`). |
| §3.C.2 Hiển thị | `CockpitShellWindow.xaml(.cs)` | Viền + tiêu đề thẻ Active Squad đổi màu `#38BDF8 / #4ADE80 / #FBBF24 / #F87171`; dòng trạng thái `[DISPATCHED]`, `[PASSED (Retries: 1)]`, `[NEEDS_WORK (Strike 2/3)]`, `[ESCALATED - 3 STRIKES!]`; dòng `Evidence:` (ẩn khi trống); nguồn (`CockpitUI`/`McpTools`). Recent Dispatches: mỗi dòng có nhãn trạng thái tô cùng màu (`Run` + `SolidColorBrush` đã `Freeze`, không hiệu ứng). Escalation: thẻ đỏ + dòng `⚠ [3-STRIKE ESCALATION]` trong log (không popup, không âm thanh, để không làm phiền khi đang chơi). |
| §3.C.3 Đồng bộ | `CockpitShellWindow.xaml.cs` | `FileSystemWatcher` chỉ theo dõi `latest_dispatch.json` (Changed/Created/**Renamed**, vì bên ghi thay file bằng đổi tên); debounce 150 ms bằng `DispatcherTimer`; đọc bằng `FileShare.ReadWrite \| Delete`; đọc hỏng thì thử lại một lần. Nạp lại thẻ + 5 dòng lịch sử. Không polling → CPU nền không tăng. Khi khởi động, thẻ hiện trạng thái cuối cùng trong file. Watcher được giải phóng khi đóng cửa sổ. |
| ADR-14 MCP | `NEXUSHome.McpTools/Program.cs` | `dispatch_agent_squad` ghi `latest_dispatch.json` (`source: McpTools`). `update_dispatch_verdict` dùng `DispatchVerdictService` (cùng cầu chì với Cockpit); vẫn bắt buộc `dispatchId > 0` và `evidenceNotes`; id không tồn tại → `isError`. Phản hồi giữ `"saved":true` (Verify-Task03 kiểm) và thêm `retryCount`, `escalated`, `escalatedNow`, `updatedAtUtc`. Đã xóa `UpdateDispatchStatus` cũ để không còn đường ghi verdict nào bỏ qua cầu chì. |
| §3.D.1 / ADR-10 < 35 ms | `HudWindowInterop.cs`, `CockpitShellWindow.xaml.cs` | Nguyên nhân khả dĩ của 72 ms ở Gate 4: mỗi lần bật/tắt `WS_EX_NOACTIVATE` đều gọi `SetWindowPos(SWP_FRAMECHANGED)`, buộc cửa sổ layered tính lại khung và vẽ lại toàn bộ bằng phần mềm. Giờ chỉ dùng `SWP_FRAMECHANGED` khi bit `WS_EX_TOOLWINDOW` đổi (một lần khi hiện lần đầu); bật/tắt `NOACTIVATE` chỉ gọi `SetWindowPos` thường. Đường găng chỉ còn đổi cờ + `Activate` + `Keyboard.Focus`; đọc SQLite/telemetry đưa vào `Dispatcher.InvokeAsync(..., Background)`. **Chưa đo** — giả thuyết sẽ được E5 xác nhận hoặc bác bỏ. |
| §3.D.2 / ADR-11 | `GameBoost.cs` | Tham số `simulateAccessDenied`: ném `Win32Exception(5)` ngay chỗ đặt `PriorityClass = High`, đi qua đúng khối `catch` thật → trạng thái `ACE-Guard Protected (Background Yield Active)`, `DeniedErrorCode = 5`. Chuỗi cũ `Priority Denied …` đã thay bằng trạng thái chuẩn này. |
| Kiểm thử | `HostService/SelfTest.cs`, `Program.cs`, `tools/Verify-Task05.ps1` | Selftest mới `schema`, `devqa-pass`, `devqa-strikes`, `boost-acedenied` (ghi `task05_selftest_*.json`). Pipe mới: `SYNCSTATE` (trạng thái Cockpit vừa nhận + độ trễ), `COMMANDMODE` (gọi đúng handler của `Ctrl+Shift+Space`), `PASSIVE` (như Esc). |

### 7.3 Cách `Verify-Task05.ps1` chấm từng mục
| # | Cách đo | PASS khi |
|---|---------|----------|
| E1 | `dotnet build -c Release -warnaserror` + selftest `schema`: đọc `pragma_table_info`, SQL của bảng, rồi `ATTACH` bản sao lưu `pre-task05` cũ nhất và đối chiếu từng dòng theo id + timestamp + squad + task | 0 lỗi/0 cảnh báo; đủ 3 cột; CHECK có `ESCALATED`; không thiếu dòng nào của bản sao lưu |
| E2 | selftest `devqa-pass`: dispatch từ `CockpitCommandProcessor` → `/pass Kiểm thử UDP đạt chuẩn` | SQLite + `latest_dispatch.json`: `PASSED`, evidence đúng nguyên văn, `retryCount 0`, JSON không BOM, đủ 10 trường; `/verdict … MAYBE` và `/pass #999999999` bị từ chối |
| E3 | selftest `devqa-strikes`: `/fail #id` ×3, rồi lần thứ 4; dispatch thứ hai `/needswork` → `/verdict #id PASSED` | 1: NEEDS_WORK r1, 2: NEEDS_WORK r2, 3: ESCALATED r3; đúng **một** dòng escalation (lần 4 không thêm); nhãn `PASSED (Retries: 1)` |
| E4 | McpTools tạo 3 dispatch, mỗi cái nhận `update_dispatch_verdict(NEEDS_WORK)` ở một phiên MCP riêng; script hỏi `SYNCSTATE` mỗi 20 ms | Cả 3: `"saved":true`, `latest_dispatch.json` có `source: McpTools`, Cockpit nhận đúng id + trạng thái; độ trễ lớn nhất < 300 ms |
| E5 | selftest `boost-acedenied` (chạy thử, không đổi tiến trình nào) + 6 lần `COMMANDMODE`/`PASSIVE` qua pipe | Trạng thái ACE-Guard, mã 5, không exception, không tiến trình Whitelist/foreground trong kế hoạch; max thời gian handler (bỏ lần đầu) < 35 ms |
| E6 | Kiểm tay (script in checklist) | Nhật Hòa xác nhận |
| E7 | HostService đang chạy: `WorkingSetPrivate` (CIM) + CPU trong 3 s; rồi `Verify-Task04 -SkipBuild` (đã gồm Verify-Task03) | private WS < 60 MB (ADR-09), CPU < 1 %, Task04 và Task03 `allPassed` |

### 7.4 Diễn giải cần Architect xác nhận
1. **Dựng lại bảng thay vì chỉ `ALTER`** (7.1): bắt buộc để lưu được `ESCALATED`. Có bản sao lưu trước khi dựng lại.
2. **Sau khi ESCALATED**: `/fail` tiếp tục tăng `retry_count` (4, 5…), trạng thái vẫn `ESCALATED`, **không** ghi thêm dòng báo cáo. `/pass` vẫn được phép (người quyết định cuối cùng ghi đè). Có muốn khóa dispatch sau escalation không?
3. **Timestamp trong dòng escalation** = `updated_at_utc` lúc chuyển sang strike 3 (không phải lúc dispatch).
4. **E3 ghi vào `qa_report.md`** — file git theo dõi. Script **khôi phục** `qa_report.md` sau E3 (dòng escalation vẫn nằm trong `task05_selftest_devqa-strikes.json`) để mỗi lần chạy kiểm thử không thêm rác vào repo. `-KeepQaReportEntry` để giữ dòng trong file.
5. **`/pass 200Hz ổn`**: số trần là ghi chú, không phải id. Muốn chỉ định id phải có `#` (`/pass #12 …`); riêng `/verdict` nhận cả số trần như đặc tả.
6. **Độ trễ E4** tính từ `updatedAtUtc` (lúc SQLite cập nhật) tới lúc Cockpit vẽ lại thẻ, **gồm cả 150 ms debounce** theo đặc tả. Do Cockpit tự đo, cùng đồng hồ máy.
7. **Độ trễ E5** đo cùng cách với con số 72 ms của Gate 4 (`LastWakeupLatencyMs`: từ lúc handler nhận lệnh tới khi đặt xong focus), nhưng điểm bắt đầu là lúc pipe gọi vào luồng UI thay vì lúc nhận `WM_HOTKEY`. Phím thật sẽ được Nhật Hòa kiểm ở E6 (thanh trạng thái hiện `WAKE: … ms`).
8. Thanh trạng thái vẫn hiện `WorkingSet64`; ADR-09 lấy private WS làm số chính thức. Chưa đổi (gate4 §3 điểm 6 vẫn mở).

### 7.5 Lần chạy 1 trên máy Nhật Hòa (13:51–13:52, commit `59f266d`) — E1–E5 PASS, E7 FAIL
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0 Warning(s), 0 Error(s). Migration chạy thật: **rebuilt=True**, 173 → 173 dòng, bản sao lưu `nexus_memory.pre-task05-20261107065132.db` 173 dòng, **0 dòng thiếu** trong bảng mới; CHECK có `ESCALATED` |
| E2 | PASS | Dispatch #174 Squad1 → `/pass Kiểm thử UDP đạt chuẩn`: SQLite và `latest_dispatch.json` đều `PASSED`, `retry 0`, evidence đúng nguyên văn; `/verdict … MAYBE` và id không tồn tại bị từ chối |
| E3 | PASS | #175: NEEDS_WORK r1 → NEEDS_WORK r2 → **ESCALATED r3**; đúng 1 dòng `### [3-STRIKE ESCALATION] Dispatch #175 \| 2026-11-07T06:51:34.2814569Z \| Squad: Squad1 \| …`; lần `/fail` thứ 4 không thêm dòng; `/needswork` + `/verdict` → `PASSED (Retries: 1)`. `qa_report.md` được khôi phục sau test |
| E4 | PASS | 3/3 lượt MCP `update_dispatch_verdict` → Cockpit nhận: **209,2 / 205,9 / 222,7 ms** (ngân sách 300, đã gồm debounce 150 ms) |
| E5 | PASS | Nhánh Access Denied: `ACE-Guard Protected (Background Yield Active)`, mã 5, không exception, không tiến trình Whitelist trong kế hoạch. Tiến trình anti-cheat thật đang chạy (`RobloxPlayerBeta`) được chặn theo tên, đúng trạng thái. **Chế độ lệnh: 113,69 (lần đầu) / 10,52 / 1,58 / 1,91 / 1,66 / 1,85 ms** — max (bỏ lần đầu) 10,52 ms, so với 72,48 ms ở Gate 4. Giả thuyết `SWP_FRAMECHANGED` được xác nhận |
| E7 | **FAIL** | Private WS **43,5 MB** (đạt), `WorkingSet64` 152,1 MB; **CPU nền 2,94 %** (trượt); Verify-Task03 PASS; **Verify-Task04 FAIL ở E2** |

**Phân tích 2 lỗi của E7 (chưa kết luận nguyên nhân P95):**
1. **Task04 E2: P95 13,738 ms, max 84,127 ms** (ngân sách 10; Gate 4 là 3,002 ms). Lúc chạy, **Delta Force + SGuard đang mở** (Task04 E3 liệt kê `DeltaForceClient-Win64-Shipping`, `SGuard64`, `SGuardSvc64`); ở Gate 4 thì không. Đường dispatch của TASK-05 khác TASK-04 ở hai chỗ: thêm cột `updated_at_utc` trong lệnh INSERT, và ghi `latest_dispatch.json` qua lớp mới (có thể thử lại sau 20 ms nếu file đang bị giữ). Chưa đủ dữ liệu để tách nguyên nhân môi trường (SGuard tốn CPU/ổ đĩa) khỏi nguyên nhân code. Verdict trong E2 cũng mất 14,99 ms.
2. **CPU nền 2,94 %** đo chỉ 2 s sau loạt lệnh E5. Khoảng 20 s sau, **cùng tiến trình HostService** (pid 78116) đo được **0,04 %** ở Task04 E6 → nhiều khả năng là đỉnh tạm thời (JIT tier-1 / GC / vẽ lại sau chế độ lệnh), không phải tải nền thường trực.

**Bản chẩn đoán đã thêm (chưa sửa logic):**
- `CockpitCommandResult.Phases`: thời gian từng bước của mỗi dispatch (định tuyến + INSERT SQLite / ghi `latest_dispatch.json` / audit log). Selftest `dispatch` ghi đủ 20 số đo, max từng bước và danh sách game/anti-cheat đang chạy.
- E7 đo CPU 15 mẫu × 1 s, chấm theo trung bình 5 giây cuối, lưu cả chuỗi (`e7CpuSeriesPct`) để thấy đỉnh có tắt dần hay không. Khi Task04 trượt, E7 in luôn mục trượt và thời gian từng bước.

### 7.6 Lần chạy 2 (13:57, commit `9ac0282`, Delta Force + SGuard đã tắt) — ALL PASS, E6 PASS
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | 0 Warning(s), 0 Error(s); bảng đã ở schema TASK-05 (rebuilt=False, 245 → 245 dòng); bản sao lưu `pre-task05` 173 dòng, **0 dòng thiếu** |
| E2 | PASS | #246 → `PASSED`, evidence `Kiểm thử UDP đạt chuẩn` trong SQLite và `latest_dispatch.json` |
| E3 | PASS | #247: NEEDS_WORK r1 → r2 → ESCALATED r3; 1 dòng escalation; `PASSED (Retries: 1)` |
| E4 | PASS | MCP → Cockpit **188,4 / 178,0 / 171,6 ms** (ngân sách 300) |
| E5 | PASS | ACE-Guard, mã 5, không exception; chế độ lệnh **47,66 (lần đầu) / 1,82 / 2,01 / 1,91 / 1,74 / 1,72 ms**, max (bỏ lần đầu) **2,01 ms** (ngân sách 35; Gate 4: 72,48 ms) |
| E6 | PASS (kiểm tay) | Nhật Hòa xác nhận: dispatch trong game → thẻ xanh dương `[DISPATCHED]`, `/fail thiếu log` → thẻ vàng `NEEDS_WORK (Strike 1/3)` + Evidence, `/pass đã sửa` → thẻ xanh lá `PASSED (Retries: 1)`; focus về game sau mỗi Enter |
| E7 | PASS | Private WS **49,4 MB** (ngân sách 60), `WorkingSet64` 157,6 MB; CPU nền **0,57 %** (trung bình 5 s cuối; chuỗi 15 s: 3,21 · 0,39 · 0,13 · 0,39 · 0 · 0 · 3,73 · 0 · 0,39 · 0,13 · 0,13 · 0,9 · 0,13 · 0,39 · 1,29 %); Verify-Task04 ALL PASS (**P95 dispatch 2,316 ms**, E6 CPU 0,00 %), Verify-Task03 ALL PASS |

**Kết luận về lỗi E7 của lần chạy 1:**
- Đường dispatch của TASK-05 khi máy rảnh: 19/20 lượt 1,29–2,32 ms. Chỉ lượt **đầu tiên** chậm (47,5 ms; theo từng bước: định tuyến + INSERT 15,2 ms, ghi `latest_dispatch.json` 29,5 ms, audit 2,1 ms) — chi phí khởi động lạnh (JIT, lần đầu tạo file), P95 bỏ qua được.
- Lần chạy 1 có P95 13,7 ms **khi Delta Force + SGuard đang chạy**; lần chạy 2 chỉ khác là game đã tắt (chỉ còn Roblox). Điều này **khớp** với nguyên nhân môi trường (SGuard chiếm CPU/ổ đĩa), nhưng chưa phải bằng chứng trực tiếp: chưa có số đo từng bước khi game đang chạy. Câu hỏi cho Architect: ngân sách 10 ms áp dụng khi máy rảnh hay cả khi đang chơi (gate5_handoff §3 điểm 0).
- CPU 2,94 % ở lần 1 là đỉnh tạm thời; chuỗi 15 s ở lần 2 cho thấy nền ~0,1–0,4 % với **đỉnh định kỳ khoảng mỗi 6 s** (3,21 % và 3,73 %). Chưa xác định nguồn (có thể là timer làm mới của MFD); ghi lại để xem ở TASK sau.


## 8. TASK-06: Hợp nhất nhánh về `main`, `nexus_config.json` + `/reload`, dọn nợ kỹ thuật — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-06-config-consolidation` (tách từ `claude/task-05-dev-qa-loop` @`6255b7f`)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task06 ALL CHECKS PASS (E1–E5)** ở lần chạy 2 (máy Nhật Hòa, 2026-11-07 20:42, commit `a812742`, `-SkipBuild`; build `-warnaserror` 0/0 ở lần chạy 1 với cùng code — lần 2 chỉ khác `.gitignore` và tài liệu). Lần chạy 1 trượt E5 vì `git status` (§8.5). Sau Gate 6, `main` được fast-forward tới đầu nhánh TASK-06 (§8.6).
- **Đặc tả**: `current_task.md` TASK-06 lấy từ máy Nhật Hòa (Gemini chưa commit; file sạch, không BOM, không ký tự hỏng). File Gemini gửi qua chat lần đầu bị cắt ở dòng 59 (giữa §3.A), bản trên máy thì đủ.

### 8.1 Hợp nhất nhánh (ADR-22)
- Chuỗi nhánh tuyến tính: `main`(`ed67341`) ⊂ task-02 ⊂ task-03 ⊂ task-04 ⊂ task-05 (17 commit), không có merge commit, không xung đột.
- **Đã fast-forward `main` → `6255b7f`** (đầu `claude/task-05-dev-qa-loop`, Gate 2–5 đều PASSED). `main` giờ chứa toàn bộ code, `Verify-Task03..05.ps1`, `GEMINI.md`, `qa_report.md`, `gate2..5_handoff.md`.
- TASK-06 làm trên nhánh riêng (luật CLAUDE.md). Sau khi Gate 6 PASS trên máy Nhật Hòa, `main` sẽ được fast-forward tới đầu `claude/task-06-config-consolidation` (E5 kiểm tra nhánh này đúng là `main` + commit mới, tức fast-forward được).
- `.gitignore` thêm: `handoff/task0*_selftest_*.json`, `task0*_verification.json`, `latest_dispatch.json`, `*.tmp`, `config/nexus_config.json` và `config/nexus_config.json.*`. Lý do: các file này do kiểm thử/vận hành sinh ra trên máy; trước đây chúng làm `git status` không bao giờ sạch (yêu cầu §3.1).

### 8.2 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| §3.2 / ADR-20 Cấu hình | `NEXUSHome.Core/NexusConfig.cs` (mới) | `NexusConfig` (9 trường đặc tả + `combatProcessPrefixes`, xem 8.4), `NexusConfigService`. Thiếu thư mục/file → tạo với giá trị mặc định đúng đặc tả. **JSON hỏng** → không áp dụng gì, giữ cấu hình hợp lệ gần nhất, sao file hỏng ra `nexus_config.json.invalid-<UTC>`, ghi lại file chuẩn từ cấu hình đang dùng. **Trường thiếu / rỗng / sai kiểu / ngoài khoảng** → riêng trường đó giữ giá trị cũ (kèm cảnh báo), các trường khác vẫn áp dụng. File có BOM → nhận, rồi ghi lại không BOM. Chấp nhận chú thích `//` và dấu phẩy thừa (người dùng sửa tay). Không bao giờ ném ngoại lệ. `Fingerprint` = SHA-256 rút gọn của cấu hình đang áp dụng. |
| `/reload`, `/config` | `CockpitCommandProcessor.cs`, `CockpitShellWindow.xaml.cs` | `/reload`: nạp lại; áp dụng ngay `defaultNexusMode`, `memoryTrimFloorMb` + `protectedProcessPrefixes` (GameBoost), `watcherDebounceMs` (timer đồng bộ), `preferredMonitorKeywords` (định vị lại Cockpit); thông báo trên ô log; ghi audit log. JSON hỏng → thông báo `[CONFIG] LỖI … giữ cấu hình hợp lệ gần nhất … đã ghi lại file chuẩn`. `/config`: in cấu hình đang áp dụng, fingerprint, hồ sơ SLA hiện tại. |
| Áp dụng cấu hình | `GameBoost.cs`, `MonitorLocator.cs`, `AulaHero68HotkeyListener.cs` | GameBoost: danh sách bảo vệ lấy từ config; **không dọn RAM tiến trình ≤ `memoryTrimFloorMb`** (trước đây dọn mọi tiến trình nền). Danh sách hệ thống cứng (`audiodg dwm nvcontainer csrss winlogon fontdrvhost explorer System Idle`) vẫn là "sàn" config không gỡ được. Màn hình: tên EDID phải chứa **tất cả** từ khóa (`MSI` và `G272`); biến môi trường `NEXUS_COCKPIT_MONITOR` vẫn ghi đè như cũ. Phím tắt: đọc `passiveHudHotkey` / `commandModeHotkey` khi HostService khởi động (cú pháp `Alt+Space`, `Ctrl+Shift+Space`, `Win+F9`…, bắt buộc có Alt/Ctrl/Win; sai → dùng mặc định); đổi phím cần khởi động lại HostService. |
| §3.3.1 / ADR-21 Dùng chung | `NEXUSHome.Core/NexusRuntime.cs` (mới), cả hai `Program.cs`, `SelfTest.cs` | Lõi đã nằm trong project chung `NEXUSHome.Core` từ TASK-03, nhưng phần khởi tạo (đường dẫn vault, DB, registry, dispatcher, verdict service, `latest_dispatch.json`) bị viết lặp ở `HostService/Program.cs`, `McpTools/Program.cs`, `CockpitShellWindow` và 4 chỗ trong `SelfTest`. Giờ tất cả đi qua `NexusRuntime.Create(source)`. Thêm `AtomicFile` dùng chung cho `latest_dispatch.json` và `nexus_config.json`. McpTools: lệnh dispatch không có `nexusMode` → dùng `defaultNexusMode` của config; tool mới **`get_nexus_config`** (Cấp Xanh) trả cấu hình + fingerprint + hồ sơ SLA. |
| §3.3.2 / ADR-19 | `NEXUSHomeHub.Core.cs` (`ApplyVerdict`), `DevQaLoop.cs` | `ApplyVerdict` đọc trạng thái cũ rồi cập nhật trong một transaction `BEGIN IMMEDIATE`, trả `(row, previousStatus)`. Dòng escalation chỉ ghi khi **chuyển từ trạng thái khác sang ESCALATED** và `qa_report.md` **chưa có dòng nào cho dispatch đó**. `/pass` từ ESCALATED → PASSED giữ `retry_count`; `/fail` từ PASSED → NEEDS_WORK, hoặc ESCALATED nếu `retry_count` đạt 3. |
| §3.3.3 / ADR-17 | `NexusConfigService.DetectSla`, `SelfTest.cs`, `Verify-Task04/05/06.ps1` | Có tiến trình khớp `combatProcessPrefixes` (mặc định `DeltaForce`, `TslGame`, `SGuard`) → hồ sơ `LiveCombat`, ngân sách `dispatchSlaCombatMs` (25 ms); ngược lại `Desktop`, `dispatchSlaDesktopMs` (10 ms). Selftest `dispatch` (Task04 E2) tự chọn ngân sách và ghi `slaProfile`, `budgetMs`, `slaMatchedProcesses`; script in ra hồ sơ đã áp dụng. |
| Kiểm thử | `SelfTest.cs`, `tools/Verify-Task06.ps1` | Selftest mới `config`, `adr19`, `sla` (ghi `task06_selftest_*.json`). Script E1–E5 như đặc tả §4 (chi tiết đầu file script). |

### 8.3 Chạy thử ADR-19 trên SQLite (Python `sqlite3`, cùng SQL)
| Bước | status / retry | Ghi dòng escalation? | Số dòng cho id |
|------|----------------|----------------------|----------------|
| `/fail` 1, 2 | NEEDS_WORK 1, 2 | không | 0 |
| `/fail` 3 | **ESCALATED 3** | **có** | 1 |
| `/fail` 4 | ESCALATED 4, evidence cập nhật | không | 1 |
| `/pass Đã khắc phục xong` | **PASSED 4** | — | 1 |
| `/fail` sau PASSED | ESCALATED 5 | không (đã có dòng) | 1 |
| Dispatch 2: fail → pass → fail | NEEDS_WORK 1 → PASSED 1 → **NEEDS_WORK 2** | không | 0 |
| id không tồn tại | `null` (báo lỗi) | — | — |

### 8.4 Diễn giải cần Architect xác nhận
1. **Thêm trường `combatProcessPrefixes`** (không bắt buộc, mặc định `["DeltaForce","TslGame","SGuard"]` đúng ADR-17). Không dùng `protectedProcessPrefixes` để nhận diện "đang chơi" vì danh sách đó có `explorer`, `dwm`, `csrss`, `NEXUSHome` luôn chạy, máy sẽ luôn ở chế độ Combat. `Roblox` không nằm trong danh sách Combat (máy Nhật Hòa thường để Roblox chạy nền).
2. **`nexus_config.json` không đưa vào git** (mỗi máy tự tạo với giá trị mặc định; giá trị mặc định nằm trong code). Bản sửa của người dùng không bị `git pull` ghi đè.
3. **`memoryTrimFloorMb` = ngưỡng dưới của việc dọn RAM**: tiến trình nền có Working Set ≤ ngưỡng không bị `EmptyWorkingSet` (đặc tả gọi là "ngưỡng dọn RAM", không nói rõ chiều).
4. **Danh sách hệ thống cứng** vẫn được bảo vệ kể cả khi người dùng xóa khỏi `protectedProcessPrefixes`.
5. **Phím tắt không đổi bằng `/reload`** (spec chỉ liệt kê 5 tham số cho `/reload`); `/config` ghi rõ "áp dụng khi khởi động lại HostService".
6. **Dòng escalation thứ hai cho cùng dispatch**: đặc tả vừa nói "chỉ ghi khi chuyển sang ESCALATED", vừa nói "tuyệt đối không ghi dòng thứ hai cho cùng dispatchId". Trường hợp mở lại một dispatch PASSED đã có ≥ 3 strike thì hai câu mâu thuẫn; Claude chọn **không ghi dòng thứ hai** (kiểm tra `qa_report.md`).
7. **E2 chạy thật trên file cấu hình thật** của HostService đang chạy (sửa → `/reload` → JSON hỏng → `/reload`), rồi **khôi phục nguyên từng byte** file gốc và `/reload` lại. Các bản `nexus_config.json.invalid-*` do kiểm thử tạo ra được giữ lại (không tự xóa file).
8. **Selftest `config`** chạy trong thư mục riêng `%TEMP%\nexus_task06_config_test\run-<giờ>-<pid>` cho mỗi lần chạy (không xóa, mỗi lần vài KB).

### 8.5 Lần chạy 1 trên máy Nhật Hòa (20:39, commit `e961de2`) — E1–E4 PASS, E5 FAIL (git status)
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0 Warning(s), 0 Error(s); không BOM: `GEMINI.md`, `current_task.md`, `nexus_config.json`, `latest_dispatch.json` |
| E2 | PASS | Riêng biệt: tự tạo (không BOM, đủ trường), 50→64/Sprint áp dụng, JSON hỏng (giữ cấu hình cũ, có bản sao lưu, file ghi lại floor=64), trường rỗng, file có BOM, 5/5 phím tắt. **Thật trên Cockpit đang chạy**: `/reload` áp dụng floor=64, mode=Sprint; JSON hỏng → `/reload` báo lỗi, HostService vẫn sống, giữ 64, file chuẩn được ghi lại, +1 bản sao lưu; file gốc khôi phục + `/reload` OK. `nexus_config.json` chưa tồn tại trước lần chạy → được tạo tự động |
| E3 | PASS | #318: r1, r2 → **ESCALATED r3 (1 dòng)** → fail 4: ESCALATED r4 (vẫn 1 dòng) → `/pass`: **PASSED r4** → mở lại: ESCALATED r5 (vẫn 1 dòng); #319: PASSED → `/fail` → NEEDS_WORK r2. `qa_report.md` được khôi phục |
| E4 | PASS | Hai project tham chiếu Core, khởi tạo qua `NexusRuntime`, không định nghĩa lại kiểu lõi. Fingerprint cấu hình **HostService = Cockpit đang chạy = McpTools = `1bb8041a2925`**; SLA cả 3 phía + script: Desktop/10 ms (không có game chạy) |
| E5 | **FAIL** | task-02…05 đều nằm trong `origin/main` ✅; nhánh TASK-06 = `main` + commit mới (fast-forward được) ✅; Verify-Task05/04/03 ALL PASS ✅ (private WS 44,3 MB, CPU nền 0,10 %); **`git status` không sạch** ❌: 6 file chưa theo dõi trong `02_Agents_Registry/` |

**Nguyên nhân E5**: 6 file đó là 5 agent trưởng squad + Reality Checker mà McpTools tự chép từ kho kỹ năng cục bộ vào `02_Agents_Registry` khi chưa có (TASK-03 §A.1, `SquadDispatcher.CoreAgentSeeds`). Chúng tồn tại từ TASK-03, nhưng trước đây không script nào kiểm tra `git status`. **Bản vá**: `.gitignore` đúng 6 đường dẫn đó (không bỏ qua cả thư mục, để agent tự thêm vẫn được git theo dõi). Không đổi code.

### 8.6 Lần chạy 2 (20:42, commit `a812742`, `-SkipBuild`) — ALL PASS
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | Không BOM ở cả 4 file (build 0/0 từ lần 1, code không đổi) |
| E2 | PASS | Như lần 1: riêng biệt đủ 6 nhóm; thật trên Cockpit: `/reload` floor 64 / Sprint, JSON hỏng → HostService sống, giữ cấu hình cũ, file chuẩn ghi lại, +1 bản sao lưu; file gốc khôi phục |
| E3 | PASS | #392: r1, r2 → ESCALATED r3 (1 dòng) → r4 (1 dòng) → PASSED r4 → r5 (1 dòng); dispatch 2 → NEEDS_WORK r2 |
| E4 | PASS | Fingerprint `1bb8041a2925` ở HostService, Cockpit đang chạy, McpTools; SLA Desktop/10 ms ở cả 3 + script |
| E5 | PASS | task-02…05 trong `origin/main`; TASK-06 fast-forward được; **`git status` sạch**; Verify-Task05/04/03 ALL PASS — P95 dispatch **2,966 ms** (Desktop, ngân sách 10), private WS **44,1 MB**, CPU nền **0,13 %** |

**Chưa được kiểm thật**: hồ sơ LiveCombat (25 ms) — cả hai lần chạy đều không có Delta Force/SGuard, nên chỉ nhánh Desktop được đo. Logic chọn hồ sơ dùng chung một hàm ở HostService, McpTools và (cùng quy tắc) script; nên chạy lại Verify-Task04 khi đang mở Delta Force để có số đo LiveCombat.

