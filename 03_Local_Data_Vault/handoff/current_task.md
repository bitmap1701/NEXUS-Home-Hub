# TASK-18b: Dịch Vụ Worker TCP 5055 Giải Mã Whisper Tiếng Việt (Nitro 5 & Loopback Host) (`ADR-73` -> `v0.18.2`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-12 18:36. Nhánh tách từ `main` = `012036b` (tag `v0.18.1`). Đặc tả trước: lịch sử git của file này.

- **ADR & Phiên bản đích**: `ADR-73` -> `v0.18.2`
- **Nhánh**: `claude/task-18b-nitro-whisper-worker` (tách từ `main` sau khi fast-forward `177e6eb` và gắn tag `v0.18.1`; trạng thái nhánh mới: chưa kiểm)
- **Phạm vi (Lean change)**:
  1. Thêm chế độ chạy `--stt-worker [--bind <ip:port>] [--cli <path>] [--args <template>]` (mặc định `0.0.0.0:5055` hoặc `192.168.1.22:5055` trên Nitro 5) nhận luồng WAV `16kHz Mono` theo đúng hợp đồng LAN của `TASK-18a`, gọi trình thực thi Whisper cục bộ (`whisper-cli` / lệnh cấu hình) và trả kết quả UTF-8 về cho `LocalSttService`.
  2. Giới hạn bảo mật & tài nguyên: chỉ chấp nhận kết nối từ loopback (`127.0.0.1`) hoặc dải LAN `192.168.1.x` (ưu tiên `192.168.1.212`), kích thước gói WAV tối đa `<= 2 MB`, tuần tự hóa `SemaphoreSlim(1,1)` để không tràn RAM/CPU khi có nhiều yêu cầu, và dọn sạch file `.wav` tạm (`0 B` rò rỉ đĩa).
  3. Kèm script khởi động nhanh `01_Foundation_Engine\tools\Start-NitroSttWorker.ps1` cho trạm Nitro 5 và selftest đầu-cuối `--selftest stt-worker-e2e` chạy tự thân trên PC `bitmappp` (không phụ thuộc Nitro 5 đang bật hay tắt).
- **Cổng nghiệm thu (`Verify-Task18b.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0` lỗi / `0` cảnh báo; BOM = `0` byte; chuyển `ADR-72` sang `Chốt v0.18.1` và đăng ký `ADR-73` trong `docs/ADR_REGISTRY.md`.
  - **E2**: `--selftest stt-worker-e2e` dựng `--stt-worker` trên cổng tạm loopback, gửi mẫu WAV từ `LocalSttService` (`provider = "lan"`) -> nhận đúng văn bản tiếng Việt UTF-8 có dấu trong `<= 500 ms` (với mock/stub CLI) và dọn sạch `100%` file tạm.
  - **E3**: Kiểm chứng bảo vệ gói tin: gửi gói rỗng, quá cỡ (`> 2 MB`) hoặc cắt ngang kết nối -> worker từ chối an toàn trong `<= 100 ms` (`0` crash, vẫn nhận yêu cầu hợp lệ kế tiếp); nếu `whisper-cli` chưa cài trên máy thì trả thông báo lỗi chuẩn để `LocalSttService` tự lùi về `SAPI` trong `<= 250 ms`.
  - **E4**: Độ trễ `WAKE 40 -> 330 px` trên `bitmappp` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
  - **E5**: `Private WS` rảnh của `HostService` trên `bitmappp` đạt `<= 45,0 MB` (mục tiêu `<= 36,0 MB`), `CPU rảnh = 0%`, chuỗi hồi quy `Verify-Task18a -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
