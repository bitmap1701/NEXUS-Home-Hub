# NEXUS Home Hub — Future Roadmap (v1.0.0 to v3.0+)

> **Trạng thái tài liệu**: thiết kế chính thức. Gemini Web (Architect & Reality Checker) đã duyệt 100 % các góp ý của Claude ngày 2026-11-08; các góp ý đó đã được hợp nhất vào thân tài liệu.
> **Tài liệu sống**: build và cập nhật dần. Mỗi task vẫn có đặc tả riêng ở `03_Local_Data_Vault/handoff/current_task.md`.
> **Số ADR**: tra [`docs/ADR_REGISTRY.md`](docs/ADR_REGISTRY.md) trước khi gán số. Mọi ADR mới của lộ trình bắt đầu từ **ADR-50**, số tiếp theo còn trống là **ADR-65**.
> **Mốc hiện tại**: `main` có tag `v0.11.0`; Gate 11b (`v0.11.1`) đang nghiệm thu.

Tài liệu này định hình chiến lược phát triển dài hạn cho hệ thống **NEXUS Home Hub**, mở rộng từ cột mốc hoàn thành cơ sở hạ tầng văn bản (`v0.11.1` / `v1.0.0`) sang kỷ nguyên trợ lý không gian cục bộ, đa phương thức và tự động hóa thông minh.

---

## 🗺️ Tổng Quan Các Giai Đoạn Phát Triển

| Giai đoạn | Phiên bản Mục tiêu | Trọng tâm Kỹ thuật & Nghiệp vụ | Trạng thái |
| :--- | :--- | :--- | :--- |
| **Phase 1: Foundation & Cockpit** | `v0.1` – `v0.8` | Nhân C#, SQLite WAL, Local MCP, HostService, phím tắt AULA HERO 68 HE, CockpitUI 144 Hz, vòng Dev-QA và cầu chì 3 lần thử | **ĐÃ HOÀN THÀNH** |
| **Phase 2: Resilient Text AI Core** | `v0.9` – `v0.11.1` | Khóa DPAPI, `/retry` & hội thoại đa lượt (ADR-36/37/43), Backoff + Jitter, QuotaGuard (ADR-47), Cache (ADR-44b), Vault Grounding (ADR-45b), tách token mock & đo P50/P95 (ADR-50..53) | **ĐANG CHỐT `v0.11.1` (Gate 11b)** |
| **Phase 3: Local Utilities, Widgets, Vision & Context** | `v0.12` – `v0.16` | Tiện ích 0 token, thẻ tiện ích tương tác, hỏi bằng ảnh / chụp màn hình, ngữ cảnh cửa sổ — **thứ tự khóa theo ADR-58** | **CHUẨN BỊ TRIỂN KHAI** |
| **Phase 4: Voice & Smart Home** | `v0.17` – `v0.19` | Giọng nói Push-to-Talk, điều khiển thiết bị Smart Home (Cấp Đỏ) | **ĐỊNH HƯỚNG** |
| **Phase 5: Autonomous Workflows & Release** | `v0.19` – `v1.0.0` | Tự động hóa theo lịch/sự kiện, tự sao lưu và bảo trì SQLite WAL, đóng gói bộ cài chính thức `v1.0.0` | **ĐỊNH HƯỚNG** |
| **Phase 6: Beyond 1.0 (Advanced OS)** | `v1.x` – `v3.0+` | FTS5 rồi mới Vector RAG, Multi-Agent Swarm, Edge SLM (có điều kiện phần cứng), Spatial Cockpit | **TẦM NHÌN DÀI HẠN** |

---

## 🧱 Ràng Buộc Bất Biến Cho Mọi Phase (ADR-57)

Mọi đặc tả mới phải ghi rõ cách giữ các ràng buộc sau, và Gate phải đo chúng.

| Ràng buộc | Giá trị chuẩn | Đã đo (Gate 9–11) | Áp dụng khi mở rộng |
|-----------|---------------|-------------------|---------------------|
| RAM private của HostService | < 60 MB (ADR-09) | 40,7 – 49,5 MB | Ảnh, âm thanh, chỉ mục không nằm lâu trên heap: buffer trả về pool, không giữ bản sao |
| CPU khi rảnh | < 1 %, mục tiêu 0 % | 0 % | Không vòng lặp chờ bận, không polling cửa sổ; timer dùng một bộ đếm hệ thống |
| Độ trễ đường đồng bộ | `/ask` P95 < 10 ms Desktop / 25 ms LiveCombat (ADR-17); trúng cache theo ADR-52 | Cold Call 9,4 ms; P95 2,2 ms | Lệnh mới phải có bảng đo từng bước như `AskSyncBreakdown` |
| GPU | GTX 1660 dành cho game/IDE; Cockpit dùng iGPU UHD 730 (SoftwareOnly) | — | Không tính năng nào chiếm dGPU khi đang LiveCombat |
| Gói NuGet | Không thêm mới | — | Ưu tiên API có sẵn: WPF/WIC, WinRT, Win32, SQLite FTS5 |
| Bí mật | Khóa chỉ trong DPAPI; chặn chuỗi `AIza…` khi ghi DB và log | — | Ảnh, chữ bôi đen, giọng nói, ghi chú đi qua cùng lớp lọc |
| Cấp thao tác | Xanh / Vàng / Đỏ (`GEMINI.md`) | — | Thiết bị vật lý, tự động hóa, xóa dữ liệu phải gắn cấp trước khi code |
| Ghi file chia sẻ | Ghi nguyên tử: file tạm + replace (ADR-14, ADR-51) | — | Ghi chú `/note`, ảnh tạm, sao lưu cũng ghi nguyên tử |

---

## 🧪 Hạ Tầng Kiểm Thử & CI (ADR-52, ADR-55, ADR-56)

1. **Đo độ trễ luôn theo nhiều mẫu (ADR-52)**: tối thiểu 10 mẫu sau 1 lần warm-up, chấm P50/P95, in toàn bộ mảng mẫu. Không chấm ngưỡng trên một mẫu đơn lẻ.
2. **Chia hai tầng hồi quy (ADR-55)**:
   - **`-FastRegression`**: dùng khi dev hằng ngày. Chạy selftest mock của Gate hiện tại và hai Gate liền trước, khoảng 2 phút, không gọi Gemini thật.
   - **`-FullRegression`**: dùng khi đóng tag release. Chạy toàn bộ chuỗi `Verify-TaskNN → … → Verify-Task03`, kèm gọi thật, đo RAM/CPU trên máy Nhật Hòa.
   - Triển khai ngay sau Gate 11b, áp dụng từ Verify-Task14.
3. **GitHub Actions CI (ADR-56)** trên `windows-latest`, cho các nhánh `main`, `claude/*`, `docs/*`:
   - `dotnet build -c Release -warnaserror` (0 cảnh báo, 0 lỗi);
   - kiểm tra 0 BOM (`.cs`, `.xaml`, `.csproj`, `.md`, `.json`; riêng `.ps1` phải có BOM);
   - giai đoạn sau: thêm các selftest mock không cần màn hình.

   Mục đích: ca nào cũng biết code có build được không mà không phải chờ máy thật. Chỉ phần gọi thật, RAM/CPU và màn hình MSI G272 mới cần máy Nhật Hòa.
4. **Token mock không bao giờ ăn hạn mức thật (ADR-50)**: bộ kiểm thử chạy ở cấu hình chuẩn `maxDailyTokens = 250000`.

---

## 🚀 Phase 3 — Thứ Tự Triển Khai Đã Khóa (ADR-58)

| Bước | Task | Phiên bản | Nội dung | ADR |
|------|------|-----------|----------|-----|
| 0 | **Gate 11b** | `v0.11.1` | Đóng Phase 2 ở cấu hình chuẩn, rồi làm ADR-55 (hai tầng hồi quy) + ADR-56 (CI) | ADR-50..53, 55, 56 |
| 1 | **TASK-14** | `v0.12` | `/calc`, `/timer`, `/note` — 0 token | ADR-59 |
| 2 | **TASK-13** | `v0.13` | HUD Micro-Widgets, `Alt+C`, `Alt+P`, Right MFD | ADR-60 |
| 3 | **TASK-12** | `v0.14` | `/img`, `/snap` + Combat-Mode Shield + Free-Tier Privacy Guard | ADR-61 |
| 4 | **TASK-16** | `v0.15.0` | Cockpit UI v2 Chặng 1 (B1–B6 + U1) + YouTube G272 → EQ K450 (U2) + webhook đồng hồ (U3) — Gate 16 ALL PASS | ADR-65 |
| 5 | **TASK-16b** | `v0.15.5` | Cockpit UI v2 Phase 2: nền đặc, markdown, lịch sử hỏi–đáp, chế độ lười (`Alt+Enter`, phím số), bảng liên minh `Alt+A`, MCP `notify_shift_complete`; WAKE 40 → 330 px đã đo (P50 8,58 ms) — Gate 16b ALL PASS | ADR-66 |
| 6 | **TASK-15** | `v0.16.0` | `/ctx` + Sensitive Window Denylist + Anti-Cheat Safe Guard + MCP `get_active_window_context`; giữ WAKE 40 → 330 px (P50 ≤ 10 / P95 ≤ 16 ms) — Gate 15 ALL PASS lần 3/3 | ADR-62 |
| 7 | **TASK-16c** | `v0.16.5` | Cockpit UI v2 phần nhìn theo 6 bảng thiết kế: 2 cột (cột phải 300 px), thanh đỉnh 44 px, dòng đơn 40 px, chuỗi thẻ ≤ 20 (Card Pool), thẻ đang chạy + `Hủy Esc`, bảng lệnh `/` và `@agent`, thẻ VIỆC TIẾP THEO, thẻ ảnh clipboard, bảng `Alt+A` + `Alt+G` / `Alt+K`; giữ RAM ≤ 45 MB, CPU rảnh 0 %, WAKE P50 ≤ 10 / P95 ≤ 16 ms — chờ Gate 16c — Gate 16c PASS (`a09d860`), 2 số đo WAKE hồi quy ghi [SLA-WARN] | ADR-68 |
| 8 | **TASK-17** | `v0.17.0` | Mở màn Phase 4: `/net` (thẻ Radar trong cột hội thoại, `/net port` / `/net ping` dòng cục bộ, dòng `⚡ NITRO5 (.22)` + `Alt+Q` lật khung ghim ↔ `MẠNG // /NET` ở cột phải), MCP `get_network_radar`, micro `FIFINE K669` + `/mic`, `/ptt` VAD 16 kHz; clipboard chống khóa chết luồng UI; giảm thời gian đổi chiều cao cửa sổ + chuẩn hóa [SLA-WARN] WAKE — Gate 17 ALL PASS (`1a6c2b0`) | ADR-67 |
| 9 | **TASK-17a** | `v0.17.1` | `networkTargets` tùy chọn trong `nexus_config.json` (`/reload`, fallback 4 đích mặc định) + dọn giao diện lúc rảnh (khảo sát: nhiễu bề mặt vẽ WPF, ngưỡng giữ ≤ 45 MB). **Gate 17a chốt `v0.17.1`** (17a.6: theo bằng chứng lần 2/3) | ADR-69 |
| 10 | **TASK-17b** | `v0.17.2` | Chẩn đoán & thu gọn bộ nhớ nền HostService đầy đủ (selftest `host-footprint`, đo biến thể runtime .NET trước khi chọn), dải `[SLA-WARN]` RAM rảnh ≤ 50 MB khi GC heap ≤ 10,5 MB, chạy lại hồi quy 1 lần (17a.6) . **Gate 17b chốt `v0.17.2`** (A/B −10,3 MB) | ADR-70 |
| 11 | **TASK-18** | `v0.18.0` | STT cục bộ cho `/ptt` (SAPI en-US trong tiến trình con ngắn hạn, VAD bỏ qua im lặng, chữ vào ô lệnh, 0 token) . **Gate 18 chốt `v0.18.0`** | ADR-71 |
| 12 | **TASK-18a** | `v0.18.1` | Cầu nối STT tiếng Việt (LAN TCP / CLI stdout UTF-8) cấu hình qua `"stt"`, tự lùi về SAPI en-US . **Gate 18a chốt `v0.18.1`** | ADR-72 |
| 13 | **TASK-18b** | `v0.18.2` | Worker STT TCP 5055 (`--stt-worker serve`, whisper.cpp) cho Nitro 5 / loopback, chặn gói xấu, tuần tự hóa, `Start-NitroSttWorker.ps1` . **Gate 18b chốt `v0.18.2`** (theo bằng chứng) | ADR-73 |
| 14 | **TASK-19** | `v0.19.0` | Alt+V nói → chữ vào ô lệnh Cockpit, mô hình `ggml-small-q5_1`, chạy lại 1 lần + `[SLA-WARN]` cho cache-hit Task15 . **Gate 19 chốt `v0.19.0`** (theo bằng chứng) | ADR-74 |
| 15 | **TASK-20** | `v0.20.0` | `VoiceCommandMapper`: khẩu lệnh tiếng Việt / Anh từ Alt+V → lệnh `/` cục bộ trong ô lệnh, 0 token | ADR-75 |

Lý do thứ tự:
- Tiện ích 0 token dễ kiểm thử, và `/note` làm giàu Vault cho grounding.
- Widgets chỉ dùng dữ liệu đã có.
- Vision và `/ctx` tốn token nhất, lại có rủi ro chống gian lận và quyền riêng tư, nên làm sau khi hạ tầng đo token, cache và CI đã ổn định.

### 1. Zero-Token Local Utilities (TASK-14 / ADR-59)
- **Tiện ích nội bộ không gọi AI**: `/calc` (máy tính biểu thức), `/timer` (đồng hồ đếm ngược trên MFD), `/note` (ghi chú nhanh vào Vault) chạy hoàn toàn cục bộ, 0 token.
- **`/calc`**: parser biểu thức tự viết, an toàn; **không** dùng eval hay `DataTable.Compute`.
- **`/timer`**: hiển thị trên Cockpit; CPU khi rảnh vẫn 0 %. *Đã triển khai (TASK-14): mỗi bộ đếm một `System.Threading.Timer` chạy một lần, không có bộ đếm thì không có timer nào; hết giờ hiện Passive HUD không lấy focus. Chờ Architect duyệt so với bản nháp "một timer duy nhất".*
- **`/note`**: ghi nguyên tử vào `03_Local_Data_Vault/zettelkasten/quick_notes.md` (theo đặc tả TASK-14; bản nháp cũ ghi `notes/`). `VaultGroundingEngine` (ADR-45b) tự tìm thấy ghi chú này khi `/ground`. Nội dung có chuỗi giống khóa API thì chặn như `/ask`.

### 2. Interactive HUD Micro-Widgets (TASK-13 / ADR-60)
- **Actionable Response Cards**: tự động bóc tách khối Code hoặc Checklist thành các thẻ tương tác, dùng parser Markdown tối giản trong Core, có selftest.
- **Phím tắt nhanh**: `Alt + C` (copy code ngay), `Alt + P` (ghim thông tin quan trọng sang Right MFD).
- **Chỉ hoạt động khi Cockpit ở Command mode**, không đăng ký phím toàn cục, vì dễ trùng phím trong game và IDE.

### 3. Multimodal Vision Engine (TASK-12 / ADR-61)
- **Lệnh `/img` & `/snap`**: dán ảnh từ Clipboard (`Ctrl + V`) hoặc chụp nhanh màn hình MSI G272, nén bằng Windows WIC (< 250 KB; WIC có sẵn trong WPF, không thêm NuGet) trong `Task.Run`, giải phóng buffer ngay.
- **Vision Cache**: khóa = SHA-256 của **ảnh đã nén** + câu hỏi + model, lưu trong SQLite WAL; tra lại trong ngưỡng ADR-52, không tốn token.
- **Combat-Mode Shield**:
  - khi đang LiveCombat (`combatProcessPrefixes`, ADR-23), không chụp cửa sổ game có anti-cheat (Delta Force dùng ACE);
  - chỉ chụp màn hình MSI G272 hoặc vùng người dùng chọn, bằng API chụp màn hình chuẩn;
  - không can thiệp tiến trình game.
- **Free-Tier Privacy Guard**:
  - khóa hiện ở gói Free của Google AI Studio, nên nội dung gửi đi có thể được Google dùng để cải thiện sản phẩm;
  - `/snap` luôn cho xem trước và xác nhận trước khi gửi;
  - cảnh báo khi trong ảnh có chữ giống mật khẩu hoặc khóa.

### 4. Active Window Context (TASK-15 / ADR-62, `v0.16.0`)
- **Nợ đo đạc từ Gate 16**: đã trả ở Gate 16b (WAKE 40 → 330 px P50 8,58 / P95 11,24 ms); Verify-Task15 đo lại để chắc `/ctx` không làm chậm bước đánh thức.
- **Lệnh `/ctx`**: nhận diện tiêu đề cửa sổ đang mở (`GetForegroundWindow` + `GetWindowText`) và đoạn văn bản đang bôi đen (UI Automation, chỉ khi người dùng gõ `/ctx`) để đặt ngữ cảnh hỏi.
- **Sensitive Window Denylist**: bỏ qua ô mật khẩu (UIA `IsPassword`), trình quản lý mật khẩu, trang ngân hàng; danh sách chặn cấu hình được trong `nexus_config.json`.
- **Anti-Cheat Safe Guard**: không đọc cửa sổ của tiến trình trong `protectedProcessPrefixes` / `combatProcessPrefixes`; không giữ ngữ cảnh sau khi trả lời.

---

## 🔊 Phase 4 — Voice & Smart Home

- **Giọng nói Push-to-Talk**:
  - một phím trên AULA HERO 68 HE làm PTT, chỉ thu âm khi giữ phím (không nghe liên tục ở Phase 4);
  - đặc tả phải đo CPU khi đang chơi game và ghi rõ âm thanh gửi tới đâu (Gemini Live hay nhận dạng cục bộ).
- **Smart Home**:
  - đi qua cầu nối chuẩn (Home Assistant REST/MQTT), không gọi API riêng của từng hãng;
  - **thao tác vật lý (khóa cửa, bếp, điều hòa) là Cấp Đỏ**: luôn hỏi xác nhận, không bao giờ chạy tự động.

## 🤖 Phase 5 — Autonomous Workflows & Release `v1.0.0`

- **Sao lưu và bảo trì**:
  - sao lưu bằng `VACUUM INTO` (đã dùng ở migration ADR-18);
  - dọn cache hết hạn, `system_audit_logs` quá N ngày và các bản `.invalid-*`;
  - giới hạn kích thước DB hoặc tách DB kiểm thử khỏi DB thật (DB thật đã có 1 770+ dòng điều phối, phần lớn do kiểm thử).
- **Bộ cài `v1.0.0`**:
  - chốt sớm: self-contained hay cần .NET runtime; tự khởi động cùng Windows; gỡ cài đặt sạch;
  - khóa DPAPI gắn theo **tài khoản Windows**: đổi máy hoặc tài khoản phải nạp lại khóa;
  - có bước di trú `nexus_config.json` giữa các phiên bản.

---

## 🦾 Phase 5A–5C — Project J.A.R.V.I.S. (Nhật Hòa + Gemini thống nhất 2026-11-13; đề xuất, chưa khóa thứ tự)

Mỗi mô-đun là một TASK riêng, chỉ phát đặc tả sau khi Gate trước qua **Đồng kiểm 4 bước** (`CLAUDE.md`). Số ADR đặt trước theo thứ tự J1 → J10; phân pha 5A / 5B / 5C do Architect chốt. Cột "Khả thi" là đánh giá ban đầu của Claude (2026-11-13), chưa đo trên PC.

| # | Mô-đun | ADR | Khả thi | Dùng lại / phụ thuộc / rủi ro |
|---|--------|-----|---------|-------------------------------|
| J1 | `LocalTtsService` — đọc thành tiếng cục bộ | ADR-76 | **Cao** | Worker `--stt-worker synth` đã tạo PCM bằng SAPI (TASK-18); thêm phát waveOut. Giọng Việt cần giọng OneCore `vi-VN` (WinRT `Windows.Media.SpeechSynthesis`) — **cần khảo sát PC** |
| J2 | Safe Hands-Free Auto-Run | ADR-77 | **Cao** | Điểm mở 20.1: chỉ tự chạy lệnh chỉ-đọc trong danh sách trắng (`/net`, `/mic status`, `/ptt status`…) từ `VoiceCommandMapper`; lệnh khác vẫn chờ Enter |
| J3 | `/brief` Briefing | ADR-78 | **Cao** | Gom dữ liệu cục bộ có sẵn (giờ, hẹn giờ, `/net`, NITRO5, RAM, việc dở) thành mẫu cố định 0 token; ghép J1 để đọc to |
| J4 | `/room` WoL & IR (Hunonic) | ADR-79 | **Thấp–TB** | Nhật Hòa (2026-11-13, đính chính): bộ IR thật là **Hunonic IR Smart** (hãng Việt, app Hunonic), **không phải Tuya**; đang điều khiển máy lạnh + TV. **Nitro 5 chưa bật WoL**. WoL (gói magic UDP tới Nitro 5): dễ về mã, cần Nhật Hòa tự bật WoL (BIOS nếu có + card LAN có dây + tắt Fast Startup). Khảo sát 2026-11-13: card LAN có dây của Nitro 5 **đã có quyền đánh thức** (`wake_armed`), còn chờ thử gói magic thật (Sleep / Shut down). IR Hunonic: **cổng 6668 đóng** → không có giao thức Tuya cục bộ; chưa thấy API công khai hay tích hợp Home Assistant; đường duy nhất đã biết là mây / trang "Web điều khiển thiết bị" của Hunonic (trang web đó **tốn phí** → loại). **TV Samsung UA43K5520** (Tizen 2016) mở cổng **8001 / 8002**, `/api/v2/` trả thông tin máy → điều khiển phím qua WebSocket LAN, 0 phí (tắt / âm lượng / nguồn; **bật từ trạng thái tắt** qua mạng chưa chắc với đời K, có thể vẫn cần IR; lần kết nối đầu TV có thể hỏi "Cho phép"). → đề xuất **J4a** = WoL Nitro 5 + TV Samsung qua LAN (khả thi), **J4b** = máy lạnh Panasonic (chỉ IR; gác, sau này ESP32 + IR nếu Nhật Hòa đồng ý chi linh kiện). Lệnh IR là thao tác vật lý **Cấp Đỏ**; đi đường riêng của hãng thì **trái luật Phase 4** → cần Architect quyết sau khảo sát |
| J5 | `/nitro` Offload Dispatcher | ADR-80 | **TB** | Mở rộng hợp đồng LAN 5055 (TASK-18a/18b) thành hàng việc; **tiền đề**: worker trên Nitro 5 chạy thật (whisper.cpp chưa cài, chưa đo) |
| J6 | `/combat` eSports Mode | ADR-81 | **TB** | `GameBoost` (ưu tiên tiến trình, ADR-11) đã có; tắt / đóng tiến trình nền là **Cấp Đỏ**; phải né anti-cheat (Vanguard / ACE) |
| J7 | Local Windows OCR 0-Token | ADR-82 | **TB–Cao** | WinRT `Windows.Media.Ocr` (cần TFM `net8.0-windows10.0.19041` cho một worker); thay một phần Vision Gemini (Gate 12) bằng 0 token; ngôn ngữ OCR có sẵn **cần khảo sát PC** |
| J8 | `/deck` iPad Air HUD | ADR-83 | **TB** | Máy chủ HTTP/SSE nhỏ trên LAN (cổng mới, tường lửa, khóa truy cập), iPad dùng Safari; ảnh hưởng RAM HostService phải đo |
| J9 | `/shield` Port Audit | ADR-84 | **Cao** | Mở rộng `/net` radar (listener đã có): cổng đang nghe + tiến trình sở hữu (`GetExtendedTcpTable`), so với danh sách đã biết; chỉ-đọc |
| J10 | `/fit` & Study Tracker | ADR-85 | **Cao** | Bảng SQLite mới + lệnh cục bộ ghi / thống kê, 0 token; theo mẫu `/note`, `/timer` (TASK-14) |

---

## 🌐 Phase 6 — Tầm Nhìn Xa (Beyond v1.0.0)

- **v1.1 (Retrieval Ladder — ADR-63)**: **SQLite FTS5 (xếp hạng BM25)** cho tra cứu tài liệu cục bộ. FTS5 có sẵn trong bản SQLite mà `Microsoft.Data.Sqlite` đang dùng, chỉ mục nằm trên đĩa, không thêm gói, không giữ chỉ mục trên heap.
- **v1.2+ (Vector RAG)**: chỉ làm khi FTS5 không đủ. Cần một ADR riêng để nới ràng buộc "không giữ chỉ mục lớn trên heap / không thêm NuGet" của ADR-57.
- **v1.x (Agentic Workspace)**:
  - hệ thống đa tác tử (Multi-Agent Swarm) phối hợp lập trình và kiểm thử, xây trên nền sẵn có: ba vai Gemini / Claude / Antigravity, cầu chì 3 lần thử, `/retry`, bàn giao ca (ADR-48);
  - bước đầu: chuẩn hóa hồ sơ bàn giao thành dữ liệu máy đọc được (JSON) bên cạnh Markdown.
- **v2.0 (Edge Neural OS — ADR-64)**: mô hình ngôn ngữ nhỏ chạy offline (Edge SLM) và nhận diện giọng nói đánh thức. **Điều kiện phần cứng**:
  - i5-12400 **không có NPU**; UHD 730 yếu;
  - GTX 1660 6 GB chỉ được chạy SLM khi **không ở chế độ LiveCombat**, hoặc sau khi nâng cấp phần cứng;
  - "giọng nói đánh thức liên tục" đi ngược mục tiêu CPU 0 % khi rảnh, phải đo trên máy thật trước khi cam kết.
- **v3.0 (Spatial Holographic Cockpit)**:
  - đưa giao diện buồng lái NEXUS lên không gian thực tế hỗn hợp (Spatial Computing / WebXR / visionOS);
  - bước nhỏ trước đó: một trang web cục bộ chỉ đọc hiển thị trạng thái Cockpit (dùng lại `latest_dispatch.json`), làm nền cho WebXR.

---

## Nhật ký cập nhật

| Ngày | Ai | Nội dung |
|------|----|----------|
| 2026-11-08 | Gemini Web | Bản nháp: Phase 1–6, chi tiết Phase 3–4, tầm nhìn v1.x–v3.0 |
| 2026-11-08 | Claude | Góp ý: trùng số ADR, hạ tầng kiểm thử và CI, ràng buộc bất biến, rủi ro từng tính năng, thứ tự Phase 3 |
| 2026-11-08 | Gemini Web → Claude | Gemini duyệt 100 % góp ý. Claude hợp nhất vào thân tài liệu, đánh số lại ADR của lộ trình từ ADR-50 (ADR-50..64), lập `docs/ADR_REGISTRY.md` |
