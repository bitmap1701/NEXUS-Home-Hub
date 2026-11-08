# Đề xuất Cockpit UI v2 — Ca Claude (2026-11-09, 03:05–03:20)

- **Người đề xuất**: Claude (ca viết code), theo yêu cầu của Nhật Hòa: "giao diện dễ dùng, hợp với người lười".
- **Gửi tới**: Gemini Web (Architect) để duyệt, đặt số task và xếp vào lộ trình. Đề xuất này **chưa có code**.
- **Mockup**: canvas "NEXUS Cockpit UI v2" trên claude.ai của Nhật Hòa, 6 khung. Link là riêng tư; Gemini chỉ xem được khi Nhật Hòa chia sẻ hoặc gửi ảnh chụp. Mọi khung được mô tả bằng chữ ở mục 3 bên dưới.
- **Nguồn**: ảnh chụp Cockpit thật lúc 03:10 (giao diện v1.0, cửa sổ 1080×440 trên màn G272) và `CockpitShellWindow.xaml`.

## 1. Lỗi trên Cockpit hiện tại (từ ảnh chụp lúc 03:10)

| # | Lỗi | Bằng chứng | Hướng sửa |
|---|-----|-----------|-----------|
| B1 | **Dữ liệu kiểm thử lẫn vào lịch sử thật** | ACTIVE SQUAD hiện #5701 `NEEDS_WORK (Strike 2/3)`, Task "Kiểm thử mớ lại PASSED…", Evidence "test chập chờn"; RECENT DISPATCHES có `#5700 ESCALATED - 3 STRIKES`; Audit Logs 22 662 | Gắn nhãn `selftest` cho mọi bản ghi do selftest / Verify tạo và ẩn chúng trên HUD, hoặc cho selftest dùng một file SQLite riêng |
| B2 | Hai số Private WS khác nhau cùng lúc; dòng "Working Set 144.5 MB" dễ gây hiểu nhầm | Thanh trên ghi 31,6 MB, cột trái ghi 32,9 MB | Một lần đọc cho mọi chỗ hiển thị; bỏ dòng Working Set (không phải chỉ số ADR-09) |
| B3 | Ô câu trả lời chỉ hiện log hệ thống | "[03:10:20.815] Wakeup (Hotkey_AltSpace_Show) in 0.68 ms." | Ô này luôn hiện câu trả lời AI gần nhất; log xuống một dòng trạng thái |
| B4 | Chữ bị cắt ở 4 chỗ | "Gate: Reality…", "Nguồn:…", gợi ý lệnh "/timer…", thanh phím tắt cụt hai đầu | Cắt có chủ đích kèm tooltip, hoặc thay bằng bảng lệnh (U3) và bảng phím tắt F1 (U2) |
| B5 | Chỉ báo thẻ dễ hiểu nhầm | `[0 thẻ \| Alt+P: Pin kết quả]` ngay dưới "PINNED MFD CARDS (1/5)" | Ghi rõ "Câu trả lời hiện tại: 0 thẻ" |
| B6 | Cockpit đè lên cửa sổ đang làm việc | Ảnh chụp: Cockpit nằm trên terminal | Kiểm tra lại việc đặt cửa sổ lên G272; thêm kéo lên / xuống và xuyên thấu (U1) |

## 2. Ràng buộc giữ nguyên
- RAM rảnh trong cổng hiện hành (ADR-09); danh sách và lịch sử có giới hạn số dòng, ô cố định như Right MFD (ADR-60).
- CPU 0 % khi rảnh: không hiệu ứng chuyển động liên tục, không timer khi rảnh.
- Không `RegisterHotKey` mới: mọi phím mới chỉ có tác dụng khi Cockpit ở Command mode (như `Alt+C` / `Alt+P`).
- Không vẽ ảnh lên HUD (ADR-61).
- Không gửi gì ra ngoài khi người dùng chưa bấm; mọi tính năng trong đề xuất này không tốn phí.

## 3. Các phần đề xuất (theo khung mockup)

### U1 — Kéo lên / kéo xuống, xuyên thấu (khung 6)
- 3 mức: **Thu gọn** 40 px (một dòng: RAM, CPU, trạng thái liên minh; có câu trả lời mới thì nháy nhẹ, không tự bung), **Nửa** 330 px (câu trả lời gần nhất + ô lệnh), **Đầy** 640 px.
- Kéo thanh dưới để đổi độ cao, thả ra thì bám mức gần nhất; nhấp đúp: Thu gọn ↔ Nửa. Kéo thanh trên để dời trong G272. Nhớ độ cao và vị trí.
- Passive rảnh 30 s → tự thu gọn (tắt được).
- **Xuyên thấu**: mờ 60 %, chuột đi qua xuống cửa sổ phía sau (`WS_EX_TRANSPARENT` + `WS_EX_LAYERED`); `Ctrl+Shift+Space` để bấm lại vào Cockpit.

### U2 — Chế độ lười (khung 5) và bảng phím tắt (khung 4)
- **`Alt+Enter` = làm tiếp**: Cockpit đọc `current_task.md` / `gate<N>_handoff.md`, biết vòng liên minh đang ở bước nào và copy đúng thứ cần dán (lời nhắn cho Gemini ở mục 5, hoặc prompt cho Claude Code ở mục 2). Người dùng chỉ Ctrl+V.
- **Thông báo khi Claude Code xong ca**: tool MCP mới (Cấp Xanh) để Claude Code đẩy một thẻ "xong ca, Gate X …" lên HUD.
- **Nhận biết clipboard**: khi có ảnh mới (Win+Shift+S) và Cockpit đang mở, hiện "Ảnh mới — hỏi gì?" với 3 câu có sẵn (Enter / 2 / 3). Không gửi khi chưa bấm.
- **Bấm số sau câu trả lời**: 1 Copy code, 2 Ghim, 3 Nói ngắn hơn, 4 Viết test (lấy từ các thẻ của `WidgetCardExtractor`).
- Chỉ cần nhớ 3 phím: `Alt+Enter`, `1–9`, `Esc`; còn lại ở bảng **F1**.

### U3 — Bảng lệnh `/` và chọn agent `@` (khung 2)
- Gõ `/` → danh sách lệnh kèm mô tả và ví dụ, lọc theo chữ, `Tab` điền, lệnh vừa dùng lên đầu.
- Gõ `@` → gợi ý trong 283 agent (ví dụ `@Security Architect`), rồi gõ câu hỏi; ô ACTIVE SQUAD bấm được để đổi agent.

### U4 — Bảng điều phối liên minh (khung 3, `Alt+A`)
- Task hiện tại, E1–En, việc kế tiếp và ai làm; nút Copy lời nhắn cho Gemini (`Alt+G`) và Copy prompt cho Claude Code (`Alt+K`); danh sách Gate đã đóng; thành viên và bảng tin từ Claude Code qua MCP. Chỉ đọc file trong máy.

### U5 — Bố cục mới và lịch sử hỏi–đáp (khung 1)
- Telemetry cột trái gom thành một dòng trạng thái; cỡ cửa sổ Gọn / Chuẩn / Cao; cỡ chữ chỉnh được.
- Lịch sử vài lượt hỏi–đáp gần nhất (giới hạn dòng), mỗi lượt có Copy / Ghim / Hỏi tiếp / Chạy lại.
- Lượt đang chạy hiện các bước Điều phối → (Nén ảnh) → Gemini → QA, số giây, `Esc` hủy.

### Để sau, chỉ làm khi Nhật Hòa yêu cầu
- **Gọi Claude trực tiếp từ Cockpit** (Claude API hoặc Claude Code chạy nền): có thể tốn phí, nên phải hỏi Nhật Hòa trước. Không xếp vào lộ trình.
- Hỏi bằng giọng nói (công tắc "Mic 16kHz VAD Stream" đã có): nhận dạng giọng nói chạy trên máy tốn RAM, phải ở tiến trình riêng.

## 4. Thứ tự đề xuất (Architect đặt số task)

| Bước | Nội dung | Lý do |
|------|---------|-------|
| 1 | B1–B6 + U1 | Sửa hiển thị sai và che màn hình; nhỏ, dùng được ngay |
| 2 | U2 + U4 | Bớt việc chép lời nhắn qua lại, đúng mục đích "phục vụ sự lười" |
| 3 | U3 | Bớt phải nhớ lệnh |
| 4 | U5 | Thay đổi bố cục lớn nhất, làm sau cùng |

Tất cả xếp sau TASK-12c (vì Gate 12 chưa đóng). Mỗi bước một task, một Gate.
