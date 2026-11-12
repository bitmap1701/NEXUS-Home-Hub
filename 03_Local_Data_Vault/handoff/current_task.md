# TASK-20: Ánh Xạ Khẩu Lệnh Tiếng Việt Sang Lệnh Cục Bộ (`VoiceCommandMapper` Cho `Alt+V`, 0 Token) (`ADR-75` -> `v0.20.0`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-12 20:29 (kèm đóng dấu `v0.19.0` @`fc658e7`). Ghi chú của Claude: lúc nhận, tag `v0.19.0` chưa gắn (`main` = `3336b87`) nên nhánh tách từ đầu nhánh 19 (`fc658e7`). Về "mục ưu tiên hơn trong ROADMAP": không có mục nào chặn TASK-20; đề xuất sau TASK-20 ở `gate20_handoff.md` §4.

- **ADR & Phiên bản đích**: `ADR-75` -> `v0.20.0` (hoặc mời Claude đề xuất nếu `ROADMAP_FUTURE.md` có mục ưu tiên hơn)
- **Nhánh**: `claude/task-20-voice-command-mapper` (tách từ `main` sau khi chốt `v0.19.0` @ `fc658e7`; trạng thái nhánh mới: chưa kiểm)
- **Phạm vi (Lean change)**:
  1. Thêm hàm thuần `VoiceCommandMapper.TryMap(string transcript)` (không regex nặng, không cấp phát thừa) ngay ở đầu ra của `Alt+V`: nếu câu tiếng Việt/Anh khớp mẫu khẩu lệnh cục bộ phổ biến (ví dụ: `"kiểm tra mạng"` / `"radar mạng"` -> `/net`, `"kiểm tra cổng <N>"` -> `/net port <N>`, `"trạng thái mic"` -> `/mic status`, `"ngữ cảnh"` -> `/ctx`, `"hẹn giờ <N> phút"` -> `/timer <N>m`, `"báo cáo qa"` -> `/qa`), tự động chuyển thành cú pháp lệnh `/` tương ứng trong `InputBox.Text` (kèm gợi ý `[khẩu lệnh -> /...]`); nếu là câu hỏi tự do thì giữ nguyên văn bản gốc để người dùng sửa/nhấn `Enter`.
  2. Mở rộng `--selftest voice-command-map` kiểm chứng bảng ánh xạ khẩu lệnh có dấu/không dấu (`<= 1,0 ms`/lượt) và tích hợp vào luồng `Alt+V`.
- **Cổng nghiệm thu (`Verify-Task20.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0/0`; BOM = `0` byte; `\bRegisterHotKey\s*\(` = `1`; `ADR-74` chốt `v0.19.0` và đăng ký `ADR-75`.
  - **E2**: `--selftest voice-command-map` ánh xạ đúng `>= 8` mẫu khẩu lệnh tiếng Việt (có dấu & không dấu) sang lệnh `/` và giữ nguyên câu hội thoại thường trong `<= 5,0 ms` tổng cộng.
  - **E3**: Selftest `Alt+V` đầu-cuối với mẫu âm thanh/stub trả về `"kiểm tra mạng"` -> `InputBox.Text` nhận đúng `"/net"` trong `<= 250 ms`, `PCM = 0 B`.
  - **E4**: `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
  - **E5**: `Private WS` rảnh `<= 45,0 MB` (mục tiêu `<= 35,0 MB`), `CPU rảnh = 0%`, chuỗi hồi quy `Verify-Task19 -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
