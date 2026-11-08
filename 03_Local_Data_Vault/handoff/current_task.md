# TASK-13a: Giải Phẫu & Ép Giảm Bộ Nhớ Cockpit (`Private WS <= 50 MB`), Chuẩn Hóa Nhãn Bộ Nhớ HUD & Theo Dõi `[SLA-WARN]` Drift

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 14b — Mốc `v0.12.1`)
- **Nghiệm thu Gate 14b (`v0.12.1`)**: `PASSED` 100% ở lượt chạy 4 và đã fast-forward lên `main` kèm tag `v0.12.1` (Build 0/0, 87 file 0 BOM; chốt chặn Git 4/4; `-FastRegression` hoàn tất trong 54.9 s; CPU Cockpit 0% sau nhịp lắng 2 s; GitHub Actions CI xanh 10/10).
- **3 Quyết định Kiến trúc Đã Chốt Từ Gate 14b**:
  1. **Mở `TASK-13a` (Pre-TASK-13 Memory Slimming)**: Vì `Private Working Set` đã chạm vùng cảnh báo vàng lần thứ hai (`57.7 MB` / trần cứng `60.0 MB`), cần đo giải phẫu từng thành phần bộ nhớ trong `HostService + CockpitUI` và ép giảm về vùng an toàn **`<= 50.0 MB`** (tạo ít nhất `8–10 MB` dư địa trước khi thêm các thẻ Micro-Widgets và Right MFD ở `TASK-13`).
  2. **Đồng bộ Nhãn Bộ Nhớ Trên HUD (Ghi nhận từ Gate 11b)**: Thay thế chỉ số `PrivateMemorySize64` (hiện ~94.2 MB trên thanh trạng thái HUD gây lệch với thước đo ADR-09) bằng đúng chỉ số **`Private Working Set`** khớp với phép đo nghiệm thu của các Gate.
  3. **Theo dõi `[SLA-WARN]` Chống Suy Giảm Ngầm (`ADR-55` Drift Tracking)**: Ghi nhận tổng số `[SLA-WARN]` và chi tiết từng metric bị cảnh báo vàng ở mỗi lượt chạy `-FastRegression` vào bảng tóm tắt, `qa_report.md` và file handoff; gắn cờ `[DRIFT-ALERT]` nếu một lượt chạy có `> 2` cảnh báo `[SLA-WARN]` hoặc cùng một metric bị cảnh báo vàng ở 3 Gate liên tiếp.

## 2. Mục Tiêu TASK-13a (Cockpit Memory Profiling, Slimming & HUD Label Alignment)
1. **Giải phẫu bộ nhớ (`MemoryBreakdown` / `/mem` command)**: Biết chính xác 57.7 MB `Private Working Set` đang nằm ở đâu (Managed Heap, SQLite WAL page cache, bộ đệm chuỗi `AgentRegistry`/`VaultGrounding`, hay WPF UI/Undo buffers).
2. **Kéo giảm `Private Working Set` sau chuỗi kiểm thử về dưới ngưỡng cảnh báo vàng (`<= 52.0 MB`, mục tiêu `<= 50.0 MB`)** mà không làm giảm tốc độ P50/P95 của `/ask`, `/ground`, `/calc`.
3. **Hiển thị đúng `Private Working Set` trên HUD** và bổ sung tổng kết `[SLA-WARN SUMMARY]` vào hạ tầng `-FastRegression`.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Bộ Đo Giải Phẫu Bộ Nhớ (`MemoryDiagnostics` & Lệnh `/mem` trên CockpitUI)
- Xây dựng hàm chẩn đoán bộ nhớ nhẹ (có thể gọi qua lệnh `/mem` hoặc từ `/status` và script kiểm thử) trả về các thành phần chính:
  - `PrivateWorkingSetMb`: Chỉ số `Private Working Set` chuẩn ADR-09 (đọc từ `PerformanceCounter("Process", "Working Set - Private", ...)` hoặc Win32 `QueryWorkingSetEx` / P/Invoke tương đương đang dùng trong `Verify`, có fallback an toàn).
  - `ManagedHeapAllocatedMb`: `GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0)`.
  - `AgentRegistryCacheBytes` & `VaultIndexCacheBytes`: Ước lượng dung lượng chuỗi đang giữ trong bộ nhớ của 283 agents và bộ chỉ mục Vault.
- Ghi rõ bảng so sánh **`Before Slimming` vs. `After Slimming`** vào `qa_report.md` và `gate13a_handoff.md`.

### 3.2. Các Biện Pháp Gọt Giảm Bộ Nhớ Không Ảnh Hưởng Độ Trễ (Memory Slimming)
Thực hiện các tối ưu trúng đích dựa trên số đo giải phẫu (giữ kỷ luật Minimal Change):
1. **Tối ưu SQLite Page Cache (`PRAGMA shrink_memory` / `cache_size`)**:
   - Trên kết nối SQLite WAL hoặc sau khi hoàn tất các thao tác truy vấn/migration lớn, giới hạn `PRAGMA cache_size = -1024;` (1 MB) hoặc gọi `PRAGMA shrink_memory;` hợp lý để SQLite không tích lũy hàng chục MB bộ đệm trang khi bảng `agent_dispatch_logs` vượt 3.000+ dòng.
2. **Tối ưu Bộ đệm WPF (`CockpitUI`)**:
   - Đặt `IsUndoEnabled = false` trên mọi điều khiển văn bản chỉ dùng để hiển thị hoặc đặt `UndoLimit = 10` trên ô `CommandInputBox` (ngăn WPF lưu trữ toàn bộ lịch sử chuỗi qua hàng trăm lần gõ/dispatch trong bài test).
   - Giới hạn chiều dài chuỗi hiển thị trực tiếp trên các thẻ UI (chỉ hiển thị bản tóm tắt/preview vừa khung nhìn trên màn MSI G272, dữ liệu đầy đủ đã nằm trong SQLite WAL và `latest_dispatch.json`).
3. **Tối ưu Bộ đệm Chuỗi trong `AgentRegistry` / `VaultGrounding` / `PerformanceCounter`**:
   - Nếu `AgentRegistry` hoặc `VaultGrounding` đang giữ nguyên văn toàn bộ nội dung dài không cần thiết trong RAM, chuyển sang chỉ giữ metadata nhẹ hoặc giới hạn bộ đệm LRU nhỏ cho các Agent/file vừa truy cập.
   - Nếu dùng `PerformanceCounter` để đọc `Private Working Set` trên HUD, tái sử dụng một thực thể duy nhất hoặc dùng phương pháp đọc nhẹ không rò rỉ bảng计数器 (Performance Counter category table) của Windows (lưu ý: khởi tạo `new PerformanceCounter(...)` trên Windows đôi khi nạp toàn bộ bảng Perflib rộng ~3–5 MB vào tiến trình; nếu đo thấy chính `PerformanceCounter` gây tốn 3–5 MB, hãy dùng Win32 API `K32GetProcessMemoryInfo` / `PrivateUsage` hoặc `QueryWorkingSetEx` nhẹ chỉ tốn 0 KB!).

### 3.3. Chuẩn Hóa Nhãn Bộ Nhớ Trên HUD (`CockpitUI`)
- Trên thanh trạng thái đỉnh (Top Bar) và kết quả lệnh `/status` của `CockpitUI`:
  - Đổi từ việc hiển thị `PrivateMemorySize64` (~94.2 MB) sang hiển thị **`Private WS: <XX.X> MB`** (hoặc `RAM (Priv WS): <XX.X> MB / 60 MB`) để con số người dùng nhìn thấy trên màn hình **MSI Optix G272** khớp với thước đo nghiệm thu của ADR-09.

### 3.4. Ghi Nhận `[SLA-WARN]` Vào Handoff & Phát Hiện Suy Giảm (`ADR-55`)
- Trong bộ khung chạy `-FastRegression` (`Verify-Task*.ps1` / `Invoke-NexusRegression.ps1`):
  - Thu thập danh sách mọi cảnh báo `[SLA-WARN]` phát sinh trong lượt chạy.
  - Ở cuối lượt chạy, in dòng tổng kết chuẩn:
    `[SLA-WARN SUMMARY] Count=<N> | Details=<danh_sách_hoặc_NONE>`
  - Nếu `Count > 2` trong cùng một lượt chạy `-FastRegression`, in thêm cảnh báo `[DRIFT-ALERT] Multiple SLA warnings detected — inspect before FullRegression`.

## 4. Verification Steps (Nghiệm Thu Gate 13a — `Verify-Task13a.ps1`)
1. **E1 (Build `-warnaserror` & Repository-Wide Zero-BOM Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; toàn bộ file theo dõi bởi Git đạt 0-byte BOM.
2. **E2 (Memory Breakdown & Post-Slimming `Private Working Set <= 52.0 MB`)**:
   - Chạy kịch bản tải (nạp 283 agents + chạy chuỗi `/ask`, `/ground`, `/calc`, `/note`) rồi đo bộ nhớ:
   - Xác nhận `Private Working Set` đạt **`<= 52.0 MB`** (nằm hoàn toàn trong vùng Xanh, thấp hơn ngưỡng cảnh báo vàng `54.0 MB` và thấp hơn mức `57.7 MB` của Gate 14b ít nhất `5 MB`).
   - In bảng giải phẫu `ManagedHeapMb` vs `PrivateWorkingSetMb` vào log.
3. **E3 (HUD Memory Label Accuracy Check)**:
   - Kiểm tra chuỗi hiển thị bộ nhớ trên HUD / lệnh `/status` khớp với chỉ số `Private Working Set` (sai số `< 2.0 MB` so với phép đo của `Verify-Task13a.ps1`, không còn hiển thị con số ~94 MB của `PrivateMemorySize64`).
4. **E4 (`[SLA-WARN SUMMARY]` Tracking & `-FastRegression` Pass)**:
   - Chạy `-FastRegression`: xác nhận hoàn tất trong `< 90 giây`, in đúng dòng `[SLA-WARN SUMMARY] Count=...` và mọi hạng mục đều PASS (không làm chậm P50/P95 của `/ask`, `/ground`, `/calc`).
5. **E5 (CPU Idle `0%`, Git Immutability & GitHub Actions CI Green)**:
   - CPU nền sau nhịp lắng 2 giây `< 0.5%`, chốt chặn Git `HEAD` không đổi, và workflow CI trên GitHub chạy xanh.