# TASK-18a: Cầu Nối STT Tiếng Việt Ngoài Tiến Trình (CLI / LAN Worker) & Fallback SAPI Cho `/ptt` (`ADR-72` -> `v0.18.1`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-12 17:53. Ghi chú của Claude: lúc nhận, tag `v0.18.0` chưa gắn (`main` = `5b42394`), nên nhánh tách từ đầu nhánh TASK-18 (`2cb6e37`); gộp `main` sau khi gắn tag là fast-forward. Đặc tả TASK-18: lịch sử git của file này.

- **ADR & Phiên bản đích**: `ADR-72` -> `v0.18.1`
- **Nhánh**: `claude/task-18a-vi-stt-bridge` (tách từ `main` sau khi fast-forward `83e2a53` và gắn tag `v0.18.0`; trạng thái nhánh mới: chưa kiểm)
- **Phạm vi (Lean change)**:
  1. Mở rộng `LocalSttService` đọc cấu hình tùy chọn `"stt"` trong `nexus_config.json` (hỗ trợ `/reload`): `{ "provider": "auto|lan|cli|sapi", "lanEndpoint": "192.168.1.22:5055", "cliPath": "", "cliArgs": "", "language": "vi-VN" }`.
  2. Chế độ `lan` gửi gói WAV `16kHz Mono` qua TCP tới `lanEndpoint` (timeout kết nối `<= 200 ms`, đọc kết quả UTF-8 `<= 2000 ms`); chế độ `cli` gọi tiến trình ngoài đọc stdout UTF-8 (`<= 3000 ms`); chế độ `auto` thử `lan` -> `cli` -> tự động lùi về `sapi` (`en-US`) nếu chưa cấu hình hoặc worker tắt (`0` exception).
  3. Mở rộng `--selftest ptt-stt` kiểm chứng cả 3 đường (`lan` loopback giả lập tiếng Việt có dấu UTF-8, `cli` giả lập, và fallback `sapi` khi cổng đóng).
- **Cổng nghiệm thu (`Verify-Task18a.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0` lỗi / `0` cảnh báo; BOM = `0` byte; chuyển `ADR-71` sang `Chốt v0.18.0` và đăng ký `ADR-72` trong `docs/ADR_REGISTRY.md`.
  - **E2**: `--selftest ptt-stt` xác nhận: (a) `lan` loopback trả đúng chuỗi tiếng Việt UTF-8 có dấu trong `<= 300 ms`; (b) `cli` trả đúng chuỗi tiếng Việt UTF-8 trong `<= 800 ms`; (c) khi `lan`/`cli` không khả dụng, tự động fallback về `sapi` trong `<= 250 ms` và giải phóng PCM về `0 B`.
  - **E3**: `/reload` cập nhật nóng cấu hình `"stt"` từ `nexus_config.json` và `/ptt status` hiển thị đúng nhánh provider đang hoạt động (`<= 50 ms`).
  - **E4**: Độ trễ `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
  - **E5**: `Private WS` rảnh trên `Task18a` và chuỗi hồi quy đạt `<= 45,0 MB` (mục tiêu thực tế `<= 40,0 MB`, dải `[SLA-WARN] <= 50,0 MB` khi `GC heap <= 10,5 MB`), `CPU rảnh = 0%`, chuỗi `Verify-Task18 -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
