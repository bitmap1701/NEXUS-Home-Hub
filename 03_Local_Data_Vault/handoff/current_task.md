# TASK-05: Đóng Vòng Lặp Dev-QA Trực Tiếp Từ CockpitUI, Cầu Chì 3 Lần Thử & Đồng Bộ MCP Hai Chiều (Hướng A)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 4)
- **Nghiệm thu Gate 4**: `PASSED` (Build `-warnaserror` sạch, P95 in-process dispatch 3 ms, đa tiến trình SQLite WAL 0 lỗi, 5 lượt điều phối chuẩn 5 Squad, Private Working Set 51.9 MB, CPU 0%, Focus trả về game tức thì sau Enter/Esc).
- **8 Quyết định Kiến trúc Đã Chốt (ADR-09 .. ADR-16)**:
  1. **ADR-09 (Memory Metric)**: Chốt `Private Working Set` (< 60 MB) làm chỉ số bộ nhớ chính thức.
  2. **ADR-10 (Command Mode Latency < 35 ms)**: Khi nhấn `Ctrl+Shift+Space`, chỉ đổi cờ cửa sổ và đặt `Keyboard.Focus` ngay trên luồng chính; đẩy việc đọc SQLite `Recent Dispatches Feed` sang `DispatcherPriority.Background`.
  3. **ADR-11 (Deterministic ACE-Guard Test)**: Hỗ trợ kiểm thử tự động nhánh `Access Denied` (Win32 Error Code 5) trong `/boost` để xác nhận chế độ `Background Yield` hoạt động 100% cả khi không bật game.
  4. **ADR-12 (Cockpit Dev-QA Slash Commands)**: Bổ sung lệnh `/pass [evidence]`, `/fail [reason]` (bí danh `/needswork`), và `/verdict <id> <PASSED|NEEDS_WORK> [notes]` ngay trên ô nhập lệnh CockpitUI.
  5. **ADR-13 (3-Strike Circuit Breaker)**: Mở rộng bảng `agent_dispatch_logs` thêm cột `retry_count` (INTEGER DEFAULT 0) và `evidence_notes` (TEXT DEFAULT ''). Khi một `dispatchId` bị đánh dấu `NEEDS_WORK` đủ 3 lần (`retry_count >= 3`), tự động chuyển `status` thành `ESCALATED`, hiển thị cảnh báo Đỏ trên CockpitUI và ghi biên bản vào `03_Local_Data_Vault/handoff/qa_report.md`.
  6. **ADR-14 (Two-Way Live Sync `McpTools` <-> `CockpitUI`)**: Khi `McpTools` thực thi `dispatch_agent_squad` hoặc `update_dispatch_verdict`, đồng thời cập nhật `03_Local_Data_Vault/handoff/latest_dispatch.json`. `CockpitUI` dùng `FileSystemWatcher` (debounce 150 ms, `FileShare.ReadWrite`) để tự động làm mới Active Card và Recent Feed trên màn hình MSI G272.
  7. **ADR-15 (`current_task.md` vs `latest_dispatch.json`)**: Giữ `current_task.md` cho đặc tả cấp Task/Sprint; dùng `latest_dispatch.json` làm trạng thái điều phối & nghiệm thu thời gian thực.
  8. **ADR-16 (Git & Encoding Discipline)**: Giữ nguyên bản `GEMINI.md` hợp nhất (gồm Chốt chặn 3 cấp, Cầu chì 3 lần thử, DPAPI, Phân vai Liên minh + ADR-05..16) và dùng UTF-8 không BOM cho mọi file.

## 2. Mục Tiêu TASK-05 (Hướng A — Close the Dev-QA Loop from CockpitUI)
Hoàn thiện chu trình khép kín **Dispatch -> Execute -> QA Verdict (`PASSED` / `NEEDS_WORK` / `ESCALATED`)** ngay trên màn hình phụ **MSI Optix G272** và đồng bộ hai chiều với **Claude / Antigravity** qua `NEXUSHome.McpTools`:
1. Người dùng (hoặc AI qua MCP) gọi điều phối nhiệm vụ -> tạo bản ghi `DISPATCHED` (`retry_count = 0`).
2. Sau khi kiểm tra kết quả, người dùng bấm `Ctrl+Shift+Space` gõ `/pass Đã chạy mượt 200Hz` hoặc `/fail Lỗi tràn bộ đệm UDP`:
   - Cập nhật trực tiếp vào SQLite WAL (`agent_dispatch_logs`).
   - Nếu `/fail` lần thứ 3 trên cùng `dispatchId`, kích hoạt **Cầu chì 3 Lần Thử (`ESCALATED`)** và tự động ghi log cảnh báo vào `qa_report.md`.
   - Cập nhật ngay `latest_dispatch.json` và đổi màu/trạng thái thẻ **Active Squad Card** trên `CockpitUI`.
3. Ngược lại, khi Claude hoặc Antigravity gọi tool `update_dispatch_verdict` hoặc `dispatch_agent_squad` từ `McpTools`, giao diện `CockpitUI` trên màn MSI G272 tự động cập nhật theo thời gian thực mà không cần khởi động lại.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### A. Migration Bảng `agent_dispatch_logs` (SQLite WAL — Backward Compatible)
- Khi khởi tạo DB, kiểm tra cấu trúc bảng `agent_dispatch_logs` qua `PRAGMA table_info(agent_dispatch_logs);`:
  - Nếu chưa có cột `retry_count`: chạy `ALTER TABLE agent_dispatch_logs ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0;`
  - Nếu chưa có cột `evidence_notes`: chạy `ALTER TABLE agent_dispatch_logs ADD COLUMN evidence_notes TEXT NOT NULL DEFAULT '';`
  - Nếu chưa có cột `updated_at_utc`: chạy `ALTER TABLE agent_dispatch_logs ADD COLUMN updated_at_utc TEXT NOT NULL DEFAULT '';`
- Đảm bảo toàn bộ dữ liệu cũ từ TASK-03 và TASK-04 được giữ nguyên 100%.

### B. Logic Nghiệm Thu & Cầu Chì 3 Lần Thử (`UpdateVerdict` Dùng Chung Cho Cockpit & MCP)
- Xây dựng hàm lõi dùng chung (trong `SquadDispatcher` hoặc lớp quản lý DB):
  - Đầu vào: `int? dispatchId` (nếu `null` hoặc `<= 0` thì tự động lấy `id` của bản ghi mới nhất trong `agent_dispatch_logs`), `string verdict` (`PASSED` | `NEEDS_WORK`), `string evidenceNotes`.
  - Nếu `verdict == "NEEDS_WORK"`:
    - Tăng `retry_count = retry_count + 1`.
    - Nếu `retry_count >= 3`: đổi `status` lưu xuống DB thành `"ESCALATED"`, đồng thời ghi nối tiếp (append, UTF-8 no BOM) một mục báo cáo ngắn gọn vào `03_Local_Data_Vault/handoff/qa_report.md` theo mẫu:
      `### [3-STRIKE ESCALATION] Dispatch #<id> | <timestamp_utc> | Squad: <squad_id> | Task: <task_summary> | Evidence: <evidenceNotes>`
    - Nếu `retry_count < 3`: lưu `status = "NEEDS_WORK"`.
  - Nếu `verdict == "PASSED"`:
    - Lưu `status = "PASSED"` (giữ nguyên `retry_count` hiện tại để thống kê số lần thử trước khi pass) và lưu `evidence_notes`.
  - Sau khi cập nhật SQLite WAL thành công, ghi đè file `03_Local_Data_Vault/handoff/latest_dispatch.json` với đầy đủ các trường mới: `dispatchId`, `status`, `retryCount`, `evidenceNotes`, `updatedAtUtc`, `squadId`, `primaryAgent`, `qaAgent`, `taskSummary`, `nexusMode`.

### C. Mở Rộng Slash Commands & Hiển Thị Trạng Thái Trên `CockpitUI`
1. **Slash Commands Mới**:
   - `/pass [ghi chú]` hoặc `/pass #<id> [ghi chú]`: Gọi `UpdateVerdict` với `PASSED`.
   - `/fail [lý do]` (hoặc `/needswork [lý do]`, `/fail #<id> [lý do]`): Gọi `UpdateVerdict` với `NEEDS_WORK`.
   - `/verdict <id> <PASSED|NEEDS_WORK> [ghi chú]`: Cú pháp đầy đủ.
2. **Hiển Thị Trực Quan Trên `CockpitUI` (`RenderMode.SoftwareOnly`)**:
   - Trên **Active Squad Card** và **Recent Dispatches Feed**, hiển thị rõ:
     - Trạng thái kèm số lần thử: ví dụ `[DISPATCHED]`, `[PASSED (Retries: 1)]`, `[NEEDS_WORK (Strike 2/3)]`, hoặc `[ESCALATED - 3 STRIKES!]`.
     - Dòng `Evidence / Verdict Notes` (nếu có).
   - Mã màu viền/chữ nhẹ (SolidColorBrush đơn giản, không dùng hiệu ứng đổ bóng nặng):
     - `DISPATCHED`: Xanh dương nhạt (`#38BDF8`)
     - `PASSED`: Xanh lá (`#4ADE80`)
     - `NEEDS_WORK`: Vàng cam (`#FBBF24`)
     - `ESCALATED`: Đỏ tươi (`#F87171`)
3. **Đồng Bộ Tự Động Qua `FileSystemWatcher`**:
   - Theo dõi thay đổi file `latest_dispatch.json` trong `03_Local_Data_Vault/handoff/`.
   - Áp dụng debounce 150 ms và mở đọc với `FileShare.ReadWrite` để khi `NEXUSHome.McpTools` cập nhật từ bên ngoài, giao diện `CockpitUI` tự động nạp lại Active Card và 5 dòng lịch sử mới nhất.

### D. Tối Ưu Độ Trễ `Ctrl+Shift+Space` (< 35 ms) & Kiểm Thử Nhánh ACE-Guard
1. Rút gọn đường găng trong hàm xử lý `Ctrl+Shift+Space`: chỉ đổi cờ cửa sổ Win32 và `Focus()` vào ô nhập lệnh trước; việc đọc lại SQLite `Recent Dispatches Feed` được gọi bất đồng bộ qua `Dispatcher.InvokeAsync(..., DispatcherPriority.Background)`.
2. Trong `GameBoost`, bổ sung khả năng kiểm thử nhánh `Access Denied` (ví dụ: qua tham số/phương thức kiểm thử hoặc gọi thử lên tiến trình bảo vệ) để chứng minh tự động trong `Verify-Task05.ps1` rằng khi gặp `Win32Exception (Access Denied)`, hệ thống bắt ngoại lệ êm và kích hoạt chế độ `Background Yield` đúng chuẩn ADR-06.

## 4. Verification Steps (Nghiệm Thu Gate 5 — `Verify-Task05.ps1`)
1. **E1 (Build & Schema Migration Check)**: `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; bảng `agent_dispatch_logs` tự động có đủ cột `retry_count`, `evidence_notes`, `updated_at_utc` mà không mất dữ liệu cũ.
2. **E2 (In-Process Dev-QA Loop `/pass`)**: Tạo 1 lệnh điều phối từ Cockpit -> gọi `/pass Kiểm thử UDP đạt chuẩn` -> xác nhận bản ghi trong SQLite WAL và `latest_dispatch.json` chuyển sang `status = "PASSED"` kèm đúng `evidenceNotes`.
3. **E3 (3-Strike Circuit Breaker `/fail` x3)**: Tạo 1 lệnh điều phối mới -> gọi `/fail` 3 lần liên tiếp -> xác nhận lần 1 và 2 là `NEEDS_WORK` (`retryCount` = 1, 2), lần thứ 3 tự động chuyển sang `status = "ESCALATED"` (`retryCount` = 3) và xuất hiện dòng `[3-STRIKE ESCALATION]` trong `03_Local_Data_Vault/handoff/qa_report.md`.
4. **E4 (Cross-Process MCP-to-Cockpit Live Sync)**: Gọi tool `update_dispatch_verdict` qua `NEXUSHome.McpTools` (JSON-RPC stdio) -> xác nhận `latest_dispatch.json` được cập nhật và bộ đồng bộ của `CockpitUI` nhận diện trạng thái mới trong dưới 300 ms.
5. **E5 (ACE-Guard Branch & Command Mode Latency < 35 ms)**:
   - Kiểm chứng tự động nhánh `Access Denied` của `GameBoost` trả về trạng thái `ACE-Guard Protected (Background Yield Active)` không ném lỗi.
   - Đo thời gian thực thi chuyển chế độ `Ctrl+Shift+Space` đạt dưới 35 ms.
6. **E6 (Kiểm Tay Thực Chiến Trên Máy Nhật Hòa)**:
   - Bấm `Ctrl+Shift+Space` gõ lệnh điều phối -> bấm lại `Ctrl+Shift+Space` gõ `/pass` hoặc `/fail` -> quan sát thẻ trên màn **MSI G272** đổi màu/trạng thái tức thì và trả Focus về game/cửa sổ trước.
   - (Tùy chọn khi mở *Delta Force*): Gõ `/boost` để xác nhận trực tiếp với ACE-Guard.
7. **E7 (Resource Budget & Regression)**: `Private Working Set` dưới 60 MB, CPU nền 0%, chạy lại `Verify-Task03` và `Verify-Task04` đều PASS.
