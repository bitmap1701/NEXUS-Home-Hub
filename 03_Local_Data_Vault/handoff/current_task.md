# TASK-17b: Chẩn Đoán & Thu Gọn Bộ Nhớ Nền HostService Đầy Đủ (`ADR-70` -> `v0.17.2`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-12 08:41. Ghi chú của Claude: lúc nhận đặc tả `v0.17.1` chưa chốt (`main` = `v0.17.0` @`aaa5ec7`), nên nhánh tách từ đầu nhánh TASK-17a (`c97cd1f`) thay vì `main`; khi `v0.17.1` chốt, nhánh này chỉ cần gộp `main` (fast-forward).

- **ADR & Phiên bản đích**: `ADR-70` -> `v0.17.2`
- **Nhánh**: `claude/task-17b-hostservice-footprint` (tách từ `main` sau khi chốt `v0.17.1`, trạng thái repo hiện tại: chưa kiểm)
- **Phạm vi**: Thêm chế độ đo `--selftest host-footprint` trên `HostService` đầy đủ (tách bạch chi phí từng dịch vụ nền: SQLite connection pool, NamedPipe, MCP, Audio/MMDevice, Network, ThreadPool/JIT); thu gọn phần chiếm dụng `7–12 MB` ngoài cửa sổ Cockpit mà không làm tăng WAKE.
- **Cổng nghiệm thu (`Verify-Task17b.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0` lỗi / `0` cảnh báo; BOM = `0` byte; đăng ký `ADR-70` (và chuyển `ADR-69` sang `Chốt v0.17.1`).
  - **E2**: `--selftest host-footprint` xuất bảng phân rã bộ nhớ từng mô-đun của `HostService` (`GC Heap`, `Private WS`, `Paged/NonPaged`) trong `<= 5000 ms`.
  - **E3**: Chạy kịch bản tải giống `Task16b` trên `HostService` đầy đủ + kích hoạt `[MEM]`; xác nhận `GC Heap <= 8,0 MB` và phần chênh ngoài cửa sổ Cockpit giảm tối thiểu `>= 3,0 MB` so với mốc `49,7 MB`.
  - **E4**: Độ trễ `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
  - **E5**: `Private WS` rảnh trên `Task17b` và `Task16b` đạt `<= 45,0 MB` (dải `[SLA-WARN] <= 50,0 MB` nếu `GC Heap <= 8,0 MB`), `CPU rảnh = 0%`, chuỗi hồi quy `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
