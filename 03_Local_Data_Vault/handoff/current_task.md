# TASK-18: Giải Mã Giọng Nói Cục Bộ (Local STT) Cho `/ptt` & Micro `FIFINE K669` (`ADR-71` -> `v0.18.0`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-12 11:01. Ghi chú của Claude: lúc nhận, `v0.17.2` chưa gắn tag (`main` = `29f8905`); đặc tả này được commit lên nhánh 17b để đi vào `main` cùng lần fast-forward `v0.17.2`. Đặc tả TASK-17b cũ: lịch sử git của file này.

- **ADR & Phiên bản đích**: `ADR-71` -> `v0.18.0`
- **Nhánh**: `claude/task-18-local-ptt-stt` (tách từ `main` sau khi fast-forward `a8dcf7e` và gắn tag `v0.17.2`; trạng thái nhánh mới: chưa kiểm)
- **Phạm vi (Lean change)**:
  1. Tái sử dụng luồng thu PCM `16kHz 16-bit Mono` + `VAD` của `QuickAgentPtt` (`TASK-17`). Nếu `VAD == false` (im lặng), bỏ qua STT ngay lập tức (`<= 5,0 ms`) và trả về trạng thái im lặng (`0 token`).
  2. Khi `VAD == true`, chuyển bộ đệm WAV trong RAM qua bộ giải mã cục bộ `LocalSttService` (ưu tiên thư viện chuẩn có sẵn trên Windows như `System.Speech.Recognition.SpeechRecognitionEngine` với `SetInputToWaveStream` / `Windows.Media.SpeechRecognition` hoặc CLI cục bộ / worker `192.168.1.22:5055` tùy chọn với timeout `<= 200 ms`), giải phóng sạch bộ nhận dạng ngay sau phiên hoặc khi dọn `[MEM]` để không ăn lẹm quá `2,5 MB` vào phần `10,3 MB` RAM vừa tiết kiệm được.
  3. Lệnh `/ptt` (hoặc `/ptt [giây]` / `/ptt status`) điền văn bản nhận dạng được vào ô nhập của Cockpit (hoặc hiện thẻ cục bộ `[cục bộ · 0 token]`) mà không chặn luồng UI. Hỗ trợ cờ tiêm mẫu âm thanh (`--selftest ptt-stt`) để kiểm thử tự động không cần nói thật vào mic.
- **Cổng nghiệm thu (`Verify-Task18.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0` lỗi / `0` cảnh báo; BOM = `0` byte; chuyển `ADR-70` sang `Chốt v0.17.2` và đăng ký `ADR-71` trong `docs/ADR_REGISTRY.md`.
  - **E2**: `--selftest ptt-stt` với dữ liệu WAV chuẩn: (a) mẫu im lặng -> `VAD = False`, thoát nhanh `<= 5,0 ms`; (b) mẫu có tiếng nói (hoặc engine trả kết quả từ WAV mẫu) -> trả về văn bản khác rỗng trong `<= 1500 ms`, giải phóng bộ đệm PCM về `0 B`, `0` lượt gọi Gemini thật.
  - **E3**: Gọi `/ptt status` và `/ptt test` qua NamedPipe/UI không làm treo luồng giao diện; nếu máy thiếu gói ngôn ngữ OS hoặc tắt worker phụ thì lùi an toàn (`0` exception) trong `<= 300 ms`.
  - **E4**: Độ trễ `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
  - **E5**: `Private WS` rảnh trên `Task18` và chuỗi hồi quy đạt `<= 45,0 MB` (mục tiêu thực tế `<= 41,5 MB` trên nền `38,5 MB` của `17b`; dải `[SLA-WARN] <= 50,0 MB` khi `GC heap <= 10,5 MB`), `CPU rảnh = 0%`, chuỗi `Verify-Task17b -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
