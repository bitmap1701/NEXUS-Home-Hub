# CHỈ THỊ TRIỂN KHAI TASK-11b: GIA CỐ HẠ TẦNG KIỂM THỬ, TÁCH QUOTA MOCK & CHUẨN HÓA ADR-14 (v0.11.1)

> Chỉ thị của Gemini Web (Architect & Reality Checker), Nhật Hòa chuyển cho Claude trong chat ngày 2026-11-08 03:53. Claude chép nguyên văn vào đây để chỉ thị có trong repo.

## 1. Khởi tạo Nhánh từ `main` (`v0.11.0`)
Sau khi đã fast-forward `main` tới đầu nhánh `claude/task-11-vault-grounding-cache` và đẩy tag `v0.11.0`:
- Tạo nhánh mới: `git checkout -b claude/task-11b-test-infra-hardening`

## 2. Bốn Hạng mục Kỹ thuật Bắt buộc trong TASK-11b

### Hạng mục A: Tách Bạch Token Mock khỏi Hạn mức Ngày (`GeminiQuotaGuard`) & Khôi phục `maxDailyTokens = 250 000`
1. Đặt lại cấu hình mặc định `maxDailyTokens = 250000` (250 000 tokens/ngày).
2. Trong `GeminiQuotaGuard` và bảng thống kê SQLite WAL:
   - Phân biệt rõ lượt gọi thật (`IsLiveApi = true`) và lượt gọi giả lập kiểm thử (`IsLiveApi = false` / Mock / Stub).
   - Các lượt gọi Mock/Stub trong `Verify-Task03.ps1` -> `Verify-Task11.ps1` chỉ cộng vào bộ đếm kiểm thử cục bộ (`mock_tokens_used`) hoặc phiên test cô lập, TUYỆT ĐỐI KHÔNG cộng dồn vào `live_daily_tokens` của khóa DPAPI thật.
   - Đảm bảo chạy toàn bộ chuỗi hồi quy `Verify-Task11.ps1` -> `Verify-Task03.ps1` với `maxDailyTokens = 250000` đạt 100% PASS mà không bao giờ gây tràn hạn mức ngày.

### Hạng mục B: Khôi phục Ghi Nguyên tử Chuẩn ADR-14 cho `latest_dispatch.json`
1. Loại bỏ cơ chế ghi đè cắt ngắn trực tiếp (`WriteFast` không nguyên tử) trên `latest_dispatch.json`.
2. Khôi phục cơ chế ghi nguyên tử chuẩn ADR-14: Ghi nội dung JSON vào file tạm cùng thư mục (`latest_dispatch.json.tmp`) bằng bộ đệm tối ưu, sau đó gọi `File.Move(tmpPath, targetPath, overwrite: true)` (hoặc `File.Replace`) có đồng bộ khóa luồng để `CockpitUI` và các tiến trình đọc không bao giờ gặp trạng thái file bị cắt cụt giữa chừng.
3. Bổ sung trường `"cache_lookup_ms"` (đo thuần thời gian tra cứu SQLite WAL + xử lý bộ nhớ của `GeminiResponseCache`) bên cạnh `"total_dispatch_ms"` (bao gồm cả bước đổi tên file nguyên tử ADR-14) trong `latest_dispatch.json`.

### Hạng mục C: Chuẩn hóa Phép đo Độ trễ Cache Hit theo Thống kê 10 Mẫu (`P50` & `P95`)
1. Cập nhật `Verify-Task11.ps1` (mục E2, E3, E5): Thay vì chấm ngưỡng cứng `< 2.0 ms` trên 1 lần gọi đơn lẻ dễ bị nhiễu lập lịch Windows, thực hiện vòng lặp đo **10 lần gọi trúng cache liên tiếp ($N=10$, sau 1 lần warm-up)**:
   - Thời gian tra cứu cache thuần (`cache_lookup_ms`): `P95 <= 2.0 ms`.
   - Thời gian toàn trình tính cả ghi nguyên tử ADR-14 (`total_dispatch_ms`): `Median (P50) <= 2.5 ms` và `P95 <= 4.0 ms`.
2. In rõ mảng 10 mẫu đo, giá trị `Min`, `P50 (Median)`, `P95`, `Max` ra nhật ký kiểm thử để Reality Checker đối chiếu.

### Hạng mục D: Cập nhật Model Mặc định sang `gemini-3.5-flash-lite` & Ghi nhận `model_used` Tường minh
1. Chuyển hằng số cấu hình model mặc định từ `gemini-2.5-flash` (đã trả HTTP 404) sang `gemini-3.5-flash-lite`.
2. Ghi tường minh trường `"model_used"` trong `latest_dispatch.json` và nhật ký SQLite WAL. Nếu hệ thống có danh sách model dự phòng (fallback), bất kỳ sự chuyển đổi model nào cũng phải ghi log `[MODEL FALLBACK: <old> -> <new>]` rõ ràng, không chuyển ngầm.
3. Trong `VaultGroundingEngine`, đảm bảo danh sách loại trừ mặc định bỏ qua `latest_dispatch.json`, `*.db*`, `*.tmp`, và sắp xếp phân định hòa điểm xác định theo `(Score DESC, RelativePath ASC, SectionIndex ASC)`.

## 3. Tiêu chí Nghiệm thu Gate 11b (`v0.11.1`)
- `dotnet build -c Release -warnaserror` đạt `0` warnings, `0` errors; `0` file BOM; bảo toàn baseline `1 296/1 296` dòng.
- Chạy `Verify-Task11.ps1` (với thước đo P50/P95 10 mẫu + kiểm tra tính nguyên tử ADR-14) và toàn bộ chuỗi hồi quy `Verify-Task10.ps1` -> `Verify-Task03.ps1` đạt **100% ALL PASS** ngay ở cấu hình chuẩn `maxDailyTokens = 250000` (không cần nâng tạm hạn mức).
- Chạy 1 lượt Live DPAPI E2E xác nhận model `gemini-3.5-flash-lite` hoạt động trực tiếp ngay lần gọi đầu tiên (không phát sinh lỗi 404 trung gian), `HostService Private WS < 60 MB` và `CPU = 0 %`.
- Ghi kết quả vào `03_Local_Data_Vault/handoff/gate11b_handoff.md` và cập nhật `qa_report.md`.
