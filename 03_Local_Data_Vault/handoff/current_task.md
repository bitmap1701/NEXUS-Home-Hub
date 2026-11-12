# TASK-19: Phím Tắt Nội Bộ `Alt+V` (Voice-to-Input) Trên Cockpit, Cấu Hình `ggml-small-q5_1` & Chống Nhiễu `Task15 E3` (`ADR-74` -> `v0.19.0`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-12 19:12 (kèm đóng dấu `v0.18.2` @`0c553cc`, 18b.1, 18b.3). Ghi chú của Claude: lúc nhận, tag `v0.18.2` chưa gắn (`main` = `012036b`) nên nhánh tách từ đầu nhánh 18b (`3336b87` = `0c553cc` + tài liệu). Trong mã có đúng 1 lời gọi `RegisterHotKey` (vòng lặp đăng ký các phím toàn cục cũ) + 1 khai báo `extern`; E1 đếm lời gọi.

- **ADR & Phiên bản đích**: `ADR-74` -> `v0.19.0`
- **Nhánh**: `claude/task-19-cockpit-voice-input` (tách từ `main` sau khi chốt `v0.18.2` @ `0c553cc`; trạng thái nhánh mới: chưa kiểm)
- **Phạm vi (Lean change)**:
  1. Gắn phím tắt nội bộ cửa sổ `Alt+V` (trong `OnPreviewKeyDown`, **không** thêm `RegisterHotKey` toàn cục): nhấn lần 1 bắt đầu thu `/ptt` (hiện chỉ báo `● REC` trên thanh trạng thái), nhấn lần 2 (hoặc hết thời gian thu) dừng thu -> chạy `LocalSttService` -> điền văn bản tiếng Việt nhận dạng được thẳng vào ô nhập lệnh của Cockpit để sửa/gửi.
  2. Cập nhật `Start-NitroSttWorker.ps1` và tài liệu mặc định trỏ tới `ggml-small-q5_1.bin` (`18b.1`); áp dụng retry 1 lần + `[SLA-WARN]` cho `Verify-Task15 E3 cacheHits P95` (`18b.3`).
- **Cổng nghiệm thu (`Verify-Task19.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0/0`; BOM = `0` byte; chỉ có duy nhất `1` `RegisterHotKey`; `ADR-73` chốt `v0.18.2` và đăng ký `ADR-74`.
  - **E2**: Selftest luồng `Alt+V` (bắt đầu thu -> dừng sớm -> nhận văn bản STT giả lập -> điền vào `InputBox.Text` và đặt con trỏ cuối dòng) hoàn tất `<= 300 ms`, `PCM = 0 B`.
  - **E3**: Kiểm chứng `Verify-Task15 E3` retry + `[SLA-WARN]` hoạt động đúng và `Start-NitroSttWorker.ps1` cấu hình chuẩn `ggml-small-q5_1.bin`.
  - **E4**: `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
  - **E5**: `Private WS` rảnh `<= 45,0 MB` (mục tiêu `<= 36,0 MB`), `CPU rảnh = 0%`, chuỗi hồi quy `Verify-Task18b -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
