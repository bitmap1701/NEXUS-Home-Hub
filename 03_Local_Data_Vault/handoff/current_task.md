# CURRENT TASK SPECIFICATION — TASK-16b (ADR-66)

- **Task ID:** `TASK-16b` (Cockpit UI v2 Phase 2: Layout, Markdown, Opaque Backdrop & Lazy Alliance Mode)
- **ADR Number:** `ADR-66`
- **Target Version:** `v0.15.5` (hoặc `v0.16.0` nếu đẩy `/ctx` sang `v0.16.5`)
- **Git Branch:** `claude/task-16b-cockpit-ui-v2-layout-lazy`
- **Base Commit:** `main @76158b1` (`v0.15.0`)
- **Role Assignments:** XR Cockpit Interaction Specialist + Desktop App Engineer + Reality Checker

---

## 1. Mục Tiêu & Phạm Vi (Scope & Naming Disambiguation)

Để không trùng ký hiệu với `U2` (YouTube -> EQ Samsung K450) và `U3` (Webhook HUAWEI WATCH GT 6) của `TASK-16` (`ADR-65`), toàn bộ nâng cấp giao diện Cockpit UI v2 trong task này sử dụng tiền tố **`CUI-`**:

1. **`CUI-Opaque` (Sửa thực tế #1 — Nền HUD Đặc Hơn):**
   - Tăng độ đục nền chính của cửa sổ HUD ở chế độ làm việc (`330 px` và `640 px`) lên **`#F8090D16` (97% Alpha)** hoặc **`#FC090D16` (99% Alpha)** để chữ của cửa sổ ứng dụng phía sau tuyệt đối không lộ qua gây rối mắt trên màn hình MSI G272.
   - Chế độ **Xuyên thấu (`Ghost / Click-Through`)** khi bật chủ động vẫn giữ độ mờ `60%` và cho chuột đi xuyên qua (`WS_EX_TRANSPARENT | WS_EX_LAYERED`).
2. **`CUI-Markdown` (Sửa thực tế #2 — Hiển thị Markdown Cơ Bản trong Ô Câu Trả Lời):**
   - Viết bộ phân tích Markdown nội bộ siêu nhẹ (`LightweightMarkdownFormatter`, 0 thư viện NuGet ngoài, dùng `Span<char>` / Regex tĩnh) chuyển đổi văn bản trả lời sang `TextBlock.Inlines` (hoặc `FlowDocumentScrollViewer` tắt thanh công cụ):
     * `**chữ in đậm**` -> `Run` với `FontWeight = FontWeights.Bold`, màu sáng `#F8FAFC`.
     * `` `inline code` `` -> `Run` với font `Cascadia Mono` / `Consolas`, nền `#1E293B`, màu chữ `#38BDF8`.
     * Khối ```` ```code``` ```` -> Khối văn bản monospace có nền tách biệt `#0F172A` và viền mảnh `#3338BDF8`.
     * Danh sách đầu dòng `- ` hoặc `* ` -> Đổi thành ký tự `• ` căn lề rõ ràng.
3. **`CUI-Layout` (Bố Cục Mới & Lịch Sử Hỏi–Đáp Đa Lượt):**
   - Tổ chức lại vùng hiển thị ở mức `330 px` và `640 px`: tách rõ **Khung Câu Trả Lời Chính** (có cuộn mượt và hiển thị `CUI-Markdown`) và **Cột/Thanh Lịch Sử Hỏi–Đáp Gần Nhất** (đọc từ SQLite WAL, chỉ hiện các bản ghi thật `is_selftest == false`, bấm chuột hoặc phím mũi tên để xem lại câu trả lời cũ trong `< 2 ms`).
4. **`CUI-Lazy` & `CUI-Alliance` (Chế Độ Lười & Bảng Điều Phối Liên Minh):**
   - **Tất cả phím tắt mới đều là In-App Shortcuts (chỉ nhận khi cửa sổ Cockpit đang Focus, KHÔNG đăng ký Global Hotkey mới):**
     * `Alt + Enter`: Chép ngay vào Clipboard lời nhắn / prompt chuẩn của bước kế tiếp trong quy trình Liên minh (dựa trên trạng thái Gate/Verify hiện tại trong `latest_dispatch.json`).
     * Phím số `1–9` (khi ô nhập lệnh đang trống hoặc không giữ tiêu điểm gõ chữ): Kích hoạt nhanh hành động gợi ý tương ứng sau câu trả lời.
     * `F1`: Bật/tắt lớp phủ tra cứu nhanh phím tắt (Cheat Sheet).
     * `Alt + A`: Bật/tắt **Bảng Điều Phối Liên Minh (`CUI-Alliance`)** hiển thị: Gate hiện tại, nhánh Git, trạng thái Verify gần nhất, RAM Private WS và trạng thái ca làm việc của Claude Code.
   - **MCP Tool mới `notify_shift_complete`**: Cho phép Claude Code gọi khi hoàn thành ca viết code/Verify để bật chỉ báo sáng trên thanh HUD `40 px` (kèm âm báo nhẹ hệ thống nếu cấu hình bật).
   - **Nhận biết thụ động ảnh mới trong Clipboard**: Chỉ kiểm tra `Clipboard.ContainsImage()` đúng 1 lần vào khoảnh khắc cửa sổ Cockpit được đánh thức (`Summon/Wake`), tốn `0% CPU` khi rảnh. Nếu có ảnh mới (khác mã băm lần trước), hiện gợi ý nhỏ `[Ảnh mới trong Clipboard — Bấm Alt+I hoặc gõ /img]`.
5. **Trả nợ Kiểm thử Gate 16 (`WAKE Latency 40px -> 330px`):**
   - Đo độ trễ **`WAKE`** khi nhấn tổ hợp `Ctrl + Shift + Space` lúc HUD đang thu gọn ở mức `40 px` bung mở lên mức `330 px` và đặt tiêu điểm vào ô nhập lệnh (Mục tiêu: `P95 <= 16,0 ms`).

---

## 2. Ràng Buộc Kỹ Thuật & An Toàn (Hard Constraints)

- **Tài nguyên:** RAM rảnh (`Private WS`, trung vị sau khi đã lắng) **<= 45,0 MB**, trần cứng **<= 52,0 MB**; CPU rảnh **= 0,0 %**; **0 vòng lặp timer mới**.
- **Mã hóa file:** UTF-8 không BOM cho mọi file mã nguồn/tài liệu; riêng `Verify-Task16b.ps1` bắt buộc có **UTF-8 BOM**.
- **Cấp Đỏ:** Không tự ý xóa file ngoài vùng tạm, không đụng Registry, không kill process, không xin quyền Admin, không ghi `Program Files`.
- **Chi phí:** 0 đồng (không gọi API tính phí).

---

## 3. Tiêu Chí Nghiệm Thu (`Verify-Task16b.ps1` — E1 đến E5)

- **E1 (Build, BOM & Baseline Integrity):**
  - `dotnet build -c Release -warnaserror` đạt `0` warnings, `0` errors.
  - Kiểm tra mã hóa: `0` file mã nguồn có BOM; các file `.ps1` có UTF-8 BOM hợp lệ.
- **E2 (`CUI-Opaque` & `CUI-Markdown` Verification):**
  - Xác nhận mã màu nền HUD ở chế độ `330 px` / `640 px` có kênh Alpha `>= 0xF8` (`>= 97%` độ đục).
  - Unit/Integration test cho `LightweightMarkdownFormatter`: chuỗi có `**bold**`, `` `code` ``, ```block``` và `- list` sinh đúng số lượng `Inline` định dạng mà không văng ngoại lệ với dữ liệu rỗng hoặc ký tự đặc biệt; thời gian phân tích `< 1,0 ms`.
- **E3 (`CUI-Layout`, `CUI-Lazy`, `CUI-Alliance` & MCP `notify_shift_complete`):**
  - Kiểm chứng chuyển đổi lịch sử hỏi–đáp đọc từ SQLite WAL (`< 2,0 ms`, lọc sạch bản ghi `is_selftest`).
  - Kiểm chứng phím nội bộ `Alt + Enter` tạo đúng chuỗi handoff vào Clipboard, `F1` và `Alt + A` bật/tắt đúng panel, và công cụ MCP `notify_shift_complete` cập nhật trạng thái ca làm việc trong `< 2,0 ms`.
- **E4 (Gate 16 Debt — Độ trễ `WAKE` `40 px -> 330 px` trên `Ctrl + Shift + Space`):**
  - Đưa HUD về trạng thái thu gọn `40 px`, kích hoạt luồng `WAKE` (`Ctrl + Shift + Space`) liên tiếp 10 mẫu ($N=10$): chiều cao cửa sổ chuyển sang `330 px` và ô nhập lệnh nhận Focus với độ trễ **`P50 <= 10,0 ms`**, **`P95 <= 16,0 ms`**.
- **E5 (Footprint & Full Regression Chain):**
  - Chạy toàn bộ chuỗi hồi quy `Verify-Task16.ps1` -> `Verify-Task03.ps1` đạt `100% ALL PASS`.
  - Trung vị RAM rảnh đã lắng (`Settled Private WS`) **<= 45,0 MB** (trần cứng `52,0 MB`), CPU rảnh **= 0,0 %**.
  - Ghi đầy đủ số liệu vào `03_Local_Data_Vault/handoff/gate16b_handoff.md` và `03_Local_Data_Vault/handoff/qa_report.md`.