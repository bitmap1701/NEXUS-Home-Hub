# TASK-14b: Hồi Quy Hai Tầng (`-FastRegression` / `-FullRegression` — ADR-55), GitHub Actions CI (ADR-56) & Gia Cố Hạ Tầng Kiểm Thử

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 14 — Mốc `v0.12.0` @ `f354f97`)
- **Nghiệm thu Gate 14 (`v0.12.0`)**: `PASSED` 100% trên nhánh `main` tại commit `f354f97` (khớp 100% với tag `v0.12.0`).
- **4 Quyết định Kiến trúc Gate 14 Đã Khóa (Ghi nhận vào `docs/ADR_REGISTRY.md` dưới ADR-50 & ADR-59)**:
  1. **One-Shot Timer (`ADR-59`)**: Mỗi bộ đếm `/timer` dùng một timer chạy một lần, 0 đối tượng khi rảnh, báo hết giờ qua `TIMERALERT` ở chế độ Passive HUD (`WS_EX_NOACTIVATE`).
  2. **Slot #3 `/ground` cho Quick Notes (`ADR-50` / `ADR-59`)**: Dành ô thứ 3 của Top-3 đoạn trích `/ground` cho ghi chú trong `quick_notes.md` khớp ít nhất một nửa (`>= 50%`) từ khóa.
  3. **Ngữ nghĩa `/calc` (`ADR-59`)**: Toán tử `^` là lũy thừa (`pow`), `log` là logarit tự nhiên (`ln`), số nguyên âm in dạng `-0x...` và `-0b...`.
  4. **Quản trị RAM trước `TASK-13` (`ADR-09`)**: Giữ trần cứng `Private Working Set < 60.0 MB`, đặt ngưỡng cảnh báo mềm (Soft Warning) ở `54.0 MB`, và kiểm tra mức thu hồi bộ nhớ sau chuỗi selftest để chuẩn bị dư địa RAM cho `TASK-13` (HUD Micro-Widgets & Right MFD).

## 2. Mục Tiêu TASK-14b (ADR-55 + ADR-56 + 3 Gia Cố Hạ Tầng a/b/c)
Tăng tốc vòng lặp phát triển Phase 3 từ ~12 phút xuống ~60 giây và tự động hóa kiểm tra biên dịch trên GitHub:
1. **ADR-55 (Two-Tier Regression Suite)**: Hỗ trợ tham số `-FastRegression` (chạy Gate hiện tại + Gate liền kề trước đó + Smoke Check cốt lõi) và `-FullRegression` (chạy toàn bộ chuỗi `Verify-Task03..14` khi đóng tag release).
2. **ADR-56 (GitHub Actions CI)**: Thiết lập `.github/workflows/nexus-ci.yml` tự động chạy `dotnet build -warnaserror`, kiểm tra 0-byte BOM và chạy kiểm thử CLI không cần màn hình vật lý mỗi khi đẩy nhánh `main`, `claude/**`, `docs/**`.
3. **3 Gia cố Hạ tầng từ Gate 14 (a, b, c)**:
   - **(a) Chốt chặn bất biến Git `HEAD` & Branch**: Ngăn lỗi dịch chuyển nhánh/commit trong lúc bộ `Verify` đang chạy.
   - **(b) Nhịp chờ lắng CPU (CPU Settle Window)**: Chờ ngắn sau selftest của `/timer` trong `Verify-Task14.ps1` trước khi lấy mẫu CPU rảnh để số đo phản ánh đúng trạng thái nghỉ (`< 0.3%`), không bị sát ngưỡng 1%.
   - **(c) Cập nhật tài liệu `gate14_handoff.md` & `docs/ADR_REGISTRY.md`**: Cập nhật dòng trạng thái tag `v0.12.0` trong `gate14_handoff.md` thành đã gắn lại đúng tại `f354f97` và ghi nhận 4 quyết định Gate 14 vào `docs/ADR_REGISTRY.md`.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Hồi Quy Hai Tầng (`-FastRegression` vs `-FullRegression` — ADR-55)
- **Tạo script điều phối hoặc cập nhật quy chuẩn tham số hồi quy**:
  - Hỗ trợ rõ 2 chế độ chạy hồi quy trong script kiểm thử mới nhất (`Verify-Task14.ps1` và script tổng hợp `01_Foundation_Engine/tools/Invoke-NexusRegression.ps1` hoặc tương đương):
    1. **Chế độ `-FastRegression` (Phục vụ dev hàng ngày & kiểm tra nhanh giữa các lần sửa)**:
       - Chỉ chạy: Gate hiện hành (`Task14`) + Gate liền trước (`Task11b`, ở chế độ không bắt buộc gọi mạng nếu dùng kèm cờ skip live hoặc giữ nguyên E1–E4 + cache) + 1 Gate nền tảng đại diện (`Task06` config/state-machine check).
       - Tổng thời gian chạy mục tiêu: `< 90 giây`.
    2. **Chế độ `-FullRegression` (Mặc định khi chốt Gate đóng dấu release tag)**:
       - Chạy đầy đủ toàn bộ chuỗi lịch sử `Task11b`, `Task10`, `Task09`, `Task08`, `Task07`, `Task06`, `Task05`, `Task04`, `Task03`.

### 3.2. GitHub Actions CI Workflow (`.github/workflows/nexus-ci.yml` — ADR-56)
- **Tạo file `.github/workflows/nexus-ci.yml` (lưu chuẩn UTF-8 không BOM)**:
  - **Sự kiện kích hoạt (`on`)**:
    - `push` trên các nhánh: `main`, `claude/**`, `docs/**`
    - `pull_request` vào `main`
  - **Môi trường chạy (`runs-on`)**: `windows-latest` (để hỗ trợ đầy đủ target `net8.0-windows` và WPF của `CockpitUI` / `HostService`).
  - **Các bước kiểm tra bắt buộc trong Job CI**:
    1. `actions/checkout@v4`
    2. `actions/setup-dotnet@v4` với `dotnet-version: '8.0.x'`
    3. **Kiểm tra Zero-BOM (`0-byte UTF-8 BOM Gate`)**: Quét toàn bộ các file `.cs`, `.csproj`, `.sln`, `.md`, `.json`, `.ps1`, `.yml` được theo dõi bởi Git (`git ls-files`), thất bại ngay nếu bất kỳ file nào có 3 byte đầu là `EF BB BF`.
    4. **Biên dịch Nghiêm ngặt (`dotnet build -warnaserror`)**: Chạy `dotnet build 01_Foundation_Engine/NEXUSHomeHub.sln -c Release -warnaserror` (hoặc cấu hình Debug/Release chuẩn của dự án) đạt 0 lỗi, 0 cảnh báo.
    5. **Headless CLI Smoke Test**: Chạy kiểm thử nhanh `NEXUSHome.McpTools` (JSON-RPC stdio: `initialize`, `tools/list`, `run_local_utility` cho `/calc`, `dispatch_agent_squad` với Mock) để xác nhận logic lõi chạy xanh ngay trên máy chủ CI của GitHub (nhờ đó khi Claude đẩy nhánh từ môi trường không có `.NET SDK`, GitHub Actions sẽ biên dịch và báo kết quả tức thì).

### 3.3. Gia Cố Hạ Tầng Kiểm Thử (a, b, c) & Kiểm Soát RAM Trước `TASK-13`
1. **(a) Chốt chặn Bất biến Git `HEAD` & Branch trong lúc chạy `Verify`**:
   - Ở đầu script kiểm thử (`Verify-Task14.ps1` và hàm/script dùng chung), chụp lại `$initialBranch = (git rev-parse --abbrev-ref HEAD).Trim()` và `$initialHead = (git rev-parse HEAD).Trim()`.
   - Ở cuối script (sau khi chạy xong hồi quy), kiểm tra lại `branch` và `HEAD` hiện tại khớp 100% với `$initialBranch` và `$initialHead` (cùng `git status --porcelain` sạch).
2. **(b) Nhịp Chờ Ổn Định CPU (CPU Settle Window) trong `Verify-Task14.ps1` & Đo RAM**:
   - Trước khi bắt đầu đo các mẫu CPU nền ở E5 của `Verify-Task14.ps1`, đảm bảo mọi timer thử nghiệm đã hoàn tất/hủy sạch và chờ một nhịp lắng ngắn (`1500 ms` – `2000 ms`) để luồng cảnh báo `TIMERALERT` và I/O của bài test E3/E4 kết thúc hoàn toàn, giúp số đo CPU nền phản ánh trung thực trạng thái nghỉ (`< 0.3%` thay vì `~0.9%`).
   - Ghi nhận rõ trong log E5 cả ngưỡng cảnh báo mềm (`Soft Warning: 54.0 MB`) và trần cứng (`Hard Limit: 60.0 MB`); thực hiện thu hồi bộ nhớ đệm tạm sau khi đóng luồng selftest nếu cần để giữ `Private Working Set` gọn gàng nhất trước khi bước vào `TASK-13`.
3. **(c) Cập nhật `gate14_handoff.md` & `docs/ADR_REGISTRY.md`**:
   - Cập nhật ghi chú trong `03_Local_Data_Vault/handoff/gate14_handoff.md` xác nhận tag `v0.12.0` đã được xóa khỏi commit cũ và gắn lại chuẩn xác tại `f354f97`.
   - Cập nhật `docs/ADR_REGISTRY.md` bổ sung chi tiết đã chốt của `ADR-55`, `ADR-56` và 4 quyết định của Gate 14 (`ADR-59`).

## 4. Verification Steps (Nghiệm Thu Gate 14b — `Verify-Task14b.ps1` hoặc `Verify-Task14.ps1`)
1. **E1 (Build `-warnaserror` & Repository-Wide Zero-BOM Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo.
   - Quét toàn bộ file trong `git ls-files` (gồm cả `.github/workflows/nexus-ci.yml`, `gate14_handoff.md`, `docs/ADR_REGISTRY.md`, `current_task.md`) đều đạt 0-byte BOM.
2. **E2 (Git `HEAD` & Branch Immutability Guard)**:
   - Xác nhận cơ chế kiểm tra bất biến `HEAD` và `branch` hoạt động đúng trong suốt phiên chạy kiểm thử.
3. **E3 (`-FastRegression` Speed & Coverage Check — ADR-55)**:
   - Chạy thử chế độ `-FastRegression`: xác nhận hoàn tất toàn bộ trong `< 90 giây` và kiểm tra đủ Gate 14 + Gate liền trước.
4. **E4 (Timer CPU Settle Window & Memory Hygiene)**:
   - Chạy lại `Verify-Task14.ps1`: xác nhận nhờ nhịp chờ lắng CPU, mức CPU nền ở E5 giảm sâu về vùng rảnh (`< 0.5%`), `Private Working Set < 60 MB`.
5. **E5 (GitHub Actions CI Workflow Validation — ADR-56)**:
   - Khi đẩy nhánh `claude/task-14b-ci-fast-regression` lên GitHub, xác nhận workflow `.github/workflows/nexus-ci.yml` có cấu trúc hợp lệ (và tự động kích hoạt chạy xanh trên GitHub Actions).