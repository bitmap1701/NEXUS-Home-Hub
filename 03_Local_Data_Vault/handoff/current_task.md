# CURRENT TASK SPECIFICATION — TASK-15 (ADR-62)

- **Task ID:** `TASK-15` (`/ctx` Active Window Context + Sensitive Window Denylist + Anti-Cheat Safe Guard)
- **ADR Number:** `ADR-62`
- **Target Version:** `v0.16.0`
- **Git Branch:** `claude/task-15-active-window-ctx-safeguard`
- **Base Commit:** `main @939da6b` (`v0.15.5` — sau khi fast-forward Gate 16b)
- **Role Assignments:** Desktop App Engineer + Security Architect + XR Cockpit Interaction Specialist + Reality Checker

---

## 1. Mục Tiêu & Phạm Vi Kỹ Thuật (Deliverables)

Triển khai lệnh `/ctx <câu hỏi>` và công cụ MCP `get_active_window_context` để trích xuất ngữ cảnh cửa sổ đang làm việc trên Windows với 2 lớp khiên bảo vệ cứng (**Anti-Cheat Safe Guard** và **Sensitive Window Denylist**), tích hợp hiển thị lên bố cục Cockpit UI v2 (`ADR-66`) mà không làm suy giảm độ trễ `WAKE 40 -> 330 px`:

1. **Bắt `HWND` Không Độ Trễ Khi Đánh Thức (`Zero-Overhead HWND Capture on Wake`):**
   - Tại Gate 16b, độ trễ `WAKE 40 -> 330 px` đạt `P50 = 8,58 ms` (chỉ còn dư `1,42 ms` so với ngưỡng `10,0 ms` do `WindowHeightMs` chiếm `9,67 ms`).
   - Do đó, khi nhấn phím tắt gọi Cockpit (hoặc chuyển trạng thái `WAKE`), hệ thống **CHỈ** gọi duy nhất 1 hàm Win32 `GetForegroundWindow()` trước khi `Activate()` cửa sổ HUD để lưu số nguyên `IntPtr _lastExternalHwnd` (nếu `HWND` đó không thuộc tiến trình NEXUS).
   - **CẤM TUYỆT ĐỐI** gọi `GetWindowText`, `OpenProcess`, `QueryFullProcessImageName` hay đọc Clipboard trong luồng `WAKE`. Mọi bước kiểm tra khiên và đọc tiêu đề chỉ chạy **khi người dùng thực sự thực thi lệnh `/ctx` hoặc gọi MCP `get_active_window_context`**.
2. **Khiên Chống Anti-Cheat (`AntiCheatSafeGuard` — Lớp Chặn Số 1):**
   - Khi `/ctx` được gọi, từ `_lastExternalHwnd` (hoặc cửa sổ ngoài gần nhất), lấy `ProcessId` qua `GetWindowThreadProcessId` và lấy đường dẫn tệp thực thi bằng `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION = 0x1000)` + `QueryFullProcessImageNameW`.
   - **CẤM TUYỆT ĐỐI** dùng quyền `PROCESS_VM_READ`, cấm `UIAutomation`, cấm `SetWindowsHookEx`, cấm gửi `WM_GETTEXT` sang tiến trình khác.
   - Kiểm tra tên tiến trình (không phân biệt hoa/thường) và danh sách tiến trình đang chạy đối với bộ lọc Anti-Cheat:
     * Dịch vụ Anti-Cheat: `EasyAntiCheat`, `EasyAntiCheat_EOS`, `BEService`, `BattlEye`, `vgc`, `vgk`, `FACEIT`, `faceitclient`, `Ricochet`, `GameGuard`, `xhunter1`.
     * Trò chơi bảo vệ nghiêm ngặt: `valorant`, `Valorant-Win64-Shipping`, `cs2`, `r5apex`, `LeagueClient`, `League of Legends`, `GenshinImpact`, `StarRail`, `PUBG-Win64-Shipping`, `FortniteClient-Win64-Shipping`.
   - Nếu phát hiện bất kỳ tiến trình Anti-Cheat nào khớp: **Chặn đồng bộ trong `P95 <= 0,5 ms`**, không đọc tiêu đề hay Clipboard, trả về thông báo an toàn `[CTX_BLOCKED: Anti-Cheat Safe Guard]`, ghi nhận `ctx_status = "BLOCKED_ANTICHEAT"` và tiêu tốn **`0` token**.
3. **Khiên Chặn Cửa Sổ Nhạy Cảm (`SensitiveWindowDenylist` — Lớp Chặn Số 2):**
   - Sau khi qua `AntiCheatSafeGuard`, đọc tiêu đề cửa sổ bằng `GetWindowTextW` và kiểm tra cả `ProcessName` lẫn `WindowTitle` với danh sách từ khóa cấm (Denylist):
     * Trình quản lý mật khẩu & Xác thực: `keepass`, `bitwarden`, `1password`, `lastpass`, `dashlane`, `authy`, `credential manager`, `windows security`.
     * Trình duyệt ẩn danh: `inprivate`, `incognito`, `private browsing`, `tor browser`.
     * Tài chính, Khóa bí mật & Thông tin nhạy cảm: `.env`, `id_rsa`, `id_ed25519`, `dpapi`, `secret`, `password`, `mật khẩu`, `banking`, `ngân hàng`, `otp`, `2fa`, `seed phrase`, `metamask`.
   - Nếu khớp bất kỳ mẫu nào trong Denylist: **Chặn đồng bộ trong `P95 <= 0,5 ms`**, hủy toàn bộ chuỗi tiêu đề/ngữ cảnh trong bộ nhớ (không lưu tiêu đề nhạy cảm vào SQLite WAL hay `latest_dispatch.json`), trả về `[CTX_BLOCKED: Sensitive Window Denylist]`, ghi `ctx_status = "BLOCKED_SENSITIVE"` và tiêu tốn **`0` token**.
4. **Luồng Thực Thi `/ctx <câu hỏi>` & MCP Tool `get_active_window_context`:**
   - Nếu vượt qua cả 2 khiên an toàn:
     * Thu thập: `process_name`, `window_title` (cắt tối đa `200` ký tự), và đoạn văn bản ngữ cảnh đi kèm (nếu người dùng vừa copy văn bản vào Clipboard và cửa sổ không bị chặn, lấy tối đa `1 000` ký tự văn bản thuần; tổng khối `[ACTIVE_WINDOW_CONTEXT]` không vượt quá **`1 200` ký tự**).
     * Hỗ trợ lệnh `/ctx` không kèm câu hỏi (hoặc `/ctx status`): Chỉ kiểm tra và hiển thị thẻ ngữ cảnh cửa sổ hiện tại (tốn `0` token Gemini) để người dùng xem trước NEXUS đang nhìn thấy cửa sổ nào.
     * Khi gõ `/ctx <câu hỏi>`: Đóng gói `[ACTIVE_WINDOW_CONTEXT: app=<process_name> | title=<window_title> | text=<optional_clip>]` kèm `<câu hỏi>` gửi qua đường ống Gemini (tích hợp `GeminiResponseCache` với `GroundingFingerprint` tính từ `process_name + window_title + text` để gọi lặp trúng cache trong `<= 3,0 ms`).
     * Trong SQLite WAL, các lượt chạy từ `/selftest` hoặc `Verify-Task15.ps1` bắt buộc gán `is_test = 1` (theo Quyết định #5 của Gate 16b) để không làm bẩn lịch sử hiển thị thật trên Cockpit UI v2.
     * Cập nhật `ROADMAP_FUTURE.md`: Ghi nhận mốc `v0.15.5` cho `TASK-16b (ADR-66)` và `v0.16.0` cho `TASK-15 (ADR-62)`.

---

## 2. Ràng Buộc Kỹ Thuật & An Toàn (Hard Constraints)

- **Tài nguyên:** RAM rảnh (`Private WS`, trung vị đã lắng) **<= 45,0 MB**, trần cứng **<= 52,0 MB**; CPU rảnh **= 0,0 %**; **0 vòng lặp timer mới**.
- **Bảo toàn tốc độ `WAKE` của Gate 16b:** Giữ nguyên `WAKE 40 -> 330 px` đạt **`P50 <= 10,0 ms`** và **`P95 <= 16,0 ms`**.
- **Mã hóa file:** UTF-8 không BOM cho mọi file mã nguồn/tài liệu; riêng `Verify-Task15.ps1` bắt buộc có **UTF-8 BOM**.
- **Cấp Đỏ:** Không tự xóa file ngoài vùng tạm, không đụng Registry, không kill process, không xin quyền Admin, không ghi `Program Files`.
- **Chi phí:** 0 đồng.

---

## 3. Tiêu Chí Nghiệm Thu (`Verify-Task15.ps1` — E1 đến E5)

- **E1 (Build, Zero-BOM & Selftest `ctx-safeguard`):**
  - `dotnet build -c Release -warnaserror` đạt `0` warnings, `0` errors.
  - Kiểm tra Zero-BOM toàn bộ kho mã nguồn; các file `.ps1` có UTF-8 BOM.
  - Selftest nội bộ kiểm tra đầy đủ các trường hợp biên của `AntiCheatSafeGuard` và `SensitiveWindowDenylist` (với `is_test = 1`).
- **E2 (`AntiCheatSafeGuard` & `SensitiveWindowDenylist` Latency & Leak Proof):**
  - Kiểm thử 10 mẫu ($N=10$) với tập mẫu tiến trình Anti-Cheat (`vgc`, `EasyAntiCheat`, `cs2`, `valorant`) và tập mẫu cửa sổ nhạy cảm (`Bitwarden`, `InPrivate`, `.env - Notepad`, `Vietcombank OTP`):
    * 100% bị chặn chính xác (`BLOCKED_ANTICHEAT` / `BLOCKED_SENSITIVE`).
    * Độ trễ chặn đồng bộ **`P95 <= 0,5 ms`**.
    * Xác nhận `0` token tiêu tốn và **không xuất hiện chuỗi tiêu đề nhạy cảm** trong SQLite WAL hay `latest_dispatch.json`.
- **E3 (Luồng `/ctx` Hợp Lệ, Cache Hit & MCP `get_active_window_context`):**
  - Kiểm thử `/ctx status` và `/ctx <câu hỏi>` trên cửa sổ hợp lệ (ví dụ: `pwsh` / `code` / `notepad` với tiêu đề thường):
    * Trích xuất đúng `process_name`, `window_title`, giới hạn ngữ cảnh `<= 1 200` ký tự.
    * Gọi lặp lại câu lệnh `/ctx <câu hỏi>` trên cùng ngữ cảnh trúng `GeminiResponseCache` với **`P95 <= 3,0 ms`**, tốn `0` token.
    * Công cụ MCP `get_active_window_context` phản hồi đúng cấu trúc JSON trong **`<= 2,0 ms`**.
- **E4 (Bảo toàn Độ Trễ `WAKE 40 -> 330 px` & Giao diện UI v2):**
  - Đo lại `WAKE 40 -> 330 px` ($N=10$ mẫu) sau khi gắn chốt bắt `_lastExternalHwnd`: xác nhận không suy thoái hiệu năng, đạt **`P50 <= 10,0 ms`** và **`P95 <= 16,0 ms`**.
  - Kiểm tra sau khi chạy selftest `/ctx`, danh sách lịch sử thật trên Cockpit UI v2 không bị lọt bất kỳ bản ghi `is_test = 1` nào.
- **E5 (Footprint, CPU `0,0 %` & Full Regression Chain `Task16b` -> `Task03`):**
  - Chạy chuỗi hồi quy đầy đủ bao gồm cả `Verify-Task16b.ps1`, `Verify-Task16.ps1` về `Verify-Task03.ps1` đạt **`100% ALL PASS`**.
  - Trung vị RAM rảnh đã lắng (`Settled Private WS`) **<= 45,0 MB** (trần cứng `52,0 MB`), CPU rảnh **= 0,0 %**.
  - Ghi báo cáo chi tiết vào `03_Local_Data_Vault/handoff/gate15_handoff.md` và `03_Local_Data_Vault/handoff/qa_report.md` §22.