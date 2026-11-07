# TASK-06: Hợp Nhất 4 Nhánh Git Về `main`, Tách Cấu Hình `nexus_config.json` & Dọn Nợ Kỹ Thuật (Hướng A)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 5)
- **Nghiệm thu Gate 5**: `PASSED` 100% (Build `-warnaserror` sạch; migration dựng lại bảng bảo toàn 173/173 dòng; `/pass` và `/fail` x3 hoạt động chuẩn xác; đồng bộ MCP sang Cockpit đạt 172–188 ms; thời gian mở chế độ lệnh `Ctrl+Shift+Space` giảm từ 72 ms xuống 2.0 ms; Private Working Set 49.4 MB; CPU nền 0.57%).
- **6 Quyết định Kiến trúc Đã Chốt (ADR-17 .. ADR-22)**:
  1. **ADR-17 (Dual-Profile P95 Dispatch SLA — Điểm mở 0)**:
     - Chế độ Desktop / IDE (không chạy game): ngưỡng P95 dispatch `< 10 ms`.
     - Chế độ Live Combat (khi phát hiện tiến trình Game/Anti-Cheat như `DeltaForce*`, `TslGame*`, `SGuard*` đang chạy): ngưỡng P95 dispatch `< 25 ms` (do minifilter của ACE-Guard quét I/O khi SQLite WAL commit).
  2. **ADR-18 (Transactional Table-Rebuild Migration — Điểm mở 1)**: Phê chuẩn chính thức cách làm của Claude: dựng lại bảng trong giao dịch nguyên tử (`BEGIN IMMEDIATE` -> tạo bảng mới -> chép toàn bộ dữ liệu -> xóa bảng cũ -> đổi tên -> `COMMIT`) làm chuẩn nâng cấp SQLite.
  3. **ADR-19 (Post-ESCALATED & Post-PASSED State Machine — Điểm mở 2)**:
     - Gọi `/fail` trên bản ghi đã `ESCALATED`: vẫn tăng `retry_count` và cập nhật `evidence_notes`, giữ `status = 'ESCALATED'`, nhưng **không ghi thêm dòng `[3-STRIKE ESCALATION]` trùng lặp** vào `qa_report.md` (chỉ ghi đúng 1 lần tại thời điểm chạm mốc chuyển sang `ESCALATED`).
     - Gọi `/pass [notes]` trên bản ghi đang `ESCALATED`: cho phép chuyển `status` sang `'PASSED'` (ghi nhận đã tháo gỡ xong điểm nghẽn), giữ nguyên `retry_count`.
     - Gọi `/fail [notes]` trên bản ghi đang `PASSED`: cho phép mở lại về `'NEEDS_WORK'` (hoặc `'ESCALATED'` nếu `retry_count >= 3`).
  4. **ADR-20 (Externalized Configuration `nexus_config.json`)**: Tách các tham số cấu hình ra file `03_Local_Data_Vault/config/nexus_config.json` (UTF-8 no BOM) và bổ sung lệnh `/reload` (bí danh `/config`) trên CockpitUI.
  5. **ADR-21 (Tech-Debt Cleanup & Shared Logic)**: Gom phần logic dùng chung giữa `HostService` và `McpTools` để loại bỏ trùng lặp mã nguồn.
  6. **ADR-22 (Branch Consolidation)**: Hợp nhất toàn bộ chuỗi nhánh `claude/task-02-monitor-routing`, `claude/task-03-agent-squad-dispatch`, `claude/task-04-cockpit-dispatch`, `claude/task-05-dev-qa-loop` và `TASK-06` về nhánh `main`.

## 2. Mục Tiêu TASK-06 (Hướng A — Merge 4 Branches + Tech-Debt Cleanup + External Config)
Hoàn thiện và đóng gói nền móng `NEXUS Home Hub` (Phase 1 -> Phase 6) trên nhánh `main`:
1. Hợp nhất sạch sẽ 4 nhánh tính năng trước đó cùng các thay đổi của TASK-06 vào nhánh `main`.
2. Đưa toàn bộ tham số vận hành ra file cấu hình động `03_Local_Data_Vault/config/nexus_config.json` kèm lệnh `/reload` trực tiếp từ CockpitUI.
3. Dọn sạch nợ kỹ thuật: đồng nhất code dùng chung giữa `HostService` và `McpTools`, khóa máy trạng thái ADR-19 và cập nhật bộ script kiểm thử tự động nhận diện SLA theo ADR-17.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Hợp Nhất Nhánh Git Về `main` (Branch Consolidation — ADR-22)
- Kiểm tra lịch sử commit của chuỗi nhánh `claude/task-02-monitor-routing` -> `claude/task-03-agent-squad-dispatch` -> `claude/task-04-cockpit-dispatch` -> `claude/task-05-dev-qa-loop`.
- Thực hiện gộp (merge) lên nhánh `main` (hoặc thực hiện TASK-06 rồi fast-forward/merge sạch vào `main`) sao cho nhánh `main` chứa đầy đủ 100% code, script `Verify-Task03..06.ps1`, `GEMINI.md` và `qa_report.md`.
- Xác nhận `git status` sạch và đẩy nhánh `main` lên GitHub remote.

### 3.2. Tách File Cấu Hình Động `nexus_config.json` & Lệnh `/reload` (ADR-20)
- **Vị trí file**: `03_Local_Data_Vault/config/nexus_config.json` (mã hóa UTF-8 không BOM).
- **Khởi tạo tự động & Chống lỗi (Fallback Resilience)**:
  - Khi `HostService` hoặc `McpTools` khởi động, nếu thư mục `config` hoặc file `nexus_config.json` chưa tồn tại, tự động tạo mới với bộ giá trị mặc định chuẩn.
  - Nếu người dùng sửa tay làm hỏng cú pháp JSON hoặc bỏ trống trường, hệ thống bắt ngoại lệ êm, giữ nguyên cấu hình hợp lệ gần nhất (hoặc mặc định) mà không làm crash tiến trình.
- **Các trường cấu hình bắt buộc trong `nexus_config.json`**:
    {
      "defaultNexusMode": "Micro",
      "preferredMonitorKeywords": ["MSI", "G272"],
      "passiveHudHotkey": "Alt+Space",
      "commandModeHotkey": "Ctrl+Shift+Space",
      "memoryTrimFloorMb": 50,
      "watcherDebounceMs": 150,
      "dispatchSlaDesktopMs": 10,
      "dispatchSlaCombatMs": 25,
      "protectedProcessPrefixes": [
        "DeltaForce", "df_launcher", "TslGame", "ExecPubg", "fczf", "fifazf", "Roblox",
        "SGuard", "ACE-", "BEService", "BEClient", "zksvc",
        "audiodg", "dwm", "csrss", "explorer", "nvcontainer", "NVDisplay", "NEXUSHome"
      ]
    }
- **Lệnh `/reload` (và `/config`) trên ô Command Input của `CockpitUI`**:
  - Gõ `/reload`: nạp lại `nexus_config.json` từ ổ đĩa ngay lập tức (cập nhật `defaultNexusMode`, `memoryTrimFloorMb`, `protectedProcessPrefixes`, `preferredMonitorKeywords`, `watcherDebounceMs`) và hiển thị thông báo xác nhận trên thẻ trạng thái của CockpitUI.
  - Gõ `/config`: hiển thị nhanh các thông số cấu hình đang áp dụng hiện tại.

### 3.3. Dọn Nợ Kỹ Thuật & Củng Cố Máy Trạng Thái (ADR-17, ADR-19, ADR-21)
1. **Loại bỏ trùng lặp mã nguồn giữa `HostService` và `McpTools` (ADR-21)**:
   - Gom các thành phần dùng chung (`NexusPathResolver`, `NexusConfig`, quản lý kết nối & migration SQLite WAL, `AgentRegistry`, `SquadDispatcher`, `GameBoost`) về một nguồn duy nhất (dùng Shared Source `<Compile Include="..." Link="..." />` hoặc project chung) để mọi bản vá luôn đồng bộ ở cả 2 tiến trình.
2. **Hoàn thiện Máy trạng thái `UpdateVerdict` sau `ESCALATED` (ADR-19)**:
   - Nếu bản ghi đang có `status == 'ESCALATED'` và nhận tiếp `/fail` (strike 4, 5...): tăng `retry_count`, cập nhật `evidence_notes` và `updated_at_utc`, giữ nguyên `status = 'ESCALATED'`, và **tuyệt đối không ghi thêm dòng `[3-STRIKE ESCALATION]` thứ hai** cho cùng `dispatchId` đó vào `qa_report.md` (chỉ ghi khi chuyển trạng thái từ khác `ESCALATED` sang `ESCALATED`).
   - Nếu bản ghi đang có `status == 'ESCALATED'` và nhận `/pass [notes]`: chuyển `status` sang `'PASSED'`, giữ nguyên `retry_count` và lưu `evidence_notes`.
3. **Tự động chọn ngưỡng SLA P95 trong Script Kiểm thử (ADR-17)**:
   - Cập nhật logic kiểm tra độ trễ P95 trong `Verify-Task04.ps1`, `Verify-Task05.ps1` và `Verify-Task06.ps1`: tự động kiểm tra nếu có tiến trình trong nhóm Game/Anti-Cheat (`DeltaForce*`, `TslGame*`, `SGuard*`) đang chạy thì áp ngưỡng `< 25 ms` (Combat SLA), ngược lại áp ngưỡng `< 10 ms` (Desktop SLA).

## 4. Verification Steps (Nghiệm Thu Gate 6 — `Verify-Task06.ps1`)
1. **E1 (Build & Zero-BOM Check)**: `dotnet build -warnaserror` toàn Solution đạt 0 lỗi, 0 cảnh báo; kiểm tra `GEMINI.md`, `current_task.md`, `nexus_config.json`, `latest_dispatch.json` đều là UTF-8 không BOM (0-byte BOM).
2. **E2 (External Config & Live `/reload` Resilience)**:
   - Xác nhận `03_Local_Data_Vault/config/nexus_config.json` tự động được tạo nếu chưa có.
   - Đổi thử `memoryTrimFloorMb` (ví dụ từ `50` sang `64`) hoặc `defaultNexusMode` trong `nexus_config.json`, gọi `/reload` và xác nhận hệ thống áp dụng ngay giá trị mới.
   - Ghi thử chuỗi JSON hỏng vào `nexus_config.json`, gọi `/reload` -> xác nhận hệ thống không crash, giữ cấu hình an toàn và khôi phục lại file chuẩn.
3. **E3 (ADR-19 Post-ESCALATED State Machine Check)**:
   - Tạo 1 dispatch mới -> gọi `/fail` 3 lần liên tiếp -> xác nhận `status = 'ESCALATED'`, `retry_count = 3` và có đúng 1 dòng `[3-STRIKE ESCALATION]` cho `dispatchId` này trong `qa_report.md`.
   - Gọi tiếp `/fail` lần thứ 4 trên cùng `dispatchId` -> xác nhận `retry_count = 4`, `status = 'ESCALATED'` và **vẫn chỉ có 1 dòng** `[3-STRIKE ESCALATION]` cho `dispatchId` đó trong `qa_report.md`.
   - Gọi tiếp `/pass Đã khắc phục xong` trên cùng `dispatchId` -> xác nhận `status` chuyển sang `'PASSED'` và `retry_count = 4`.
4. **E4 (ADR-17 Dual-Profile SLA & Shared Code Integrity)**:
   - Xác nhận `HostService` và `McpTools` dùng chung logic lõi và script kiểm thử nhận diện đúng ngưỡng SLA (`10 ms` vs `25 ms`).
5. **E5 (Git `main` Consolidation & Full Regression Suite)**:
   - Xác nhận nhánh `main` đã tích hợp đầy đủ các nhánh trước đó; chạy `Verify-Task03.ps1`, `Verify-Task04.ps1`, `Verify-Task05.ps1` và `Verify-Task06.ps1` đều PASS 100%; `Private Working Set` dưới 60 MB, CPU nền dưới 1%.