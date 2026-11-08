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


## 9. TASK-07: Nối Gemini API vào CockpitUI & McpTools, khóa API bằng Windows DPAPI — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-07-gemini-dpapi` (tách từ `main` @`35926f0`, sau tag `v0.6.0`)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task07 ALL CHECKS PASS (E1–E5)** trên máy Nhật Hòa (2026-11-07 22:29, commit `f9a173e`, có build). Phần gọi Gemini thật ở E5 **SKIPPED** (chưa cài khóa) — §9.3.
- **Đặc tả**: `current_task.md` TASK-07 lấy từ máy Nhật Hòa (sạch, không BOM). **Lại chưa có trên GitHub**: lời nhắn nói đã commit nhưng `main` vẫn là TASK-06. Tag `v0.6.0` có trên GitHub (trỏ `a812742`, trước commit tài liệu Gate 6 `35926f0`).

### 9.1 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| §3.1 Kho khóa DPAPI | `NEXUSHome.Core/DpapiSecretVault.cs` (mới); gói `System.Security.Cryptography.ProtectedData` 8.0.0 | `ProtectedData.Protect/Unprotect`, `DataProtectionScope.CurrentUser`, entropy cố định `NEXUS_HOME_HUB_DPAPI_V1`, ghi `config/gemini_key.dpapi` qua file tạm + đổi tên; xóa mảng byte bản rõ sau khi dùng. `Resolve()`: DPAPI → `GEMINI_API_KEY` → `Missing`, không ném lỗi (file của tài khoản/máy khác → cảnh báo rồi thử ENV). `MaskApiKey`: `AIza...9xQ (39 chars, Source: DPAPI)`. Mọi lời gọi DPAPI có `OperatingSystem.IsWindows()` để analyzer CA1416 không chặn `-warnaserror` (Core là `net8.0`). |
| `/apikey set\|status\|clear` | `CockpitCommandProcessor.cs`, `CockpitShellWindow.xaml.cs` | `set`: mã hóa, báo kèm chuỗi đã che; audit chỉ ghi `{"action":"set","length":N}`; ô lệnh đã được xóa ngay khi Enter (có từ TASK-04); dòng echo trên HUD hiện `/apikey set ••••••`. `status`: `Configured via DPAPI` / `Configured via ENV` / `Not Configured` + model (+ cảnh báo nếu bật mock). `clear`: xóa `gemini_key.dpapi` (báo nếu ENV vẫn còn khóa). |
| Zero-Secret-Leak (thêm) | `SquadDispatcher.Dispatch`, `SqliteMemoryStore.WriteAuditLog`, `CockpitCommandProcessor`, `GeminiAgentClient` | Chuỗi dạng khóa Google (`AIza…`) trong câu lệnh thường / `/ask` / MCP dispatch bị **từ chối, không lưu** (không vào `task_summary`); `audit_logs.arguments_json` luôn được che; mọi thông báo lỗi Gemini được lọc bỏ khóa. Khóa chỉ đi trong header `x-goog-api-key`, không bao giờ trên URL. |
| §3.2 Config | `NexusConfig.cs` | `geminiModel` (`gemini-2.5-flash`, chỉ chữ/số/`.-_` vì nằm trong URL), `geminiTimeoutSeconds` 1..120 (25), `geminiMaxOutputTokens` 1..8192 (1024), `geminiTemperature` 0..2 (0,3). Không bắt buộc: file TASK-06 tự nhận mặc định, không cảnh báo. `/config` in thêm dòng gemini. |
| §3.2 Schema | `NEXUSHomeHub.Core.cs` | `ai_response`, `ai_model`, `ai_latency_ms` qua `ALTER TABLE ADD COLUMN` idempotent (cùng cơ chế TASK-05); nhánh dựng lại bảng TASK-05 giữ danh sách 11 cột cũ, cột mới lấy mặc định. `SaveAiResponse` (UPDATE … RETURNING, không đổi `status`). `latest_dispatch.json` thêm `aiResponse`, `aiModel`, `aiLatencyMs`. |
| §3.3 Gemini client | `NEXUSHome.Core/GeminiAgentClient.cs` (mới) | `POST v1beta/models/{model}:generateContent`, body `system_instruction` (SystemInstruction 6 phần của Primary Agent — đã có persona + phần cứng — cộng mục 7 "HUD Answer Format": trả lời theo ngôn ngữ người hỏi, ~200 từ, không gợi ý tắt anti-cheat), `contents`, `generationConfig`. Một `HttpClient` dùng chung, timeout theo `geminiTimeoutSeconds`. Lỗi → văn bản chẩn đoán: `[GEMINI API ERROR HTTP 4xx/5xx] …` (gợi ý riêng cho 400/401/403/404/429/5xx), `[GEMINI TIMEOUT]`, `[GEMINI NETWORK ERROR]`, `[GEMINI BLOCKED]`, `[GEMINI EMPTY]`; `MAX_TOKENS` thì ghi chú bị cắt. **Mock**: `NEXUS_GEMINI_MOCK_RESPONSE` → không gọi mạng, `aiModel = gemini-mock-verifier`, nội dung có tên Primary Agent và SquadId. |
| §2.3 / §3.4 Luồng `/ask` | `AgentConsultService` (trong `GeminiAgentClient.cs`), `CockpitCommandProcessor.ExecuteAsk`, `CockpitShellWindow` | **Đồng bộ**: định tuyến + dòng `DISPATCHED` + `latest_dispatch.json` (event `ask`) → thẻ hiện `[THINKING... Calling Gemini]` (màu tím, chỉ là nhãn giao diện, `status` trong DB vẫn `DISPATCHED`) → Enter trả focus về game ngay (đã có từ TASK-04). Đọc SQLite để làm mới feed/telemetry giờ chạy ở mức Background sau khi trả focus. **Nền** (`Task.Run`): lấy khóa, gọi Gemini, lưu `ai_*`, ghi `latest_dispatch.json` (event `ai_response`), rồi luồng UI hiện câu trả lời trong khung **AI RESPONSE** mới (cuộn được, tối đa 150 px, `SoftwareOnly`). Câu tự nhiên không có `/ask` vẫn điều phối cục bộ như cũ. |
| MCP `consult_gemini_agent` | `NEXUSHome.McpTools/Program.cs` | Cấp Xanh; `taskDescription` (bắt buộc), `nexusMode`, `preferredAgentId`; cùng `AgentConsultService` (ghi event `ask` trước để Cockpit hiện THINKING, rồi kết quả cuối); trả Squad + `aiResponse`, `aiModel`, `aiLatencyMs`, `keySource`. Đây là tool thứ 9 của server (đặc tả gọi là "tool thứ 5"). |
| Kiểm thử | `SelfTest.cs`, `tools/Verify-Task07.ps1` | Selftest `schema07`, `vault`, `ask`, `ask-missingkey`, `ask-live` (`task07_selftest_*.json`; khóa thử không bao giờ được ghi vào báo cáo). Script E1–E5 như đặc tả §4, thêm quét rò rỉ khóa thử trong SQLite (db/wal/shm), `latest_dispatch.json`, log, `qa_report.md` và báo cáo selftest. |

### 9.2 Diễn giải cần Architect xác nhận
1. **Mock thắng khóa**: khi `NEXUS_GEMINI_MOCK_RESPONSE` được đặt, không cần khóa và không gọi mạng. Script chỉ đặt biến này cho tiến trình con của nó, gỡ ngay sau E3, và khởi động HostService chạy lâu dài **trước** khi đặt biến (để `/ask` thật trong Cockpit không bao giờ bị mock).
2. **Ngân sách phần đồng bộ của `/ask`**: 5 ms (Desktop) như đặc tả; khi LiveCombat dùng `dispatchSlaCombatMs` (25 ms) theo tinh thần ADR-17. Đo 5 lần "ấm" sau lần đầu (lần đầu có JIT).
3. **E5 khi chưa có khóa**: phần gọi thật được đánh dấu **SKIPPED** (không tính trượt); hồi quy vẫn bắt buộc PASS. Có khóa mà gọi lỗi (sai khóa, hết quota, mất mạng) thì E5 **trượt** (để lộ ra vấn đề thật).
4. **`updated_at_utc` đổi khi lưu câu trả lời AI** (để Cockpit tính độ trễ đồng bộ và thứ tự sự kiện đúng). Không ảnh hưởng dòng escalation (chỉ ghi khi chuyển trạng thái).
5. **Chặn chuỗi giống API key ở mọi đường điều phối** (Cockpit, `/ask`, MCP `dispatch_agent_squad`/`consult_gemini_agent`): thêm ngoài đặc tả, theo kỷ luật Zero-Secret-Leak.
6. **Chưa kiểm**: lỗi HTTP/timeout thật (chỉ có đường thiếu khóa được kiểm tự động ở E4); nên thử tay bằng khóa sai sau khi build (kỳ vọng `[GEMINI API ERROR HTTP 400 …]`, ứng dụng không văng).

### 9.3 Lần chạy trên máy Nhật Hòa (22:29, commit `f9a173e`) — ALL PASS, gọi thật SKIPPED
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0 Warning(s), 0 Error(s) (gói ProtectedData tải được, CA1416 không chặn); migration thật: thêm `ai_response, ai_model, ai_latency_ms`, **465 → 465 dòng** |
| E2 | PASS | `gemini_key.dpapi` 262 byte, **không có bản rõ** (UTF-8 lẫn UTF-16), giải mã khớp, mask `AIza...456 (39 chars, Source: DPAPI)`; `/apikey set/status/clear` đúng, dán khóa vào câu lệnh bị chặn, thông báo sạch; **quét rò rỉ sạch** ở `nexus_memory.db`, `-wal`, `-shm`, `latest_dispatch.json`, `hostservice.log`, `mcp_debug.log`, `qa_report.md`, báo cáo selftest; file khóa bị git bỏ qua; trước đó chưa có khóa thật → không để lại khóa thử |
| E3 | PASS | `/ask` #466 → Squad1 / Backend Architect; câu trả lời mock lưu trong SQLite và `latest_dispatch.json`; phần đồng bộ (5 lần ấm) **1,53–2,36 ms** (ngân sách 5); MCP `consult_gemini_agent` #472 → Squad1, `gemini-mock-verifier`, `latest_dispatch.json` source McpTools, **Cockpit đang chạy nhận được** |
| E4 | PASS | `/apikey status` → `Not Configured`; `/ask` #473 → `DISPATCHED`, không exception, `ai_response = [LOCAL DISPATCH ONLY] Chưa cấu hình Gemini API Key…` |
| E5 | PASS | Gọi thật **SKIPPED** (chưa có khóa). Hồi quy Verify-Task06/05/04/03 ALL PASS: P95 dispatch 2,585 ms (Desktop), config fingerprint trùng ở 3 tiến trình, CPU nền 0,05 %, private WS **58,6 MB** (Task05 E7) / **53 MB** (Task04 E6, khoảng 1 phút sau, cùng tiến trình) |

**Ghi nhận (chưa phải lỗi, đề xuất xử lý ở TASK-08):**
1. **Private WS 58,6 MB sát ngân sách 60.** Cùng tiến trình đo 53 MB khoảng một phút sau (Gate 5–6: 52,7–53 MB ở cùng điểm đo Task04 E6), nên đây là đỉnh tạm thời trước khi GC thu hồi, không phải mức nền tăng. Hai chỗ có thể giảm: `AgentConsultService` tạo `GeminiAgentClient` ngay khi khởi động, nên `HttpClient` + `SocketsHttpHandler` tĩnh được tạo trong mọi tiến trình (nên tạo khi cần); `GoogleApiKeyPattern` dùng `RegexOptions.Compiled` (sinh mã động; với chuỗi ngắn không cần).
2. **Lần `/ask` đầu tiên trong selftest E3 mất 2,9 s ở phần đồng bộ** (E4 lần đầu: 72 ms; các lần ấm 1,5–2,4 ms). Xảy ra ngay sau khi HostService chạy lâu dài vừa khởi động. Chưa có số đo từng bước cho `/ask` nên chưa rõ nguyên nhân (tranh chấp khóa SQLite lúc HostService khởi động, quét của phần mềm diệt virus khi ghi file lần đầu, hay JIT). Đề xuất thêm đo từng bước cho `/ask` giống `DispatchPhaseTimings`.
3. **Chưa kiểm thật**: gọi Gemini thật, khóa sai (HTTP 400), hết quota (429), timeout. Cần khóa của Nhật Hòa.


## 10. TASK-08: Vòng Dev-QA tự động có Gemini (`/qa`, `--auto-qa`), giảm RAM, xử lý cold-start của `/ask` — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-08-gemini-qa-loop` (tách từ `main` @`86520e9`; mang theo commit tài liệu Gate 7 `11d6785` → `316ed18`, vì commit đó chưa vào `main`)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task08 ALL CHECKS PASS (E1–E5)** ở lần chạy 3 (máy Nhật Hòa, 22:58, commit `c1d7e10`, có build) — §10.6. Lần chạy 1–2 và các bản sửa: §10.4–10.5. Gọi Gemini thật vẫn SKIPPED (chưa có khóa).
- **Đặc tả**: `current_task.md` TASK-08 **đã có trên `main`** (commit `86520e9` của Nhật Hòa/Gemini), sạch, không BOM.

### 10.1 Đối chiếu ADR-28 với code (trước khi sửa)
ADR-28 cho rằng cú khựng 2,9 s là do DPAPI `Unprotect` / khởi tạo `HttpClient` chạy trên luồng đồng bộ. **Code TASK-07 không làm vậy**: `Vault.Resolve()` chỉ được gọi trong `AgentConsultService.CompleteAsync` (chạy trong `Task.Run`), còn `HttpClient` tĩnh được tạo khi khởi động tiến trình (lúc dựng `NexusRuntime`), không phải trong `/ask`. Regex `Compiled` cũng được biên dịch lúc khởi động. Nguyên nhân thật của 2,9 s vì vậy **vẫn chưa xác định**; con số 72 ms ở lần `/ask` đầu của E4 (Gate 7) cho thấy chi phí "lạnh" thông thường là JIT + mở kết nối SQLite đầu tiên + metadata System.Text.Json. TASK-08 vẫn làm đủ mọi yêu cầu của ADR-28/29, và thêm đo từng bước để lần chạy trên máy cho biết bước nào chậm.

### 10.2 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| §3.1 / ADR-28 `AskSyncBreakdown` | `GeminiAgentClient.cs` (record), `CockpitCommandProcessor.ExecuteAsk/ExecuteQa` | `GuardMs` (regex chặn lộ khóa), `DispatchAndDbMs` (định tuyến + INSERT `DISPATCHED` + `latest_dispatch.json`), `TaskSpawnMs` (`Task.Run`), `TotalSyncMs` (từ lúc vào `Execute` tới khi trả kết quả). Trả về trong `CockpitCommandResult.SyncTiming` và qua pipe `COCKPIT`. DPAPI và `HttpClient` chỉ được chạm tới bên trong `Task.Run`. |
| Khởi động nóng (thêm) | `NexusRuntime.WarmUp()`, `HostService/Program.cs` | Chạy nền khi HostService khởi động: mở kết nối SQLite trong pool (kèm PRAGMA), JIT định tuyến + regex chặn khóa, metadata JSON của `latest_dispatch.json`. Không DPAPI, không HTTP, không ghi gì. Ghi `[WARMUP] … ms` vào `hostservice.log`. Lý do: lần `/ask` đầu trong một tiến trình mới tốn ~72 ms JIT/SQLite (Gate 7 E4), vượt ngân sách 25 ms của Cold Call #1. |
| §3.2 / ADR-29 | `DpapiSecretVault.cs`, `SquadDispatcher.cs`, `CockpitCommandProcessor.cs`, `GeminiAgentClient.cs` | Bỏ cả 4 `RegexOptions.Compiled` (thay bằng `CultureInvariant`). `Lazy<HttpClient>` (`ExecutionAndPublication`), chỉ tạo sau khi đã qua nhánh mock và kiểm tra khóa; `SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = 2 phút }`. `GeminiAgentClient.HttpClientCreated` để selftest chứng minh chạy mock không tạo `HttpClient`. |
| §3.3.1 Config | `NexusConfig.cs` | `autoQaAfterAsk` (mặc định `false`, không bắt buộc). |
| §3.3.2 Schema | `NEXUSHomeHub.Core.cs`, `DevQaLoop.cs` | `qa_response`, `qa_latency_ms` qua ALTER idempotent; `SaveQaResponse`; `latest_dispatch.json` thêm `qaResponse`, `qaLatencyMs`. |
| §3.3.3 `RunQaReviewAsync` | `AgentConsultService` (trong `GeminiAgentClient.cs`) | `StartQa` (đồng bộ: tìm dispatch, mới nhất nếu không có id; chặn bằng chứng chứa khóa; ghi `latest_dispatch.json` event `qa` → Cockpit hiện `[QA REVIEWING... <QA agent>]`). `RunQaReviewAsync` (nền): persona Markdown của **đúng `qa_agent` gắn với dispatch** từ `02_Agents_Registry` + phần cứng + quy tắc kiểm định + mục "QA Verdict Format" (dòng đầu `VERDICT: PASSED` hoặc `VERDICT: NEEDS_WORK`, rồi 1–3 câu); nội dung gửi gồm Task Summary, Primary Agent, AI Response, Additional Evidence. Đọc phán quyết: có `VERDICT: PASS…` → `PASSED`; `NEEDS_WORK`/`FAIL`/không rõ → `NEEDS_WORK` (nguyên tắc Reality Checker). Lưu `qa_*`, rồi gọi `DispatchVerdictService.Apply` (máy trạng thái ADR-19, cầu chì 3 lần → `ESCALATED`, 1 dòng escalation). `evidence_notes` = `QA <tên agent>: <nhận xét>`. |
| Không tính strike khi QA không chạy được | như trên | Chưa có khóa → `qa_response = [LOCAL QA SKIPPED] Chưa cấu hình Gemini API Key.`, không đổi `status`/`retry_count` (đúng đặc tả). **Thêm**: lỗi API/mạng/timeout → `[QA NOT RUN] <chẩn đoán>`, cũng không đổi trạng thái; `--auto-qa` mà Primary không có câu trả lời hợp lệ → `[LOCAL QA SKIPPED] Primary Agent chưa có câu trả lời hợp lệ để kiểm định.` |
| §3.3.4 / ADR-31 Mock | như trên | `NEXUS_GEMINI_QA_MOCK_VERDICT="PASSED: …"` → phản hồi `VERDICT: PASSED\n…`, model `gemini-qa-mock-verifier`, không gọi mạng, không cần khóa. |
| §3.3.5 Cockpit | `CockpitCommandProcessor`, `CockpitShellWindow` | `/qa [bằng chứng]`, `/qa #<id> [bằng chứng]`; `/ask --auto-qa <nhiệm vụ>` (hoặc `autoQaAfterAsk: true`): sau câu trả lời của Primary, QA chạy nối tiếp trong nền. Thẻ có khung **QA REVIEW** (hồng, tối đa 90 px) dưới khung AI RESPONSE; nhãn `[QA REVIEWING... <agent>]`; báo `⚠ [3-STRIKE ESCALATION]` khi QA làm chạm strike 3. `SYNCSTATE` thêm `qaLatencyMs`, `qaResponseChars`. |
| §3.3.5 MCP | `NEXUSHome.McpTools/Program.cs` | `run_gemini_qa_review` (`dispatchId`, `additionalEvidence` đều tùy chọn), **Cấp Vàng** vì đổi trạng thái; trả `dispatchId`, `qaAgent`, `verdict`, `status`, `retryCount`, `qaResponse`, `qaLatencyMs` (+ `reviewed`, `escalatedNow`, `keySource`). Tool thứ 10 của server. |
| Kiểm thử | `SelfTest.cs`, `tools/Verify-Task08.ps1` | Selftest `schema08`, `ask-cold [nowarm]`, `autoqa`, `qa-strikes`, `qa-missingkey`. Script E1–E5 theo đặc tả §4. |

### 10.3 Diễn giải cần Architect xác nhận
1. **Cold Call #1 được đo sau `WarmUp()`**, đúng như HostService làm khi khởi động (người dùng không thể gõ `/ask` trong vài trăm ms đầu). E2 in thêm một lần đo **không** warm-up để tham khảo (không tính PASS/FAIL), cho thấy mức chênh.
2. **Lỗi API/mạng khi QA không tính strike** (thêm ngoài đặc tả): nếu không, mạng chập chờn 3 lần sẽ đẩy dispatch sang `ESCALATED`.
3. **Câu trả lời QA không có dòng `VERDICT:`** → `NEEDS_WORK` (đặc tả: "mặc định theo nguyên tắc Reality Checker khi không rõ ràng") — tính 1 strike.
4. **`run_gemini_qa_review` là Cấp Vàng** (đặc tả không ghi cấp; nó đổi `status`, giống `update_dispatch_verdict`).
5. **Ngân sách đồng bộ của `/qa`**: < 10 ms (Desktop), 25 ms khi LiveCombat (theo ADR-17), giống `/ask`.

### 10.4 Lần chạy 1 trên máy Nhật Hòa (22:47–22:49, commit `10d8deb`) — E1, E4 PASS; E2, E3, E5 FAIL
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0/0; thêm `qa_response, qa_latency_ms`, 547 → 547 dòng; `RegexOptions.Compiled` trong mã nguồn: **0** |
| E2 | **FAIL** | Warm-up 37,97 ms. **Cold Call #1: guard 0,033 / dispatch+db 397,235 / spawn 3,824 / total 408,968 ms** (ngân sách 25). 10 lần sau: total 2,85–11,32 ms, P95 = 11,324 (ngân sách 10). Không warm-up (tham khảo): Cold Call #1 = 180,7 ms (dispatch+db 156,4), P95 8,37. Chạy mock không tạo `HttpClient` ✅ |
| E3 | **FAIL** (chỉ thời gian) | 3a `/ask --auto-qa` #570 → Reality Checker `PASSED`, `ai_response` + `qa_response` trong SQLite và `latest_dispatch.json` ✅. 3b #571: NEEDS_WORK r1 → r2 → **ESCALATED r3**, 1 dòng escalation ✅; nhưng `/qa` lần đầu đồng bộ **10,186 ms** (ngân sách 10; hai lần sau 2,2 / 5,0). 3c không khóa: `DISPATCHED r0`, `[LOCAL QA SKIPPED]` ✅ |
| E4 | PASS | MCP `run_gemini_qa_review` #573 → Reality Checker `PASSED`; Cockpit đang chạy nhận được, độ trễ **155,9 ms** |
| E5 | **FAIL** | Private WS HostService **38,6 MB** ✅ (Gate 7: 44–58,6). Hồi quy: Task03 PASS; **Task04 E6 trượt** vì CPU nền **1,47 %** đo bằng **một cửa sổ 3 s** ngay sau 5 lệnh điều phối → kéo Task05, 06, 07 trượt theo dây chuyền. Task05 E7 trên cùng tiến trình: CPU trung bình 0,00 % |

**Phân tích (dựa trên số liệu, chưa kết luận tuyệt đối):**
- **Cold Call #1 chậm nằm hoàn toàn ở `DispatchAndDbMs`**, không phải guard hay spawn, và không phải JIT (có warm-up còn chậm hơn: 397 ms so với 156 ms). Điểm chung: lần đo diễn ra trong ~2 s sau khi script khởi động HostService chạy lâu dài (`[WARMUP]` của HostService lúc 22:47:47,77; selftest ask-cold lúc 22:47:48–49). **Cú 2,9 s của Gate 7 cũng xảy ra ngay sau khi HostService được khởi động** (E3 chạy ngay sau đó). Trong cùng lần chạy, lần dispatch đầu tiên của Task04 (khi HostService đã chạy ổn định) chỉ mất 19 ms. Kết luận khả dĩ nhất: **tranh chấp với quá trình khởi động của một tiến trình khác** (CPU / đĩa / SQLite), không phải đường găng của `/ask`. 10 lần gọi sau (3–11 ms, so với ~1,4 ms của dispatch thường ở Task04) nhiều khả năng cùng nguyên nhân.
- **`/qa` lần đầu 10,186 ms**: cũng trong lúc HostService khởi động; ngoài ra phần đồng bộ của `/qa` còn ghi `latest_dispatch.json` dù Cockpit không cần (cửa sổ tự áp snapshot).
- **Task04 E6**: một cửa sổ 3 s duy nhất dễ dính đỉnh CPU tạm thời (render, JIT tầng 1, GC) ngay sau các lệnh. Task05 E7 đã đổi sang nhiều mẫu từ Gate 5 vì đúng lý do này.

**Bản sửa sau lần chạy 1:**
1. `Verify-Task08.ps1`: HostService chạy lâu dài chỉ được khởi động **ngay trước E4** (bước duy nhất cần nó), chờ 3 s cho nó khởi động xong; E2/E3 chạy khi không có tiến trình NEXUS nào đang khởi động — đúng hoàn cảnh thật (`/ask` chạy bên trong HostService đã khởi động xong).
2. `/qa` từ Cockpit không ghi `latest_dispatch.json` trên luồng đồng bộ (`StartQa(writeFile: false)`); đường MCP vẫn ghi để Cockpit hiện "QA REVIEWING".
3. `AskSyncBreakdown` thêm `DispatchMs` (định tuyến + persona + INSERT SQLite) và `LatestFileMs` (ghi `latest_dispatch.json`), tách `DispatchAndDbMs` để lần sau thấy rõ phần nào chậm.
4. Selftest `autoqa`, `qa-strikes` gọi `WarmUp()` trước, như HostService khi khởi động.
5. `Verify-Task04.ps1` E6: CPU nền đo **10 × 1 s, chấm theo trung bình 5 giây cuối**, in cả chuỗi (cùng cách Task05 E7 dùng từ Gate 5).

### 10.5 Lần chạy 2 (22:53, commit `c1e6f9a`) — E1, E3, E4, E5 PASS; E2 FAIL
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | 0/0; 655 → 655 dòng; `RegexOptions.Compiled`: 0 |
| E2 | **FAIL** | Warm-up 13,15 ms. **Cold Call #1: guard 0,01 / dispatch+db 50,709 (dispatch 47,423 + latest_dispatch.json 2,978) / spawn 2,02 / total 55,206 ms** (ngân sách 25). **P95 của 10 lần = 3,739 ms** ✅ (max từng bước: dispatch 2,298, file 1,458). Không warm-up (tham khảo): Cold Call #1 74,3 ms (dispatch 53,9 + file 13,1), P95 2,55. Mock không tạo `HttpClient` ✅ |
| E3 | PASS | 3a #678 `--auto-qa` → PASSED; 3b #679 r1, r2 → ESCALATED r3, 1 dòng, `/qa` đồng bộ **4,33 / 0,36 / 0,10 ms**; 3c #680 không khóa → DISPATCHED r0, bỏ qua |
| E4 | PASS | MCP `run_gemini_qa_review` #681 → PASSED; Cockpit nhận, **165,3 ms** |
| E5 | PASS | Private WS sau E1–E4: **44,8 MB**; hồi quy Verify-Task07/06/05/04/03 **ALL PASS** (Task05 E7: 56,2 MB sau cả chuỗi, CPU 0,08 %) |

**Phân tích**: so với lần 1 (409 ms), tách khỏi lúc HostService khởi động đã kéo Cold Call #1 xuống **55 ms** và P95 xuống 3,7 ms — xác nhận phần lớn cú khựng trước đây là tranh chấp khởi động. Phần còn lại nằm gần hết ở `Dispatch` (47 ms), trong đó có 3 việc warm-up chưa làm: đọc file persona của agent (`LoadMarkdown`), câu `INSERT` đầu tiên của tiến trình (khóa ghi, nối WAL, commit), và dựng SystemInstruction (JIT).

**Bản sửa sau lần chạy 2:**
1. `WarmUp()` thêm: đọc file persona của 5 Primary + QA agent (chỉ đọc), dựng một SystemInstruction (không INSERT), và **ghi một dòng audit `host_warmup`** để trả trước chi phí ghi SQLite đầu tiên (dòng nhật ký hợp lệ, không phải dispatch giả).
2. `Dispatch` báo riêng `PersonaMs`, `InsertMs`, `BuildMs` (`DispatchTimings`), đưa vào `AskSyncBreakdown.Dispatch`; E2 in ra để thấy chính xác phần nào còn chậm.

### 10.6 Lần chạy 3 (22:58, commit `c1d7e10`) — ALL PASS
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0/0; 763 → 763 dòng; `RegexOptions.Compiled`: 0 |
| E2 | PASS | Warm-up 61,56 ms (SQLite + 1 dòng audit 9,37; định tuyến + regex + 10 file persona 45,5; JSON 6,68). **Cold Call #1 = 9,43 ms** (ngân sách 25): guard 0,022 / dispatch+db 5,018 = dispatch 1,731 [persona 0,204 / insert 0,545 / build 0,024] + `latest_dispatch.json` 2,981 / spawn 1,297. **P95 của 10 lần = 2,191 ms** (ngân sách 10). Chạy mock không tạo `HttpClient`. Tham khảo, không warm-up: Cold Call #1 = 113,7 ms (dispatch 87,6, trong đó persona 0,39 + insert 8,77 + build 2,30 → **~76 ms là lần chạy đầu của định tuyến / tra registry (JIT)**), P95 3,58 |
| E3 | PASS | 3a #786 `--auto-qa` → Reality Checker `PASSED` (DB + `latest_dispatch.json`); 3b #787 r1, r2 → `ESCALATED` r3, 1 dòng escalation, `/qa` đồng bộ 4,17 / 0,32 / 0,08 ms; 3c #788 không khóa → `DISPATCHED` r0, `[LOCAL QA SKIPPED]` |
| E4 | PASS | MCP `run_gemini_qa_review` #789 → `PASSED`; Cockpit đang chạy nhận, **164,4 ms** |
| E5 | PASS | Private WS HostService sau E1–E4: **44,2 MB** (Gate 7: 58,6 đỉnh); hồi quy Verify-Task07/06/05/04/03 **ALL PASS** (Task05 E7 sau cả chuỗi: 54,6 MB, CPU 0,13 %) |

**Kết luận về cú khựng của lần `/ask` đầu (Gate 7: 2,9 s; Gate 8 lần 1: 409 ms):**
1. Phần lớn là **tranh chấp với quá trình khởi động của HostService chạy song song** (hai lần đo lớn đều nằm trong ~2 s sau khi HostService được khởi động; tách ra thì còn 55 ms).
2. Phần còn lại là **chi phí chạy lần đầu trong tiến trình** (JIT định tuyến / tra registry ~76 ms khi không warm-up, đọc persona, INSERT đầu tiên). `NexusRuntime.WarmUp()` trả trước các chi phí này khi HostService khởi động → Cold Call #1 còn 9,4 ms.
3. **Không phải DPAPI hay `HttpClient`** như ADR-28 giả định: hai thứ đó đã nằm ngoài luồng đồng bộ từ TASK-07.

**Ghi nhận**: private WS sau cả chuỗi hồi quy dao động 54–56 MB (Task05 E7), sau E1–E4 là 44 MB; vẫn dưới 60 MB. Gọi Gemini thật (cả Primary lẫn QA) chưa được kiểm vì chưa có khóa.

## 11. TASK-09: Vòng tự sửa `/retry` theo nhận xét QA, hội thoại nhiều lượt `/followup`, kiểm thử E2E với khóa thật — Ca Claude (2026-11-07)

- **Nhánh**: `claude/task-09-retry-followup-e2e` (tách từ `main` @`ea62086`, sau tag `v0.8.0`; mang theo commit tài liệu Gate 8 lần chạy 3 `f1feae5` → `bbd6b80`, vì lần fast-forward `main` tới `c1d7e10` không gồm commit đó)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task09 ALL CHECKS PASS (E1–E5)** ở lần chạy 1 (máy Nhật Hòa, 23:23, commit `31ee4a8`, có build) — §11.3. Gọi Gemini thật vẫn SKIPPED (chưa nạp được khóa).
- **Đặc tả**: `current_task.md` TASK-09 trên `main` (commit `ea62086`), không BOM.

### 11.1 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| §3.1.1 Schema | `NEXUSHomeHub.Core.cs` | `turn_count INTEGER NOT NULL DEFAULT 1`, `turn_history_json TEXT NOT NULL DEFAULT '[]'` qua ALTER idempotent (dòng cũ = lượt 1, lịch sử rỗng). `RecentDispatch` thêm `TurnCount`, `TurnHistoryJson`. `SaveRefinedTurn` (BEGIN IMMEDIATE: `turn_count + 1`, nối lượt, cắt còn `maxConversationTurns × 2` mục, thay `ai_*`, retry thì `status = DISPATCHED`), `AppendTurnEntry` (lượt QA). `/ask` ghi lượt 1 (`user` + `model`) cùng lúc lưu `ai_response`. |
| §3.1.1 Định dạng lịch sử | `TurnHistory.cs` (mới) | `{ "turn", "role": "user"\|"model"\|"qa", "text", "timestampUtc" }`; đọc lỗi/rỗng → danh sách rỗng (không làm hỏng dispatch); mỗi mục tối đa 8 000 ký tự. |
| §3.1.2 Config | `NexusConfig.cs` | `maxConversationTurns` (mặc định 6, 1..20, không bắt buộc trong file; `/config` hiện giá trị). |
| §3.1.3 `latest_dispatch.json` | `DevQaLoop.cs` | Thêm `turnCount` (file cũ → 1). |
| §3.2 / ADR-36 `/retry` | `AgentConsultService` (`GeminiAgentClient.cs`), `SquadDispatcher.BuildInstructionFor`, `CockpitCommandProcessor` | `/retry [#id] [--auto-qa] [--force] [gợi ý]` (cờ theo thứ tự bất kỳ). **Đồng bộ**: chặn khóa trong gợi ý → tìm dispatch (mới nhất nếu không có id) → **cầu chì**: `status = ESCALATED` hoặc `retry_count ≥ 3` mà không có `--force` → trả đúng `[3-STRIKE CIRCUIT BREAKER] Dispatch #<id> is ESCALATED. Use /retry --force to override.`, không tạo tác vụ nền, không đổi gì, 1 dòng audit `gemini_retry_blocked` → nếu hợp lệ: thẻ `[RETRYING... Primary Agent refining]`, trả focus. **Nền**: dựng lại SystemInstruction 6 phần của Primary Agent từ dòng SQLite + persona; prompt gồm 4 phần cố định (1 nhiệm vụ gốc, 2 câu trả lời trước, 3 `qa_response` + `evidence_notes`, 4 gợi ý) + yêu cầu sửa; câu trả lời mới → lượt mới, `status = DISPATCHED`, `retry_count` giữ nguyên; `--auto-qa` (hoặc `autoQaAfterAsk`) → `RunQaReviewAsync` chấm lại. Cầu chì được kiểm lại ở phần nền. |
| §3.2.4 MCP | `NEXUSHome.McpTools/Program.cs` | `retry_gemini_dispatch` (`dispatchId`, `userHint`, `autoQa`, `forceEscalated`), Cấp Vàng; bị chặn → `isError` + JSON `{blocked, message, status, retryCount, turnCount}`; xong → `dispatchId, status, retryCount, turnCount, aiResponse, qaResponse` (+ `qaVerdict`, `contentsSent`, `turnHistory` tóm tắt). |
| §3.3 / ADR-37 `/followup`, `/f` | như trên + `TurnHistory.BuildContents` | `/f [#id] [--auto-qa] <câu hỏi>`. Nền: lấy tối đa `maxConversationTurns` cặp `user`/`model` gần nhất (bỏ mục `qa`), thêm câu hỏi mới → mảng `contents` nhiều lượt; vai luân phiên, bắt đầu bằng `user` (gộp mục liền nhau cùng vai; lượt `user` chưa có trả lời được gộp vào câu hỏi mới); dòng cũ chưa có lịch sử → dựng lượt 1 từ `task_summary` + `ai_response`. Câu trả lời → lượt mới; `status`/`retry_count` giữ nguyên. QA chấm câu trả lời của lượt sau thì nội dung gửi QA có thêm "Current Turn (n) Request". |
| §3.3.3 MCP | như trên | `followup_gemini_dispatch` (`followUpText` bắt buộc, `dispatchId`, `autoQa`); Cấp Xanh, Cấp Vàng khi `autoQa`. |
| §3.4 / ADR-38 Mock | `GeminiAgentClient` | Với `/retry`, `/f`: `[MOCK-RETRY-TURN-n]` / `[MOCK-FOLLOWUP-TURN-n]` + phần **tính từ chính request đã gửi**: `contents=… (user=…, model=…)`; retry: đầu các phần `task`, `prev_answer`, `qa_seen`, `hint`; follow-up: đầu lượt `model` trước đó (`prev_model`) và câu hỏi (`ask`). `/ask` giữ nguyên định dạng mock cũ (không ảnh hưởng Verify-Task07/08). |
| §3.4 Nạp khóa | `tools/Set-NexusGeminiKey.ps1` (mới) | `Read-Host -AsSecureString` (không hiện, không vào tham số/lịch sử) → DPAPI CurrentUser cùng entropy/định dạng với `DpapiSecretVault` → `config/gemini_key.dpapi`; rồi `HostService --selftest apikey-status` giải mã và in khóa đã che. |
| Giao diện | `CockpitShellWindow.xaml(.cs)` | Nhãn `[RETRYING... Primary Agent refining]`, `[FOLLOW-UP... <agent> turn n]`; dòng meta có `Turn: n`; tiêu đề AI RESPONSE ghi lượt; `SYNCSTATE` và pipe `COCKPIT` thêm `turnCount`, `refinePending`. |
| Kiểm thử | `SelfTest.cs`, `tools/Verify-Task09.ps1` | Selftest `schema09`, `retry-loop`, `retry-breaker`, `followup-chain` (kèm kiểm cửa sổ 6 cặp / cắt 12 mục / dựng lượt 1 cho dòng cũ, không cần mạng), `live-e2e`, `apikey-status`. Script E1–E5 theo đặc tả §4; E1 quét BOM toàn bộ `.cs`/`.xaml`/`.csproj`, tài liệu handoff, `nexus_config.json`; E5 đo CPU 10 × 1 s (5 mẫu cuối) và chạy hồi quy Verify-Task08 → 03. |

### 11.2 Diễn giải cần Architect xác nhận
1. **`/followup` không đổi `status`/`retry_count`** (đặc tả không nói). Hệ quả: hỏi tiếp trên dispatch `PASSED` thì thẻ vẫn `PASSED` dù câu trả lời đã khác; dùng `/f --auto-qa` để QA chấm lại.
2. **`/retry` hoặc `/f` không nhận được câu trả lời** (chưa có khóa, lỗi API) → **không ghi gì** vào SQLite (giữ câu trả lời và nhận xét QA cũ cho lần sửa sau), khác `/ask` (lưu chuỗi chẩn đoán vào `ai_response`). `--auto-qa` khi đó không chạy QA.
3. **Cầu chì chặn cả `retry_count ≥ 3` khi đã `PASSED`** (đúng chữ đặc tả "hoặc retry_count >= 3"): dispatch đã qua bằng `--force` muốn sửa tiếp vẫn phải `--force`.
4. **Giới hạn lưu `maxConversationTurns × 2` mục tính cả mục `qa`** (đúng chữ đặc tả): mỗi vòng có QA dùng 3 mục, nên số cặp lưu được có thể ít hơn 6. Phương án khác: không tính mục `qa`, hoặc giới hạn × 3.
5. **Cửa sổ gửi** = tối đa `maxConversationTurns` cặp trước đó + câu hỏi mới. Nhiệm vụ gốc luôn nằm trong SystemInstruction (mục "2. Mission"), nên không mất khi lượt 1 trượt khỏi cửa sổ.
6. **Đánh số tool**: đặc tả gọi là "tool thứ 7/8", thực tế là tool thứ 11 và 12 (server đã có 10). Cấp: `retry_gemini_dispatch` Vàng; `followup_gemini_dispatch` Xanh (Vàng khi `autoQa`).
7. **`autoQaAfterAsk = true` cũng áp dụng cho `/retry` và `/f`** (đặc tả nêu cho `/retry`).
8. **E5 khi có khóa mà gọi thật lỗi** (ví dụ 429 hết quota) → E5 FAIL kèm chẩn đoán, không coi là SKIPPED.
9. **Kịch bản E2/E3 đổi `NEXUS_GEMINI_QA_MOCK_VERDICT` ngay trong tiến trình selftest** (NEEDS_WORK → PASSED) thay vì script đặt hai lần, để một dispatch đi trọn vòng trong một tiến trình. Script chỉ đặt mock của `/ask`.

### 11.3 Lần chạy 1 trên máy Nhật Hòa (23:23, commit `31ee4a8`) — ALL PASS, gọi thật SKIPPED
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `-warnaserror` 0/0; migration thêm `turn_count`, `turn_history_json`, **871 → 871 dòng**, 0 dòng có dữ liệu lượt không hợp lệ; `maxConversationTurns` = 6; BOM: 0/39 file |
| E2 | PASS | #872: `/qa` → `NEEDS_WORK` r1 lượt 1 → `/retry --auto-qa Đã bổ sung khối using cho Socket` → **`PASSED` r1 lượt 2**; lịch sử `1:user 1:model 1:qa 2:user 2:model 2:qa`; câu trả lời mock cho thấy prompt có đủ `task`, `prev_answer`, `qa_seen="VERDICT: NEEDS_WORK Thiếu xử lý đóng kết nối Socket …"`, `hint`; phần đồng bộ `/retry` **2,98 ms** (guard 0,003 / tìm dispatch 0,91 / spawn 0,32; ngân sách 10) |
| E3 | PASS | #873: `/qa` ×3 → r1, r2, `ESCALATED` r3; `/retry` → đúng thông báo cầu chì, không tác vụ nền, dòng SQLite không đổi, 1 dòng audit `gemini_retry_blocked`, đồng bộ **4,39 ms**; `/retry --force --auto-qa` → **`PASSED` r3 lượt 2**, đồng bộ 0,43 ms; vẫn 1 dòng escalation |
| E4 | PASS | #874: `/f` lượt 2 gửi 3 lượt, đồng bộ 1,83 ms; MCP `followup_gemini_dispatch` lượt 3 gửi **5 lượt (user=3, model=2)**, `prev_model` = câu trả lời lượt 2, `DISPATCHED`, lịch sử `1..3 user/model`; cửa sổ 6 cặp / cắt 12 mục / dựng lượt 1 cho dòng cũ đúng; Cockpit đang chạy nhận lượt 3 trong **153,3 ms** |
| E5 | PASS | Gọi thật **SKIPPED** (chưa có khóa); private WS HostService sau E1–E4 **40,7 MB** (Gate 8: 44,2); CPU nền **0 %** (10 mẫu đều 0); hồi quy Verify-Task08/07/06/05/04/03 **ALL PASS** |

**Ghi nhận**:
- `Set-NexusGeminiKey.ps1` báo "Khóa phải dài 20..256 ký tự" vì không nhận được ký tự nào (ô nhập ẩn không hiện gì). Đã sửa thông báo: báo rõ khi rỗng (dán bằng Ctrl+V hoặc chuột phải) và in số ký tự nhận được (không in khóa).
- Gọi Gemini thật (Primary, follow-up, QA) **vẫn chưa được kiểm** trên bất kỳ Gate nào.


### 11.4 Kiểm thử E5 với khóa Gemini thật (DPAPI) — Ca Antigravity (2026-11-08)
- **Nguồn khóa**: `DPAPI` (`03_Local_Data_Vault/config/gemini_key.dpapi`, nạp qua `Set-NexusGeminiKey.ps1`).
- **Phát hiện thực tế từ endpoint Google (`v1beta`)**:
  - Khi gọi `models/gemini-2.5-flash`, máy chủ Google trả về `HTTP 404 Not Found`: *"This model models/gemini-2.5-flash is no longer available to new users. Please update your code to use models/gemini-3.8-flash for the latest features and improvements."*
  - Model `models/gemini-3.8-flash` được gợi ý hiện bị treo quá 25 s trên phía máy chủ Google, trong khi `models/gemini-3.5-flash-lite` phản hồi ổn định trong ~1,2–2,1 s và trả đầy đủ `usageMetadata`.
  - `GeminiAgentClient.GenerateAsync` đã được bổ sung cơ chế tự động chuyển sang `gemini-3.5-flash-lite` khi gặp `HTTP 404` trên model mặc định (vẫn giữ nguyên mặc định `geminiModel = "gemini-2.5-flash"` trong `NexusConfig`).
- **Số liệu E5 (`live-e2e`, Dispatch `#1307`)**:
  - `/ask`: `ok = true`, `model = gemini-3.5-flash-lite`, độ trễ **`2 093 ms`**.
  - `/f` (lượt 2, `contentsSent = 3`): `ok = true`, `model = gemini-3.5-flash-lite`, độ trễ **`1 257 ms`**, `turn_count = 2`.
  - `/qa`: `reviewed = true`, `verdict = PASSED`, độ trễ **`1 396 ms`**, `status = PASSED`, lịch sử `1:user 1:model 2:user 2:model 2:qa`.
  - Tổng thời gian trọn vòng 3 bước (`/ask` → `/f` → `/qa`): **`4 828,6 ms`**.

## 12. TASK-10: Độ bền gọi Gemini API thật (HTTP 429/5xx Backoff), Giới hạn RPM/Token cục bộ & Đếm `usageMetadata` trên HUD — Ca Antigravity (2026-11-08)

- **Nhánh**: `claude/task-10-gemini-resilience-quota` (tách từ `main` @`7b97945`, sau tag `v0.9.0`)
- **Trạng thái kiểm chứng**: ✅ **Verify-Task10 ALL CHECKS PASS (E1–E5)** kèm toàn bộ chuỗi hồi quy `Verify-Task09` → `Verify-Task03` (máy Nhật Hòa `BITMAPPP`, commit `d2f2b09`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-10 Hướng A, ADR-40..48)

### 12.1 Đã triển khai
| Mục đặc tả | File | Ghi chú |
|-----------|------|---------|
| §3.1.1 Schema SQLite WAL | `NEXUSHome.Core/NEXUSHomeHub.Core.cs` | Migration idempotent thêm `prompt_tokens INTEGER NOT NULL DEFAULT 0`, `completion_tokens INTEGER NOT NULL DEFAULT 0`, `total_tokens INTEGER NOT NULL DEFAULT 0` vào `agent_dispatch_logs` (bảo toàn 100% các dòng cũ). `SaveAiResponse`, `SaveQaResponse`, `SaveRefinedTurn` thực hiện **cộng dồn (`+=`)** token của mọi lượt gọi Gemini thuộc cùng một `dispatchId`. Bổ sung `GetDailyTokenUsage()` truy vấn tổng token từ `00:00:00 UTC` trong ngày. |
| §3.1.2 `nexus_config.json` | `NEXUSHome.Core/NexusConfig.cs` | Thêm `maxRequestsPerMinute` (10), `maxTokensPerMinute` (30 000), `maxDailyTokens` (250 000), `httpMaxRetries` (2), `httpRetryBaseDelayMs` (300). Hỗ trợ nạp nóng qua `/reload` và hiển thị trong `/config`. |
| §3.1.3 `latest_dispatch.json` | `NEXUSHome.Core/DevQaLoop.cs` | Thêm `promptTokens`, `completionTokens`, `totalTokens` (của dispatch hiện hành) và `sessionTotalTokens`, `dailyTotalTokens`. Giữ nguyên định dạng UTF-8 không BOM. |
| §3.2 `usageMetadata` & `GeminiQuotaGuard` | `NEXUSHome.Core/GeminiAgentClient.cs`, `NexusRuntime.cs`, `CockpitCommandProcessor.cs` | Trích xuất `promptTokenCount`, `candidatesTokenCount`, `totalTokenCount` từ `usageMetadata` của phản hồi Gemini thật; ở chế độ Mock ước lượng tất định `promptTokens = Math.Max(10, promptText.Length / 4)`, `completionTokens = Math.Max(8, responseText.Length / 4)`. `GeminiQuotaGuard` duy trì cửa sổ trượt 60 giây cho RPM/TPM và tổng token ngày từ SQLite; chặn đồng bộ trước khi gọi mạng (`0,019 ms`, ngân sách `< 2 ms`) với đúng thông báo `"[QUOTA GUARD] Rate/Token limit reached (RPM/TPM/Daily). Adjust in nexus_config.json and run /reload if needed."` |
| §3.3 HTTP 429/5xx Backoff + Jitter | `NEXUSHome.Core/GeminiAgentClient.cs` | Tự động thử lại tối đa `httpMaxRetries` lần khi gặp HTTP `429`, `500`, `502`, `503`, `504`. Ưu tiên header `Retry-After` nếu `<= 3 s`, ngược lại tính `httpRetryBaseDelayMs * Math.Pow(2, attempt) + Random(0..100)` giới hạn trần `<= 2 500 ms`. Hỗ trợ biến môi trường `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="N"` để kiểm thử tự động. |
| §3.4 ADR-43, HUD, `/usage` & MCP Tool | `GeminiAgentClient.cs`, `CockpitCommandProcessor.cs`, `CockpitShellWindow.xaml.cs`, `NEXUSHome.McpTools/Program.cs` | Nâng trần lưu trữ `turn_history_json` lên `maxConversationTurns * 3` (18 mục khi `maxConversationTurns = 6`), trong khi `/followup` lọc các cặp `user`/`model` tối đa `maxConversationTurns * 2`. Hiển thị `Tokens: <dispatch> (Today: <daily>)` trên `CockpitUI`; thêm lệnh `/usage` (bí danh `/tokens`); thêm MCP tool `get_gemini_usage_stats`. |
| Độ bền E3 Gate 4 khi khóa màn hình | `NEXUSHome.Core/GameBoost.cs` | `ForegroundWindowInfo.Current()` và `ProcessIdOf()` fallback về `GetShellWindow()` / `GetDesktopWindow()` / PID của `explorer.exe` khi `GetForegroundWindow()` trả về `0` (lúc màn hình khóa/ngủ), tránh lỗi giả `foreground=Idle(0)` trong `Verify-Task04.ps1` E3. |
| Kiểm thử Gate 10 | `NEXUSHome.HostService/SelfTest.cs`, `tools/Verify-Task10.ps1` | Selftest `schema10`, `token-accum`, `backoff-retry`, `quota-guard`, `live-e2e10` và script `Verify-Task10.ps1` (E1–E5 + hồi quy `Verify-Task09` → `Verify-Task03`). |

### 12.2 Kết quả kiểm thử nghiệm thu Gate 10 (`Verify-Task10.ps1`, commit `d2f2b09`) — ALL PASS
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | `dotnet build -c Release -warnaserror` đạt **0 Warning(s), 0 Error(s)**; quét BOM: **0 vi phạm**; migration `schema10` có đủ `prompt_tokens`, `completion_tokens`, `total_tokens`, bảo toàn **1 296 → 1 296 dòng**, `invalidTokenRows = 0`; cấu hình mặc định `maxRequestsPerMinute = 10`, `maxTokensPerMinute = 30 000`, `maxDailyTokens = 250 000`, `httpMaxRetries = 2`, `httpRetryBaseDelayMs = 300`. |
| E2 | PASS | Dispatch `#1297` (mock): `/ask` (`17 + 33 = 50` tokens) → `/qa` (`+52` prompt, `+15` completion → cộng dồn **`148` tokens**, `PASSED`) → `/f` (`+65` prompt, `+87` completion → cộng dồn **`300` tokens** trong SQLite và `latest_dispatch.json`). Kiểm chứng ADR-43: sau 6 lượt có kèm QA, `turn_history_json` giữ đủ **18 mục** (`1:user..6:qa`); ở lượt 7 (`/f`), request gửi đúng **13 mục `contents`** (6 cặp `user`/`model` + câu hỏi mới) và `turn_history_json` cắt trượt giữ đúng **18 mục** (`2:user..7:qa`). |
| E3 | PASS | `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="2"` (Case A, `#1298`): tự động backoff qua 2 lần lỗi HTTP 429 và **thành công ở lần thử thứ 3** (`httpRetryCount = 2`, `aiLatencyMs = 987 ms` ≥ ngưỡng lý thuyết 900 ms). `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="3"` (Case B, `#1299`): dừng đúng sau 2 lần retry (`httpRetryCount = 2`, `aiLatencyMs = 1 042 ms`), trả về `[GEMINI API ERROR HTTP 429] Quota exceeded / Rate limited...` êm ái, `totalTokens = 0`. |
| E4 | PASS | Hạ tạm thời `maxRequestsPerMinute = 2` + `/reload` → 2 lệnh `/ask` đầu thành công, lệnh thứ 3 bị chặn đồng bộ bởi `[QUOTA GUARD]` trong **`0,019 ms`** (ngân sách `< 2 ms`), không tạo dòng dispatch, không khởi tạo `HttpClient`; khôi phục cấu hình + `/reload` hoạt động bình thường. Lệnh `/usage` (`1,697 ms`), `/tokens` (`0,401 ms`) qua Named Pipe và MCP tool `get_gemini_usage_stats` trả về đúng số liệu RPM/TPM/Daily/Session. |
| E5 | PASS | **Live Gemini E2E (`live-e2e10`, Dispatch `#1303`, khóa `DPAPI`)**: `/ask` (`gemini-3.5-flash-lite`, `1 814 ms`) trả về `usageMetadata` thật `promptTokens = 4 051`, `completionTokens = 91`, `totalTokens = 4 142`; `/qa` (`1 042 ms`, `VERDICT: PASSED`) trả về `promptTokens = 2 921`, `completionTokens = 56`, `totalTokens = 2 977`; dòng SQLite `#1303` và `latest_dispatch.json` cộng dồn chính xác **`promptTokens = 6 972`, `completionTokens = 147`, `totalTokens = 7 119`** (tổng thời gian `2 918,9 ms`). Tài nguyên `HostService`: Private Working Set = **`54,7 MB`** (`< 60 MB`), CPU nền 5 mẫu cuối = **`0 %`** (`< 1 %`). Chuỗi hồi quy `Verify-Task09` → `08` → `07` → `06` → `05` → `04` → `03` đạt **ALL PASS 100%** (bao gồm cả `Verify-Task09` E5 live `#1307` và `Verify-Task07` E5 live `#1342`). |

## 13. TASK-11: Bộ đệm phản hồi xác định `GeminiResponseCache` (ADR-44) & Định vị ngữ cảnh kho cục bộ `VaultGroundingEngine` (ADR-45) — Nghiệm thu, Ca Claude (2026-11-08)

- **Nhánh**: `claude/task-11-vault-grounding-cache`. Code do Antigravity viết (`d0c24a1` [WIP], tài liệu `30debbc`); bản sửa `/qa` + escalation do Claude Code chạy trên máy Nhật Hòa (`7ed52b9`). Ca này Claude (claude.ai) đối chiếu code, đọc kết quả từ máy, viết nghiệm thu.
- **Đặc tả**: `current_task.md` TASK-11 **chỉ có trên nhánh này** (nằm trong commit WIP `d0c24a1`), không có trên `main`.
- **Trạng thái kiểm chứng**: ✅ **Verify-Task11 ALL CHECKS PASS (E1–E5)** ở lần chạy 3 (03:46, `7ed52b9` + tài liệu `59b49e2`, có build; §13.3), **với `maxDailyTokens` nâng tạm lên 1 000 000** trong `nexus_config.json` cục bộ (không commit). Lần 1–2 trượt (§13.2); lý do cũ: Hai nguyên nhân còn lại không nằm ở chức năng: ngưỡng `< 2 ms` của **một lần đo duy nhất** dao động quanh 2 ms, và **hạn mức token ngày cục bộ bị chính token mock của bộ kiểm thử ăn hết**, làm hồi quy Task10 → 06 trượt.

### 13.1 Đối chiếu code với báo cáo bàn giao
| # | Phát hiện | Bằng chứng | Trạng thái |
|---|-----------|-----------|-----------|
| 1 | `/qa` trúng cache **không đi qua `DispatchVerdictService`**: `NEEDS_WORK` từ cache chạm strike 3 thì không có dòng `[3-STRIKE ESCALATION]` trong `qa_report.md`, không có audit `dispatch_verdict` (vi phạm ADR-13/19) | `TryExecuteCachedQa` cập nhật SQLite trực tiếp qua `TryCacheHitAndApplyQaVerdict` | ✅ Đã sửa ở `7ed52b9` (`RecordCachedEscalationInBackground`, chạy nền, không ghi trùng); Claude Code thử trên bản sao vault: strike 3 → 1 dòng + 1 audit, strike 4 → không trùng. **Chưa có selftest trong Verify** |
| 2 | `LatestDispatchFile.WriteFast` **ghi đè tại chỗ, không nguyên tử** (ADR-14 yêu cầu file tạm + thay thế). Bản sửa `7ed52b9` bỏ cắt ngắn file (cắt ngắn tốn 5–7 ms ngay sau một lần thay file nguyên tử) và đệm khoảng trắng | Commit message `7ed52b9`; `TryRead` đọc được khoảng trắng thừa (đã thử) | ⚠️ Cần Architect quyết định: đánh đổi tính nguyên tử lấy ngân sách 2 ms. Đọc trúng lúc đang ghi → `TryRead` trả `null`, sự kiện bị bỏ qua, không sập |
| 3 | Model cấu hình `gemini-2.5-flash` trả **HTTP 404** với khóa hiện tại; code TASK-10 **tự đổi ngầm** sang tên viết cứng `gemini-3.5-flash-lite` | `GeminiAgentClient.cs` (nhánh `status == 404 && !modelFallbackTried`); E5 cả 2 lần: `model = gemini-3.5-flash-lite` | ⚠️ Cần Architect quyết định |
| 4 | **Token mock được tính vào hạn mức ngày thật** (`EstimateMockTokens` → `RecordTokens`, và `SeedFromStore` cộng `total_tokens` của dòng mock). Mỗi lần chạy trọn chuỗi Verify tiêu hàng chục nghìn token "giả" | Lần chạy 2: hạn mức ngày ~235–250k / 250k → `QuotaGuard` chặn `/ask` trong Verify-Task10 → 07 | ❌ Chưa sửa; là nguyên nhân E5 trượt |
| 5 | `Verify-Task11.ps1` gọi `Stop-Process -Force` nếu `SHUTDOWN` qua pipe không xong trong 0,8 s (Cấp Đỏ theo `GEMINI.md`) | `Stop-HostService` | ⚠️ Chưa đổi. Lần chạy 2 đã tắt qua pipe trước nên không dùng tới |
| 6 | E5 `live-e2e11` **tự chạy `/retry`** nếu QA chấm `NEEDS_WORK`, nhưng báo cáo không ghi là đã retry | `SelfTest.Task11.cs` | ⚠️ Cả 2 lần QA thật đều `PASSED` ngay, nên chưa xảy ra; nên ghi trường `retriedToPass` |
| 7 | Bộ kiểm thử `Clear()` bảng `gemini_response_cache` trên DB thật | `cache-hit-bypass`, `vault-grounding`, `cache-commands`, `live-e2e11` | ℹ️ Chỉ mất cache, không mất dữ liệu điều phối |

### 13.2 Kết quả trên máy Nhật Hòa
| # | Lần 1 (02:59, `30debbc`, `-SkipBuild`, bản build của Antigravity) | Lần 2 (03:18, build lại, có bản sửa sau này là `7ed52b9`) |
|---|---|---|
| E1 | ✅ build 0/0; 15 cột cache; 1 427 dòng giữ nguyên; BOM 0 | ✅ build 0/0; 1 664 dòng; BOM 0 |
| E2 | ❌ `/ask` hit 1,264 / 0,662 ms, 0 token, quota không đổi, `/ask!` `--fresh` `/f` gọi API đúng; **`/qa` hit 4,962 ms** | ❌ `/ask` hit 1,541 / 0,828 ms; **`/qa` hit 2,461 ms** = khóa 0,04 + giao dịch 0,58 + snapshot 0,01 + **`WriteFast` 1,23 ms** |
| E3 | ✅ 3 đoạn, 1 500 ký tự; tìm ấm 0,08 ms; `/g` hit 1,632 ms | ❌ **`/g` hit 2,124 ms** (đường này không bị sửa) |
| E4 | ✅ `/cache`, `/cache clear` qua pipe; MCP `search_local_vault` 0,13 ms | ✅ MCP 0,25 ms |
| E5 | ✅ thật: lần 1 qua mạng 3 358 ms, 4 906 token; lần 2 trúng cache 1,678 ms, 0 token; `/qa` PASSED 1 144 ms. WS 48,7 MB, CPU 0 %; hồi quy 10 → 03 ALL PASS | ❌ thật vẫn PASS (1 878 ms, 4 916 token → hit, QA PASSED); WS 49,5 MB, CPU 0 %; **hồi quy Task10/09/08/07/06 FAIL** (hạn mức token ngày; Task06 E5 thêm `git status` bẩn vì code chưa commit lúc chạy); 05/04/03 PASS |

**Số đo riêng của Claude Code** (selftest `cache-hit-bypass` trên bản sao vault, không có HostService): `/qa` hit trước sửa 5,8–8,1 ms (`WriteFast` 4,6–6,4); sau sửa **3,118 / 1,289 / 1,726 / 1,754 / 1,130 ms** (4/5 < 2 ms). Còn ~0,3–0,5 ms chi phí lần trúng cache đầu tiên qua `CockpitCommandProcessor` mà `WarmUp()` chưa phủ.

**Kết luận**: chức năng ADR-44/45 đúng đặc tả (trúng cache trả đúng nội dung, 0 token, quota không đổi, cộng dồn token tiết kiệm đúng, bypass đúng, nguồn `[file#section]` đúng, ≤ 3 đoạn, ≤ 1 500 ký tự, gọi thật PASS cả 2 lần). Chưa đạt Gate vì: (a) các lần đo đơn lẻ của đường trúng cache nằm trong khoảng 0,7–2,5 ms, ngưỡng 2 ms cho **một** mẫu không ổn định; (b) hạn mức token ngày bị token mock ăn hết.

### 13.3 Lần chạy 3 (03:46, có build, `maxDailyTokens` = 1 000 000 cục bộ) — ALL PASS
| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | build 0/0; 1 770 dòng; BOM 0 |
| E2 | PASS | `/ask` trúng cache **1,811 / 1,114 ms**, 0 token, quota không đổi, cộng dồn đúng; **`/qa` trúng cache 1,137 ms** = khóa 0,03 + giao dịch 0,31 + snapshot 0,004 + `WriteFast` 0,31; `/ask!`, `--fresh`, `/f` gọi API |
| E3 | PASS | 3 đoạn, 1 500 ký tự; tìm ấm 0,14 ms; **`/g` trúng cache 1,802 ms** |
| E4 | PASS | `/cache`, `/cache stats`, `/cache clear` qua pipe; MCP `search_local_vault` 0,14 ms |
| E5 | PASS | Thật (`gemini-3.5-flash-lite`, khóa DPAPI gói Free): lần 1 qua mạng 2 679 ms, 4 897 token, 3 nguồn grounding → lần 2 trúng cache **1,923 ms**, token ngày đứng yên 256 912 → 256 912, RPM 1 → 1 → `/qa` PASSED 1 059 ms, 3 066 token. WS **49,1 MB**, CPU **0 %** (10 mẫu đều 0); hồi quy Verify-Task10/09/08/07/06/05/04/03 **ALL PASS** |

**Lưu ý khi đọc kết quả này**:
- **Biên rất mỏng**: `/ask` hit 1,811 ms, `/g` hit 1,802 ms, hit thật 1,923 ms (chỉ dưới ngưỡng 0,08–0,2 ms). Lần chạy 2 cùng đường này ra 2,12–2,46 ms. Một mẫu đơn lẻ vẫn có thể trượt ở lần chạy sau → điểm mở §3.1 của `gate11_handoff.md` (P95 của 10 lần) vẫn cần quyết định.
- **Chỉ qua được hồi quy nhờ nâng `maxDailyTokens`**: token ngày cục bộ đã ở 256 912, vượt mức mặc định 250 000 vì token mock được cộng vào. Lỗi gốc (§13.1 #4) chưa sửa. File cấu hình nằm trong `config/` (git-ignore), nên thay đổi chỉ có trên máy Nhật Hòa.
- Khóa API ở **gói Free** của Google AI Studio (chưa gắn thanh toán): gọi thật không tốn tiền; nhiều khả năng đó là lý do `gemini-2.5-flash` trả 404.
- Claude Code ghi nhận thêm: `Verify-Task03.ps1:200` báo `Cannot index into a null array` khi đọc dòng `Target monitor` trong log; không làm hỏng kết quả (Task03 `allPassed = True`), nên sửa sau.

## 14. TASK-11b: Gia cố hạ tầng kiểm thử, tách token mock, khôi phục ghi nguyên tử ADR-14 — Ca Claude (2026-11-08)

- **Nhánh**: `claude/task-11b-test-infra-hardening`, tách từ `main` @`6424d0f` (Claude fast-forward `main` theo lệnh Gemini lúc 03:52). **Tag `v0.11.0` chưa lên GitHub**: máy chủ chặn đẩy tag từ môi trường của Claude (HTTP 403), Nhật Hòa cần đẩy từ máy.
- **Đặc tả**: chỉ thị TASK-11b của Gemini (chat, 03:53), chép nguyên văn vào `current_task.md` trên nhánh này.
- **Trạng thái kiểm chứng (cập nhật 16:03, lần chạy 5, §14.6)**: ✅ **Verify-Task11 ALL CHECKS PASS (E1–E5)**, `allPassed = true`. Nhánh `-r2` @`690961a` (code giống `269e434`), có build, cấu hình chuẩn `250000` / `gemini-3.5-flash-lite`, máy rảnh (CPU toàn máy trung bình 8,2 %). Chưa gắn `v0.11.1` (chờ Architect), chưa merge vào `main`.
- **Lần chạy 4 (15:52, §14.5)**: ❌ chưa đạt — E1, E4, **E5 PASS** (lần đầu `/ground` thật đạt cả P50 lẫn P95); **E2, E3 (mock) trượt ngưỡng độ trễ** vì máy đang tải 76 % CPU (Roblox, Discord, Chrome, Overwolf chạy cùng lúc). Chưa gắn `v0.11.1`.
- **Trạng thái cũ**: ❌ lần chạy 2 (04:20, có build, cấu hình chuẩn): E1–E4 PASS; E5 trượt **đúng một ngưỡng**: tổng thời gian trúng cache của `/ground` thật P50 = 2,754 ms (ngưỡng 2,5). Mọi phần khác của E5 PASS, kể cả hồi quy Task10 → 03 với `maxDailyTokens = 250000` (§14.3).

### 14.1 Đã triển khai
| Hạng mục | File | Ghi chú |
|----------|------|---------|
| A. Tách token mock | `NEXUSHomeHub.Core.cs`, `GeminiAgentClient.cs` (`GeminiQuotaGuard`), `DevQaLoop.cs`, `CockpitCommandProcessor.cs` | `GeminiResult.IsLiveApi` (true chỉ khi phân tích câu trả lời từ API thật). 4 cột mới trên `agent_dispatch_logs` (ALTER idempotent): `live_prompt_tokens`, `live_completion_tokens`, `live_total_tokens`, `mock_tokens_used`; lần tạo cột thì điền ngược: dòng có `ai_model` chứa "mock" → `mock_tokens_used`, còn lại → `live_*`. `SaveAiResponse` / `SaveQaResponse` / `SaveRefinedTurn` nhận `liveApi`. `GetDailyTokenUsage` (hạn mức ngày) chỉ cộng `live_*`; `GetDailyMockTokens` riêng. `QuotaGuard.RecordTokens(…, liveApi)`: token mock chỉ vào bộ đếm mock (không vào cửa sổ TPM, tổng phiên, hạn mức ngày). `GeminiUsageStats.MockTokensUsed`, `latest_dispatch.json` `mock_tokens_used`, `/usage` hiện riêng dòng mock. Mặc định `maxDailyTokens` vẫn là 250 000 trong code; **file cấu hình trên máy Nhật Hòa đang là 1 000 000 và phải sửa về** (E1 kiểm). |
| B. Ghi nguyên tử ADR-14 | `DevQaLoop.cs` (`LatestDispatchFile.Write`), `NexusConfig.cs` (`AtomicFile.WriteAllBytes`) | Bỏ hẳn `WriteFast`. Mọi lần ghi `latest_dispatch.json` (cả đường trúng cache): `SerializeToUtf8Bytes` → `latest_dispatch.json.<pid>.tmp` cùng thư mục (FileStream bộ đệm 1 byte, một lệnh ghi) → `File.Move(…, overwrite: true)`, dưới một khóa toàn tiến trình (các luồng trong cùng tiến trình dùng chung tên file tạm). Trường mới `cache_lookup_ms`, `total_dispatch_ms`; `QaCacheHitTimings.WriteFastMs` đổi thành `AtomicWriteMs`. |
| C. P50/P95 trên 10 mẫu | `SelfTest.Task11.cs`, `Verify-Task11.ps1` | `MeasureHits`: 10 lần trúng cache liên tiếp sau 1 lần warm-up cho `/ask`, `/qa` (E2), `/g` (E3), `/ground` thật (E5). Ngưỡng: `cache_lookup_ms` P95 ≤ 2,0 ms; tổng (`SyncTiming.TotalSyncMs`, gồm ghi nguyên tử) P50 ≤ 2,5 ms, P95 ≤ 4,0 ms. Phân vị nội suy tuyến tính (R-7 / Excel `PERCENTILE.INC`). Script in mảng 10 mẫu, min, P50, P95, max. **Thêm** kiểm tính nguyên tử: 40 lần ghi khi có một luồng đọc song song parse `latest_dispatch.json` liên tục; yêu cầu 0 lần đọc hỏng. |
| D. Model + grounding | `NexusConfig.cs`, `GeminiAgentClient.cs`, `DevQaLoop.cs`, `VaultGroundingAndCache.cs` | Mặc định `geminiModel = gemini-3.5-flash-lite` (`GeminiAgentClient.FallbackModel`). Gặp HTTP 404 thì vẫn đổi sang model này một lần, nhưng **không ngầm**: mọi kết quả từ model khác cấu hình mang `ModelFallback = "[MODEL FALLBACK: <cũ> -> <mới>]"` và có một dòng audit `gemini_model_fallback`; nhớ model dự phòng chỉ cho đúng model cấu hình đó. `latest_dispatch.json` có `model_used`. Grounding: loại trừ thêm `*.db*`, `*.tmp`, mọi `latest_dispatch.json*`, `task*_selftest_*`, `task*_verification.json`; thứ tự hòa điểm `(Score DESC, RelativePath ASC (ordinal), SectionIndex ASC)`, bỏ ưu tiên riêng cho `current_task.md`. |
| Kiểm thử cũ chỉnh theo | `SelfTest.cs` (`token-accum`), `Verify-Task10.ps1` E4 | `token-accum` (Gate 10 E2) trước đây đòi token mock xuất hiện trong tổng phiên/ngày; nay kiểm điều ngược lại: `mock_tokens_used` tăng đúng, hạn mức ngày thật **không đổi**. Verify-Task10 E4 chấp nhận `dailyTotalTokens + mockTokensUsed > 0` (không có lượt gọi thật trong ngày UTC thì `dailyTotalTokens` có thể bằng 0). |
| Ngoài chỉ thị | `Verify-Task11.ps1` | `Stop-HostService` không còn `Stop-Process -Force` (Cấp Đỏ): chỉ tắt qua pipe, không thoát sau 8 s thì dừng script và báo người dùng (điểm mở #6 của Gate 11). E5 ghi `retriedToPass` và số dòng audit `gemini_model_fallback` (điểm mở #7). |

### 14.2 Diễn giải cần Architect xác nhận
1. **`total_dispatch_ms` trong `latest_dispatch.json`** không thể chứa thời gian ghi chính file đó. Trong file nó là thời gian **tới ngay trước** lần ghi; con số chấm P50/P95 (gồm cả ghi nguyên tử) là `SyncTiming.TotalSyncMs` của lệnh, được selftest và script in ra.
2. **`cache_lookup_ms`** = khóa SHA-256 + định tuyến trong bộ nhớ + giao dịch SQLite WAL (UPDATE cache … RETURNING + INSERT dispatch, hoặc cập nhật phán quyết với `/qa`); không gồm dựng snapshot và ghi file.
3. **Tổng phiên (`SessionTotalTokens`) cũng chỉ đếm token thật**; token mock chỉ ở `mock_tokens_used`. RPM vẫn đếm mọi request (kể cả mock), vì Gate 10 E4 dùng RPM để kiểm chặn.
4. **Điền ngược dữ liệu cũ** dựa vào `ai_model`: một dòng trả lời thật nhưng QA mock (hiếm) bị tính cả vào live; một dòng mock nhưng QA thật bị tính cả vào mock.
5. **File cấu hình không được tự sửa**: Claude không viết code ghi đè `nexus_config.json` của người dùng. `geminiModel` và `maxDailyTokens` trên máy phải sửa tay; E1 trượt và in hướng dẫn nếu chưa sửa.
6. **Rủi ro còn lại của ghi nguyên tử**: `File.Move` bị chặn nếu có chương trình mở `latest_dispatch.json` mà không cho xóa (ví dụ phần mềm diệt virus). Khi đó `AtomicFile` chờ 20 ms rồi thử lại, một mẫu như vậy sẽ đẩy P95 vượt 4 ms.

### 14.3 Kết quả trên máy Nhật Hòa (Claude Code chạy)
- **Lần 1**: build lỗi kiểm thử cũ: `Verify-Task03.ps1` E5 đọc nhầm khối log của một tiến trình `--selftest` (`Cannot index into a null array`). Ngoài ra `foundVaultDoc` (E3) chỉ nhận 3 tên file cố định. Claude Code sửa cả hai ở `02dac22`. Số đo tham khảo của lần này: `/ground` thật lookup P95 2,283 ms; tổng P50 4,834, P95 6,17 ms.
- **Lần 2 (04:20, `02dac22`, có build, `maxDailyTokens = 250000`, `geminiModel = gemini-3.5-flash-lite`)**:

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | build 0/0; BOM 0; 4 cột `live_*`/`mock_tokens_used` có; 2 163 dòng giữ nguyên; cấu hình chuẩn. Token hôm nay sau khi tách: **live 174 454, mock 149 935**. Nếu chưa tách, tổng là 324 389, đã vượt 250 000 và chặn hồi quy |
| E2 | PASS | `/ask` lookup P95 **1,156**; tổng P50 **2,186**, P95 **2,881** · `/qa` lookup P95 **0,714**; tổng P50 **1,816**, P95 **2,256** · ADR-14: 40 lần ghi, **17 738 lần đọc song song, 0 lần đọc hỏng** |
| E3 | PASS | 3 đoạn, 1 500 ký tự · `/g` lookup P95 **0,863**; tổng P50 **1,65**, P95 **2,017** |
| E4 | PASS | MCP `search_local_vault` 0,142 ms |
| E5 | **FAIL** | Gọi thật `gemini-3.5-flash-lite` trả lời ngay, **0 dòng audit `gemini_model_fallback`** (2 441 ms, 4 852 token) · `/qa` PASSED, `retriedToPass = false` · `/ground` thật trúng cache: lookup P95 **1,807** ✅; tổng **P50 2,754** ❌ (ngưỡng 2,5), P95 **3,383** ✅ · WS **47,3 MB**, CPU **0,03 %** · hồi quy Verify-Task10/09/08/07/06/05/04/03 **ALL PASS** |

Mảng 10 mẫu (ms):
- `/ask` tổng: 1,431 1,941 1,863 1,907 2,144 2,823 2,228 2,694 2,716 2,928
- `/g` tổng: 1,555 1,718 2,115 1,474 1,478 1,897 1,583 1,396 1,871 1,768
- `/ground` thật lookup: 0,725 1,013 0,965 1,09 1,268 1,068 1,875 1,725 1,058 1,256
- `/ground` thật tổng: 1,978 3,343 2,529 2,7 3,108 2,795 3,415 3,116 2,641 2,712

**Nhận định của Claude (chưa kiểm chứng)**: đường trúng cache dùng cùng một đoạn code cho mock và thật. Khác biệt nằm ở **kích thước dữ liệu**: câu trả lời thật dài hơn câu trả lời mock nhiều lần, và 3 nguồn grounding thật có tên dài. Dữ liệu lớn hơn làm chậm lệnh INSERT dòng điều phối (lookup thật ~1,1 ms so với mock ~0,6 ms), bước tạo JSON và ghi nguyên tử `latest_dispatch.json`. Cách kiểm không tốn token thật: chạy `vault-grounding` với `NEXUS_GEMINI_MOCK_RESPONSE` dài bằng câu trả lời thật, đo từng bước.

### 14.4 Đo từng bước (Claude Code, `1e650c7`) và lần chạy 3 (04:40)
- **Đo từng bước** (selftest `cache-hit-profile`, câu trả lời và tên nguồn dài như bản thật):
  - Mock dạng E3 và mock dạng E5 chỉ lệch khoảng 0,1 ms; có HostService chạy thì chậm thêm 0,1–0,2 ms.
  - **`/ground` thật chạy riêng (có HostService): tổng P50 1,754 ms, P95 2,641 ms, lookup P95 1,114 ms → ĐẠT.** Phân bổ P50: ghi file tạm 0,509 + `File.Move` 0,438 (khoảng 55 %), giao dịch SQLite 0,535; mỗi bước còn lại ≤ 0,09 ms (serialize JSON 0,062, `SUM` 0,024, open + begin 0,085, `Grounding.Search` 0,072).
  - Thử ghi file tạm bằng `File.OpenHandle`: không cải thiện (chi phí nằm ở hệ điều hành và bộ lọc file), đã hoàn tác.
  - Kết luận: không có bước code nào chiếm khoảng 1,1 ms. Phần chênh của lần chạy 2 đến từ hoàn cảnh lúc chạy cả bộ kiểm thử.
- **Lần chạy 3** (đầy đủ, có build): E1–E4 PASS. Số đo:
  - `/ask`: tổng P50 1,256 / P95 2,142 ms;
  - `/qa`: tổng P50 2,173 / P95 3,25 ms;
  - `/g`: tổng P50 1,612 / P95 2,003 ms.
- **E5 FAIL vì hai nguyên nhân ngoài code của nhánh**:
  1. Lần gọi `/ground` thật đầu tiên bị `[GEMINI TIMEOUT]` sau 25 s (0 token), nên không có gì để lưu cache và không đo được mẫu trúng cache nào. Các lần gọi thật trong hồi quy cũng chậm bất thường (10,7–14,5 s/lần, bình thường 1–2 s).
  2. `main` đã tiến tới `603a974` (roadmap + sổ ADR, Claude fast-forward theo lệnh Gemini). Nhánh chưa chứa hai commit này, nên Verify-Task06 E5 báo "HEAD diverged" và Task10 → 06 trượt dây chuyền.
- **Xử lý nguyên nhân 2**: Claude chép lại 4 commit TASK-11b lên trên `main` mới, sang nhánh **`claude/task-11b-test-infra-hardening-r2`**. Code giống hệt `1e650c7`, chỉ thêm `ROADMAP_FUTURE.md` và `docs/ADR_REGISTRY.md` có sẵn trên `main`. Làm vậy để không phải force-push và giữ `main` thẳng hàng (ADR-39). Nhánh cũ giữ nguyên để đối chiếu.


### 14.5 Lần chạy 4 (15:49–15:52, nhánh `-r2` @`269e434`, có build, Claude Code chạy trên máy Nhật Hòa)
- **Thử trước**: build Release 0/0; một `/ask!` thật (dispatch #2790) → `gemini-3.5-flash-lite` trả lời sau **1 453 ms**, 4 138 token, không fallback. Hạn mức ngày đã reset (0 / 250 000 trước lượt thử). Đạt điều kiện 1–3 s nên chạy Verify đầy đủ.
- **`main` không đổi**: `origin/main` = `603a974`, là tổ tiên của `-r2` (Verify-Task06 E5: "fast-forward ready").
- **Hoàn cảnh máy**: tải CPU toàn máy **76 %** khi chạy. Tiến trình nặng nhất: `RobloxPlayerBeta` (1,4 GB, đang chạy game), Discord, Chrome, Overwolf, Steam, iCUE. Lần chạy 2–3 (khoảng 04:00) máy rảnh. Claude không tắt các chương trình này (Cấp Đỏ).

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | build 0/0; BOM 0; 2 790 dòng; 15 cột cache; đủ cột `live_*`/`mock_tokens_used`; cấu hình chuẩn `250000` / `gemini-3.5-flash-lite`; token hôm nay live 4 138, mock 0 |
| E2 | **FAIL** | `/ask` lookup P95 1,14 ✅; tổng **P50 2,923** ❌ / **P95 8,922** ❌ (min 1,97, max 12,399) · `/qa` lookup **P95 2,059** ❌; tổng **P50 2,8** ❌ / **P95 4,336** ❌ · ADR-14: 40 lần ghi, **17 090 lần đọc song song, 0 lần đọc hỏng** ✅ · 0 token, bypass `/ask!` `/f` đúng ✅ |
| E3 | **FAIL** | 3 đoạn, 1 500 ký tự, nguồn `[file#section]` ✅ · `/g` lookup **P95 3,411** ❌; tổng **P50 3,392** ❌ / **P95 6,791** ❌ |
| E4 | PASS | pipe + MCP; `search_local_vault` 0,168 ms |
| E5 | **PASS** | gọi thật `gemini-3.5-flash-lite`, **0 dòng audit `gemini_model_fallback`**, `retriedToPass = false`; `/ground` thật trúng cache: lookup P95 **1,102**, tổng **P50 2,363**, P95 **2,932** ✅ (lần đầu đạt cả hai); `/qa` PASSED; private WS **49 MB**, CPU HostService **0 %**; hồi quy Verify-Task10/09/08/07/06/05/04/03 **ALL PASS** ở `250000` |

Mảng 10 mẫu tổng (ms):
- `/ask`: 1,989 2,623 4,673 **12,399** 2,404 3,385 3,509 1,97 3,222 2,144
- `/qa`: 3,54 2,082 2,028 2,132 2,556 2,742 3,875 4,714 2,857 3,616
- `/g`: 2,32 3,655 2,381 **7,649** 3,13 2,073 2,393 3,833 4,551 5,743
- `/ground` thật: 2,014 1,504 2,357 2,078 2,41 3,123 2,698 2,282 2,369 2,697

**Nhận định (chưa kiểm chứng bằng lần chạy khi máy rảnh)**: trượt do máy bận, không do code. Bằng chứng: (1) code không đổi so với lần chạy 2–3, khi E2/E3 đều đạt; (2) các mẫu đột biến đơn lẻ 7–12 ms, kiểu luồng bị hệ điều hành tạm dừng, không phải chậm đều; (3) cả lookup (chỉ SQLite) cũng nhảy lên 2,4–4,6 ms. Cần chạy lại khi đã tắt Roblox và các ứng dụng nặng.

### 14.6 Lần chạy 5 — ✅ ALL CHECKS PASS (16:00–16:03, nhánh `-r2` @`690961a`, có build, Claude Code chạy trên máy Nhật Hòa)
- **Trước khi chạy**: Nhật Hòa tắt Roblox, Overwolf và các ứng dụng nặng. Bộ đếm `\Processor(_Total)\% Processor Time` 10 × 1 s: 7,2 13,9 10,4 9,7 5,1 12,7 5,6 6,7 3,3 7,4, **trung bình 8,2 %** (lần 4: 76 %). Còn lại: Discord, Chrome, Steam, iCUE. `RobloxPlayerBeta` tự mở lại lúc 15:58 nhưng chỉ dùng 0,6–2,4 % CPU, 122 MB.
- **Code**: không đổi so với lần 4 (`690961a` chỉ thêm tài liệu sau `269e434`). `origin/main` = `603a974`, là tổ tiên của nhánh ("fast-forward ready"). Tag `v0.11.0` đã có trên GitHub (`ece865f`).
- **Report**: `task11_verification.json` `allPassed = true`, timestamp `2026-11-08T09:03:45Z`.

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | PASS | build 0/0 (`-warnaserror`); BOM 0; 3 001 dòng; 15 cột cache; đủ `live_*`/`mock_tokens_used`; cấu hình chuẩn `maxDailyTokens = 250000`, `geminiModel = gemini-3.5-flash-lite`; token hôm nay live 32 659, mock 7 788 |
| E2 | PASS | `/ask` lookup P95 **0,553**; tổng P50 **1,283** / P95 **1,692** · `/qa` lookup P95 **0,531**; tổng P50 **1,468** / P95 **1,829** · 0 token, `saved_total_tokens` cộng dồn 114; `/ask!`, `/f` không trúng cache (bypass đúng) · ADR-14: 40 lần ghi, **24 216 lần đọc song song, 0 lần đọc hỏng** |
| E3 | PASS | 3 đoạn, 1 500 / 1 500 ký tự, nguồn `[file#section]`; tìm kiếm ấm 0,083 ms · `/g` lookup P95 **0,714**; tổng P50 **1,826** / P95 **2,334** |
| E4 | PASS | pipe `/cache` + MCP `get_gemini_cache_stats` (54 token tiết kiệm) + `search_local_vault` **0,099 ms** |
| E5 | PASS | gọi thật `gemini-3.5-flash-lite` (khóa DPAPI), **0 dòng audit `gemini_model_fallback`**, `retriedToPass = false` · `/ground` thật trúng cache: lookup P95 **1,01**; tổng P50 **1,715** / P95 **2,294** · `/qa` PASSED · private WS **49 MB**, CPU HostService **0 %** (10 × 1 s) · hồi quy Verify-Task10/09/08/07/06/05/04/03 **ALL PASS** ở `250000` |

Mảng 10 mẫu sau warm-up (ms):

| Đường | Đo | Mẫu |
|-------|----|-----|
| `/ask` (mock) | lookup | 0,41 0,409 0,425 0,394 0,395 0,421 0,438 0,511 0,444 0,587 |
| `/ask` (mock) | tổng | 1,289 1,171 1,15 1,109 1,201 1,277 1,757 1,389 1,469 1,612 |
| `/qa` (mock) | lookup | 0,368 0,498 0,325 0,311 0,366 0,335 0,532 0,528 0,53 0,367 |
| `/qa` (mock) | tổng | 1,47 1,485 1,14 1,152 1,271 1,465 1,843 1,811 1,561 1,351 |
| `/g` (mock) | lookup | 0,574 0,462 0,466 0,39 0,657 0,66 0,718 0,709 0,667 0,64 |
| `/g` (mock) | tổng | 1,578 1,499 1,262 1,366 1,832 1,868 2,514 1,838 2,113 1,819 |
| `/ground` (thật) | lookup | 0,532 0,508 0,666 0,666 0,595 0,534 0,752 1,222 0,63 0,506 |
| `/ground` (thật) | tổng | 1,755 1,489 1,645 1,866 1,783 1,628 1,835 2,645 1,675 1,481 |

**Kết luận**: cùng một code, lần 4 (máy tải 76 %) trượt E2/E3, còn lần 5 (máy rảnh, 8 %) đạt mọi ngưỡng với dư địa lớn: tổng P50 1,3–1,8 ms so với ngưỡng 2,5; P95 1,7–2,3 ms so với 4,0. Điều này xác nhận nhận định ở §14.5: lần 4 trượt do máy bận, không do code.

## 15. TASK-14: Tiện ích nội bộ 0 token `/calc`, `/timer`, `/note` (ADR-59) — Ca Claude (2026-11-08)

- **Nhánh**: `claude/task-14-local-utilities`, tách từ `main` @`ced587b` (sau `v0.11.1` @`c0618fa` và đặc tả TASK-14 của Gemini).
- **Đặc tả**: `current_task.md` (TASK-14, Gemini ghi lúc 16:15).
- **Trạng thái**: ca viết code (`ec01576`) **chưa build** vì môi trường của Claude không có .NET SDK. Ca sau, Claude Code chạy `Verify-Task14.ps1` (có build) trên máy Nhật Hòa: **✅ ALL PASS ngay lần 1, không phải sửa code**. Số đo ở §15.3.
- **Lần chạy 2 (sau Quyết định (5), `024b63a`)**: ✅ **ALL CHECKS PASS (E1–E5)** lúc 17:46, có build, `overwolfInDefault=False`. Không phải sửa code. Hai lượt trước đó trượt vì lý do ngoài code (repo bị đổi nhánh giữa chừng; Gemini timeout một lần). Chi tiết ở §15.5.

### 15.1 Đã triển khai

| Hạng mục | File | Nội dung |
|----------|------|----------|
| `/calc` (`/c`, `/=`) | `LocalCalculator.cs` (mới) | Bộ phân tích đệ quy viết tay, không Regex, không sinh IL (ADR-29). Thứ tự ưu tiên từ thấp đến cao: `\|`, `&`, `<< >>`, `+ -`, `* / %`, dấu một ngôi, `^`/`**` (kết hợp phải). Số nguyên giữ chính xác trong Int64 (thập phân, `0x`, `0b`); tràn Int64 ở `+ - * ^` thì chuyển sang double. Hàm: `sqrt abs round floor ceil min max pow log ln log2 log10 sin cos tan`; hằng `pi`, `e`. Kết quả nguyên trong Int64 in đủ ba hệ `[CALC] <biểu thức> = <Dec> (Hex: 0x… \| Bin: 0b…)`. Mọi lỗi (chia cho 0, sai cú pháp, NaN, vô cực, lồng quá 48 mức, dài quá 256 ký tự) trả `[CALC ERROR] …`, không ném ngoại lệ ra ngoài |
| `/timer` (`/timers`) | `LocalTimerManager.cs` (mới) | Thời lượng `250ms`, `90s`, `5m`, `1h30m`, `2m30s`, số trơn = giây; tối đa 24 h, 20 bộ đếm. Mỗi bộ đếm là **một** `System.Threading.Timer` chạy một lần, hủy ngay khi hết giờ hoặc bị hủy. Không có bộ đếm thì không có timer, luồng hay vòng lặp nào. `/timer list` hiện các bộ đếm ACTIVE và 3 bộ đếm vừa kết thúc; `/timer cancel <id\|all>`, `stop`, `clear` |
| Hết giờ → HUD thụ động | `LocalUtilityService.cs`, `CockpitShellWindow.xaml.cs`, `Program.cs` | Ghi `latest_dispatch.json` với event `timer_expired`, status `EXPIRED`, nội dung `[TIMER EXPIRED] #<id>: <nhãn> (<thời lượng>)`. Cockpit đang ẩn thì hiện bằng `ShowPassiveCockpit` (`WS_EX_NOACTIVATE`, `ShowActivated=False`); đọc `GetForegroundWindow()` trước, ngay sau, và sau lượt render. Lệnh pipe mới `TIMERALERT` trả bằng chứng này |
| `/note` | `LocalNoteService.cs` (mới) | Ghi vào `03_Local_Data_Vault/zettelkasten/quick_notes.md` (UTF-8 không BOM). Mỗi ghi chú là một mục `### Note <id> · <UTC>` và dòng `- [YYYY-MM-DD HH:mm:ss UTC] <nội dung>`. Ghi nguyên tử (file tạm + `File.Move`) dưới mutex dùng chung cho HostService và McpTools. `/note list [N]` (mặc định 5), `/note find <từ khóa>` (`search`), không phân biệt hoa thường. Secret Guard chặn khóa Google `AIza…`, `sk-…`, `gh*_…`, `AKIA…`, `xox*-…` và khóa PEM trước khi đụng đĩa; dòng audit chỉ ghi độ dài. File nằm trong `.gitignore` |
| Liên thông `/ground` | `VaultGroundingAndCache.cs` | Thêm `Invalidate()`: `/note` gọi để bỏ bộ nhớ 5 s của truy vấn gần nhất. Bộ nhớ đó cũng hết hạn khi `quick_notes.md` đổi (do tiến trình kia ghi). Nếu Top-3 chưa có ghi chú nào, ghi chú khớp nhiều nhất (ít nhất một nửa số từ khóa) lấy ô cuối |
| 0 token | `LocalUtilityService.cs`, `DevQaLoop.cs` | Không gọi Gemini, không lấy suất RPM, không tạo dòng `agent_dispatch_logs`. Snapshot có `dispatchId = 0`, event `local_utility`, `ai_model = local-utility`, mọi trường token = 0, trường mới `local_utility`. `TryRead` chỉ chấp nhận `dispatchId = 0` với hai event cục bộ |
| Cockpit | `CockpitCommandProcessor.cs`, `CockpitShellWindow.xaml(.cs)` | Thẻ "LOCAL UTILITY // CALC/TIMER/NOTE" (ẩn ô QA, hiện kết quả trong ô câu trả lời). Kết quả do McpTools ghi hiện thêm dòng `[SYNC ← McpTools] …`. SYNCSTATE thêm `localUtility`, `localResult`. Cập nhật dòng gợi ý của ô lệnh |
| MCP | `NEXUSHome.McpTools/Program.cs` | Công cụ mới `run_local_utility` (Cấp Xanh), tham số `utility` (`calc`/`timer`/`note`) và `commandText`. Audit ghi tham số đã che khóa. Trả về JSON có token = 0 và `latestDispatchUpdatedAtUtc` |
| Quyết định Gate 11b → Quyết định (5) | `NexusConfig.cs`, `Verify-Task06.ps1` | Lần đầu thêm `"Overwolf"` vào `combatProcessPrefixes` mặc định; sau lần chạy 1 **gỡ ra** theo Quyết định (5) (§15.4) |
| Kiểm thử | `SelfTest.Task14.cs` (mới), `tools/Verify-Task14.ps1` (mới, UTF-8 có BOM) | Selftest `schema14`, `local-calc`, `local-timer`, `local-note`; E3/E5 chạy thêm trên HostService thật qua pipe và MCP |

### 15.2 Diễn giải cần Architect xác nhận

1. **Mỗi bộ đếm một `Timer` chạy một lần**, không phải "một `System.Threading.Timer` duy nhất" như bản nháp roadmap. Cả hai cách đều 0 % CPU khi rảnh; cách này đơn giản hơn và không phải lập lịch lại khi hủy giữa chừng. Tối đa 20 bộ đếm.
2. **`/ground` dành ô cho ghi chú nhanh.** Câu hỏi E4 trùng nhiều từ với các tài liệu bàn giao dài (chính `current_task.md` chứa nguyên câu hỏi). Theo cách chấm điểm ADR-45b, một ghi chú ngắn khó vào Top-3. Quy tắc mới vẫn xác định (cùng file → cùng kết quả) và chỉ có tác dụng khi có ghi chú khớp ít nhất một nửa từ khóa.
3. **Ngữ nghĩa `/calc`.**
   - `^` là lũy thừa, không phải XOR.
   - `log` là logarit tự nhiên (giống C#/Python); có thêm `ln` và `log10`.
   - Số âm in Hex/Bin dạng dấu + độ lớn (`-0x4`), không phải bù hai 64 bit.
   - `round` làm tròn 0,5 ra xa số 0.
   - Không nhân ngầm: `2pi` báo lỗi, phải viết `2*pi`.
4. **Snapshot cục bộ ghi đè `latest_dispatch.json`.** Thẻ Cockpit chuyển sang kết quả tiện ích. Dispatch gần nhất vẫn còn trong SQLite và danh sách "Recent". `/qa`, `/retry` không có `#id` vẫn lấy dispatch mới nhất từ SQLite, không từ file.
5. **Bộ đếm tạo qua MCP sống cùng tiến trình McpTools.** Client MCP đóng thì bộ đếm mất. Bộ đếm tạo từ Cockpit sống cùng HostService.
6. **Overwolf** (✅ đã quyết (5): gỡ khỏi mặc định, xem §15.4). Ghi chú gốc: chỉ đổi giá trị mặc định. `nexus_config.json` trên máy Nhật Hòa (không đưa lên Git) đã có danh sách riêng, nên phải tự thêm `"Overwolf"` vào đó. Khi Overwolf chạy nền, hồ sơ SLA sẽ ở LiveCombat (25 ms) suốt thời gian đó.
7. **Đổi nhãn bộ nhớ trên HUD sang Private Working Set**: để lại TASK-13 như Gemini ghi. Đọc Private WS trong tiến trình cần bộ đếm hiệu năng, không còn là chỉnh nhẹ.
8. **Cú pháp `/note`**: `list` hoặc `list 10` là liệt kê; `list mua sữa` là một ghi chú. `find …` và `search …` luôn là tìm kiếm.
9. **Độ trễ E2** đo toàn bộ đường đồng bộ: phân tích lệnh, chốt khóa API, tính, và ghi nguyên tử `latest_dispatch.json`. Phần tính đo riêng (`compute`) để đối chiếu.

### 15.3 Kết quả kiểm thử — Lần chạy 1: ✅ ALL PASS (kết thúc 17:26, 2026-11-08, nhánh `claude/task-14-local-utilities` @`ec01576`, có build, Claude Code chạy trên máy Nhật Hòa `BITMAPPP`)

Lệnh:
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14-local-utilities
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14.ps1
```
Exit code 0. Report `task14_verification.json`: `allPassed=true`, timestamp `2026-11-08T10:26:12Z`. Build và mọi kiểm thử đạt ngay lần đầu, **không có bản vá nào** trong ca này.

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | ✅ PASS | `dotnet build -c Release -warnaserror`: **0 Warning(s), 0 Error(s)** (6,06 s); 0 file BOM; SQLite 3 212 dòng (≥ 3 001); `quick_notes.md` bị Git bỏ qua; Overwolf có trong `combatProcessPrefixes` mặc định |
| E2 | ✅ PASS | `sqrt(144) + 2^8 = 268 (Hex: 0x10C \| Bin: 0b100001100)`; `0xFF + 0b101 = 260 (Hex: 0x104 \| Bin: 0b100000100)`; `(1 + 2) * 3 - 4 / 2 % 3 = 7`; `10 / 0` → `[CALC ERROR] Chia cho 0.`; 14 ca lỗi đều `[CALC ERROR]`. 30 lần `/calc`, toàn đường đồng bộ: **P50 1,148 ms / P95 1,372 ms** (ngưỡng 1,5 / 2,5; max 1,593); riêng phần tính P50 0,019 / P95 0,027 ms. Token live 60 975 → 60 975, mock 15 576 → 15 576, session 0 → 0, dòng dispatch 3 212 → 3 212 |
| E3 | ✅ PASS | Headless: `/timer 250ms` hết giờ sau **269,3 ms** (ngân sách 400), 0 đối tượng timer khi rảnh, CPU rảnh 0,904 % (< 1 %). HostService thật, Cockpit đang ẩn: `[TIMER EXPIRED] #1: Kiểm thử tự động Gate 14 (250ms)`, hiện Passive HUD `NOACTIVATE\|TOOLWINDOW` (exstyle `0x08080088`), alertMs 15,6; foreground trước = sau = sau render = `0x20400` (HostService và script đo độc lập, trùng nhau). `/timer 60s ToCancel` → `/timer cancel all` hủy 1 → `/timer list` 0 đang chạy |
| E4 | ✅ PASS | `/note` ghi `Mã cổng thử nghiệm của Gate 14 là 49152` (không BOM), `/note find 49152` trả 1 kết quả; khóa giả bị chặn (`[NOTE BLOCKED] Secret Guard…`), không có trong file, `latest_dispatch.json`, SQLite (0 lần). `/ground` (mock) trả 3 nguồn, nguồn thứ ba là `zettelkasten/quick_notes.md#Note 1 · 2026-11-08 10:23:18 UTC`, ngữ cảnh chứa ghi chú, không khởi động lại. Ghi chú test đã xóa (0 → 0) |
| E5 | ✅ PASS | MCP `run_local_utility` calc → Cockpit SYNCSTATE `localUtility=calc source=McpTools` **latencyMs 174,2** (< 300); MCP note có khóa giả: `blocked=true`. HostService Private WS **55,2 MB** (< 60), WorkingSet64 160,1 MB, CPU rảnh 5 mẫu cuối **0 %** (cả 10 mẫu = 0), CPU máy 15 %. Hồi quy Verify-Task11 → 03: task11…task03 đều `allPassed=True` (Task11 E5 gọi Gemini thật `gemini-3.5-flash-lite`, `/qa` PASSED). `git status --porcelain` rỗng |

**Ghi nhận (không chặn Gate):**
- CPU rảnh ở selftest `local-timer` là 0,904 %, sát ngưỡng 1 %. Mẫu này lấy 1 s ngay trong tiến trình selftest vừa khởi động; HostService chạy lâu đo 0 % ở E5. Nếu máy bận, mẫu headless có thể trượt như Gate 11b lần 4.
- Private WS 55,2 MB, cao hơn các lần trước (Gate 11: 50,7 MB) sau thêm ba dịch vụ tiện ích; còn dư 4,8 MB so với ngân sách 60 MB.
- `nexus_config.json` trên máy vẫn chưa có `"Overwolf"` (script in nhắc). Code mặc định đã có; Nhật Hòa tự thêm nếu muốn áp dụng (§15.2 mục 6).

### 15.4 Sau lần chạy 1: Quyết định (5) — gỡ Overwolf khỏi danh sách mặc định (Claude, 17:35, **chưa build**)
- **Quyết định**: Architect và Nhật Hòa chốt Overwolf **không** phải tiến trình chiến đấu. Overwolf chạy nền cả khi không chơi game; nếu nằm trong danh sách thì SLA dispatch ở LiveCombat (25 ms) suốt thời gian đó. Danh sách mặc định trở lại `DeltaForce, TslGame, SGuard`, trùng với `nexus_config.json` trên máy Nhật Hòa.
- **Đã sửa**:
  - `NexusConfig.cs`: gỡ `"Overwolf"`.
  - `Verify-Task06.ps1`: danh sách dự phòng trở lại `DeltaForce, TslGame, SGuard`.
  - `SelfTest.Task14.cs` (`schema14`): điều kiện PASS đổi thành "Overwolf **không** có trong mặc định" (`combatDefaultOk`), báo thêm `defaultCombatProcessPrefixes`.
  - `Verify-Task14.ps1`: bỏ dòng nhắc thêm Overwolf vào `nexus_config.json`.
- **Cần làm**: lần chạy 2 `Verify-Task14.ps1` (có build) trên máy Nhật Hòa; ghi số đo ở §15.5. → ✅ Đã chạy, ALL PASS (§15.5).


### 15.5 Lần chạy 2 (sau Quyết định (5)) — ✅ ALL CHECKS PASS (kết thúc 17:46, 2026-11-08, nhánh `claude/task-14-local-utilities` @`024b63a`, có build, Claude Code chạy trên máy Nhật Hòa `BITMAPPP`)

- **Trước khi chạy**: `git pull --ff-only` lên `024b63a`. Đọc diff của commit: chỉ gỡ `"Overwolf"` và đổi điều kiện `schema14`; `Verify-Task14.ps1` không có thao tác Cấp Đỏ (chỉ xóa file `task14_selftest_*.json` của chính nó, tắt HostService qua pipe). CPU toàn máy 5,5 % (5 × 1 s); không có game hay overlay nào chạy.
- **Không sửa code.** Build và mọi kiểm thử Gate 14 đạt ở cả ba lượt; hai lượt trượt đều do hồi quy, vì lý do ngoài code:

| Lượt | Giờ | Kết quả | Nguyên nhân |
|------|-----|---------|-------------|
| 2a | 17:35–17:38 | ❌ E1–E4 PASS, E5 FAIL | **Repo bị checkout sang `main` lúc 17:35:57**, khoảng 20 s sau khi script bắt đầu (reflog: `checkout: moving from claude/task-14-local-utilities to main`). Claude không chạy lệnh này và không có script nào gọi `git checkout`; có thể một phiên khác (Antigravity, phiên Claude khác hoặc người dùng) đang dùng chung thư mục. Trên `main`, `.gitignore` chưa có `quick_notes.md`, nên Verify-Task06 E5 thấy `?? 03_Local_Data_Vault/zettelkasten/quick_notes.md` (git không sạch) → Task06 FAIL → Task07…11 trượt dây chuyền; Task14 E5 cũng báo git không sạch. Claude checkout lại nhánh task-14 (không sửa gì trên `main`) và theo dõi `.git/HEAD` ở các lượt sau: không đổi nữa |
| 2b | 17:39–17:43 | ❌ E1–E4 PASS, E5 FAIL | Verify-Task09 E5 gọi Gemini thật: `/ask` 2 122 ms ✅, **`/f` timeout 25 010 ms** ❌, `/qa` 2 253 ms ✅ → Task09 FAIL → Task10, 11 trượt dây chuyền. Task08 → 03 PASS; git sạch. Lỗi mạng/Gemini chập chờn, giống Gate 11b lần 3 |
| **2c** | **17:43–17:46** | ✅ **ALL CHECKS PASS (E1–E5)** | Thử một `/ask!` trước: 2 036 ms. Report `task14_verification.json`: `allPassed=true`, `2026-11-08T10:45:51Z`, exit 0 |

**Số đo lượt 2c**:

| # | Kết quả | Số liệu |
|---|---------|---------|
| E1 | ✅ PASS | `dotnet build -c Release -warnaserror`: **0 Warning(s), 0 Error(s)** (1,20 s, build tăng dần sau lượt 2a/2b cùng commit); 0 file BOM; SQLite **3 850** dòng (≥ 3 001); `quick_notes.md` bị Git bỏ qua; **`overwolfInDefault=False`** (Quyết định (5)); `overwolfInConfigFile=False` |
| E2 | ✅ PASS | `sqrt(144) + 2^8 = 268 (Hex: 0x10C \| Bin: 0b100001100)`; `0xFF + 0b101 = 260 (Hex: 0x104 \| Bin: 0b100000100)`; `(1 + 2) * 3 - 4 / 2 % 3 = 7 (Hex: 0x7 \| Bin: 0b111)`; `10 / 0` → `[CALC ERROR] Chia cho 0.` 30 lần `/calc`, toàn đường đồng bộ: **P50 1,092 ms / P95 1,301 ms** (ngưỡng 1,5 / 2,5; min 0,787, max 1,407); riêng phần tính P50 0,019 / P95 0,027 ms. Token live 151 209 → 151 209, mock 40 191 → 40 191, session 0 → 0, dòng dispatch 3 850 → 3 850 |
| E3 | ✅ PASS | Headless: `/timer 250ms` hết giờ sau **268 ms** (ngân sách 400), 0 đối tượng timer, CPU rảnh **0,906 %** (< 1 %). HostService thật, Cockpit đang ẩn: `[TIMER EXPIRED] #1: Kiểm thử tự động Gate 14 (250ms)`, Passive HUD `NOACTIVATE\|TOOLWINDOW` (exstyle `0x08080088`), alertMs **12,94**; foreground trước = sau = sau render = `0x20CDE` (script đo độc lập: `0x20CDE` → `0x20CDE`). `/timer cancel all` hủy 1 → 0 đang chạy |
| E4 | ✅ PASS | `/note` ghi `Mã cổng thử nghiệm của Gate 14 là 49152`; `/note find 49152` → 1 kết quả; khóa giả → `[NOTE BLOCKED] Secret Guard…`, không có trong file, `latest_dispatch.json`, SQLite (0). `/ground` (mock): nguồn thứ ba `zettelkasten/quick_notes.md#Note 1 · 2026-11-08 10:43:00 UTC`, ngữ cảnh chứa ghi chú. Dọn ghi chú test (0 → 0, xóa 1) |
| E5 | ✅ PASS | MCP `run_local_utility` calc → Cockpit SYNCSTATE `localUtility=calc source=McpTools` **latencyMs 158,4** (< 300); MCP note có khóa giả: `blocked=true`. HostService Private WS **54,9 MB** (< 60), WorkingSet64 158,5 MB, CPU rảnh 5 mẫu cuối **0 %**, CPU máy 19 %. Hồi quy task11 → task03 đều `allPassed=True` (Task11 E5 gọi thật `gemini-3.5-flash-lite`, 0 fallback, `/ground` thật trúng cache P50 2,176 / P95 2,772 ms). `git status` sạch |

Số đo hồi quy Task11 ở lượt 2c (10 mẫu sau warm-up, tổng ms): `/ask` P50 1,8 / P95 2,057; `/qa` P50 2,234 / P95 3,643 (một mẫu 4,2); `/g` P50 2,146 / P95 2,712; ADR-14 0 / 28 189 lần đọc hỏng.

**Ghi nhận (không chặn Gate)**:
1. **Dùng chung working copy**: một phiên khác đổi nhánh giữa lúc Verify chạy và làm hỏng lượt 2a. Đề nghị mỗi agent dùng `git worktree` riêng, hoặc thống nhất chỉ một phiên chạy Verify mỗi lần.
2. CPU rảnh của selftest `local-timer` 0,906 % (lần 1: 0,904 %), vẫn sát ngưỡng 1 %.
3. Private WS 54,9 MB (lần 1: 55,2 MB), còn dư 5,1 MB.
4. Token thật trong ngày: 151 209 trước lượt 2c. Mỗi lượt Verify-Task14 đầy đủ (gồm hồi quy có gọi Gemini thật) tốn khoảng 28–29k token thật; ba lượt hôm nay tốn khoảng 86k.
5. **⚠️ Tag `v0.12.0` trỏ sai commit** (phát hiện sau lượt 2c, khi push): `git ls-remote` cho thấy tag annotated `v0.12.0` đã có trên GitHub. Tag do Huynh Nhat Hoa gắn lúc **17:35:58**, trỏ vào **`ced587b`** (`main` trước TASK-14), nên không chứa code TASK-14. Thời điểm này cách lần checkout sang `main` của lượt 2a (17:35:57) một giây, nên lượt 2a nhiều khả năng trượt vì chính thao tác gắn tag này. Claude không sửa tag; xem `gate14_handoff.md` §3 mục 0.

## 16. TASK-14b: Hồi quy hai tầng (ADR-55), GitHub Actions CI (ADR-56), gia cố kiểm thử a/b/c — Ca Claude (2026-11-08)

- **Nhánh**: `claude/task-14b-ci-fast-regression`, tách từ `main` @`089abcb` (đặc tả TASK-14b sau `v0.12.0` @`f354f97`).
- **Trạng thái**: **chưa build** trong môi trường của Claude (không có .NET SDK). Mục này không có số đo trên máy. CI đã được kích hoạt trên GitHub nhưng **job không chạy** vì tài khoản GitHub bị khóa do vấn đề thanh toán (§16.3).

### 16.1 Đã triển khai

| Hạng mục | File | Nội dung |
|----------|------|----------|
| ADR-56 CI | `.github/workflows/nexus-ci.yml` (mới) | Kích hoạt khi push `main`, `claude/**`, `docs/**`, khi mở PR vào `main`, và chạy tay (`workflow_dispatch`). `windows-latest`, `actions/checkout@v4`, `actions/setup-dotnet@v4` 8.0.x; job tự ghim SDK 8 bằng `global.json` tạm (không commit). Các bước: cổng 0 BOM → build HostService và McpTools `-c Release -warnaserror` → smoke McpTools. `permissions: contents: read`, hủy lượt cũ khi push mới, giới hạn 15 phút |
| Cổng 0 BOM | `tools/Test-NexusBom.ps1`, `tools/NexusVerify.Common.ps1` (mới) | Quét mọi file trong `git ls-files`. `*.ps1` **phải có** BOM, mọi file khác **không** được có (§16.2 mục 1) |
| 10 file `.gitkeep` | `01_Foundation_Engine/…`, `02_Agents_Registry/…`, `03_Local_Data_Vault/…` | Mỗi file chỉ chứa đúng 3 byte BOM (và một dòng trống), nên cổng BOM toàn repo trượt ngay. Nay để rỗng (0 byte) |
| Smoke không màn hình | `tools/Invoke-CiSmoke.ps1` (mới) | Gửi JSON-RPC qua stdio tới McpTools với Gemini giả lập: `initialize`, `tools/list`, `run_local_utility` calc (`= 268 (Hex: 0x10C …)`, 0 token), calc `10 / 0` → `[CALC ERROR]`, timer `list`, note có chuỗi giống khóa → bị chặn, `dispatch_agent_squad` → có `dispatchId` |
| ADR-55 hai tầng | `tools/Invoke-NexusRegression.ps1` (mới), `Verify-Task14.ps1`, `Verify-Task11.ps1` | `-FastRegression`: Gate 14 + Verify-Task11 (`-SkipLive -SkipRegression -CpuSamples 4`) + Verify-Task06 (`-SkipRegression`), không gọi Gemini, ngân sách < 90 s; xóa report cũ của 11/06 trước khi chạy để một lần PASS cũ không được tính. `-FullRegression` (mặc định): chuỗi Task11 → 03 như trước. Verify-Task11 thêm tham số `-CpuSamples` (mặc định 10, giữ nguyên hành vi cũ). Bản tóm tắt ghi vào `handoff/regression_<fast|full>.json` (Git bỏ qua) |
| (a) Chốt Git | `NexusVerify.Common.ps1`, `Verify-Task14.ps1`, `Invoke-NexusRegression.ps1`, `Verify-Task14b.ps1` | Ghi lại nhánh và HEAD lúc bắt đầu; kiểm tra sau mỗi E và sau hồi quy; lệch thì dừng ngay, report ghi bước bị lệch. Cuối lượt còn đòi `git status --porcelain` rỗng |
| (b) Nhịp lắng CPU | `Verify-Task14.ps1` E5, `SelfTest.Task14.cs` (`local-timer`) | E5: `/timer cancel all`, xác nhận 0 bộ đếm, chờ 2 000 ms rồi mới lấy mẫu CPU. Selftest: chờ 1 500 ms rồi lấy mẫu 2 s (trước là 1 s ngay lập tức). E5 báo thêm `idleCpuUnderHalfPct` |
| RAM | `Verify-Task14.ps1` E5 | Báo Private WS so với ngưỡng mềm 54 MB (vàng, không trượt) và trần cứng 60 MB (trượt) |
| (c) Tài liệu | `gate14_handoff.md`, `docs/ADR_REGISTRY.md` | Ghi tag `v0.12.0` đã gắn lại tại `f354f97`; ADR-55, ADR-56 ghi chi tiết đã triển khai; ADR-59 ghi 4 quyết định Gate 14 và chuyển sang "Chốt (`v0.12.0`)"; ADR-09 thêm ngưỡng mềm 54 MB |
| Nghiệm thu | `tools/Verify-Task14b.ps1` (mới) | E1–E5 theo đặc tả (§16.3) |

### 16.2 Diễn giải cần Architect xác nhận

1. **Quy tắc BOM cho `.ps1` ngược với câu chữ của đặc tả.** Đặc tả yêu cầu mọi file, kể cả `.ps1`, đều không có BOM. Làm vậy thì cả 12 script hiện có đều trượt. Theo quy ước dự án, `.ps1` bắt buộc UTF-8 **có** BOM, vì Windows PowerShell 5.1 đọc UTF-8 không BOM theo bảng mã ANSI và làm hỏng tiếng Việt. Cổng CI vì thế kiểm tra hai chiều: `.ps1` thiếu BOM là lỗi, file khác có BOM là lỗi.
2. **CI build từng `.csproj`, không build file solution.** File solution của dự án là `NEXUSHomeHub.slnx`, cần SDK 9.0.200 trở lên. Đặc tả ghi `.sln` và SDK 8. CI ghim SDK 8 và build HostService (kéo theo Core và CockpitUI) cùng McpTools. Máy Nhật Hòa vẫn build `.slnx` như cũ.
3. **CI không chạy selftest của HostService.** Các selftest đó cần dữ liệu thật (≥ 3 001 dòng SQLite) và có ngưỡng thời gian; máy chủ chia sẻ của GitHub không hợp để chấm ms. CI chỉ chạy smoke McpTools không màn hình, như đặc tả.
4. **Tầng nhanh (`-FastRegression`) chọn Task06** làm Gate nền tảng đại diện, như đặc tả. Verify-Task06 có `git fetch origin`, nên cần mạng tới GitHub (không cần Gemini).
5. **Không ép thu hồi bộ nhớ trước khi đo RAM.** Đặc tả cho phép "thu hồi bộ nhớ đệm tạm nếu cần". Claude chưa dùng `EmptyWorkingSet` trước phép đo, vì nó làm Private WS giảm tạm thời rồi tăng lại khi trang nhớ được dùng; con số sẽ đẹp nhưng không phản ánh trạng thái chạy thật. Nếu Architect muốn, có thể thêm một phép đo phụ "sau khi thu hồi" bên cạnh phép đo chính.
6. Đặc tả ghi ô thứ 3 của `/ground` dưới ADR-50. Claude ghi quyết định này vào ADR-59 kèm tham chiếu ADR-45b, vì ADR-50 là quy tắc đếm token live/mock, còn cách xếp hạng `/ground` thuộc ADR-45b.

### 16.3 Kết quả kiểm thử
- **CI lần đầu** (`b276a95`, 10:58 UTC): workflow được kích hoạt đúng sự kiện, nhưng GitHub **không chạy job**: *"The job was not started because your account is locked due to a billing issue."* Chưa có kết quả build từ CI.
- **Máy Nhật Hòa**: chưa chạy (**chưa build**). Lệnh:
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14b-ci-fast-regression
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14b.ps1
```
