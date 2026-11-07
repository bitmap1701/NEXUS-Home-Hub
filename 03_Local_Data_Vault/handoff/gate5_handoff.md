# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 5 (TASK-05) & Nhận TASK-06

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-05-dev-qa-loop` (xếp chồng trên `claude/task-04-cockpit-dispatch`; chưa merge vào `main`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-05, lấy từ máy Nhật Hòa, lưu không BOM)
- **Chi tiết kỹ thuật**: `qa_report.md` §7

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Schema `agent_dispatch_logs` | Không có `retry_count`, `evidence_notes`, `updated_at_utc`; CHECK chặn `ESCALATED` | Đủ 3 cột; CHECK có `ESCALATED`; bảng được dựng lại có sao lưu, giữ nguyên id và dữ liệu cũ |
| 2 | Verdict | Chỉ qua MCP, chỉ đổi `status`, evidence nằm trong audit log | Hàm dùng chung cho Cockpit và MCP: một `UPDATE … RETURNING` nguyên tử, lưu evidence, cầu chì 3 lần → `ESCALATED` + dòng báo cáo trong `qa_report.md` (đúng một lần) |
| 3 | Lệnh Cockpit | `/boost`, `/status`, `/mode` | Thêm `/pass`, `/fail`, `/needswork`, `/verdict` (có `#id`) |
| 4 | Giao diện | Thẻ và feed một màu, không có trạng thái QA | Màu theo trạng thái (xanh dương/xanh lá/vàng/đỏ), nhãn `Strike n/3`, `Retries`, dòng Evidence, cảnh báo đỏ khi escalate |
| 5 | Đồng bộ MCP → Cockpit | Cockpit chỉ thấy dòng mới khi tự làm mới | `FileSystemWatcher` trên `latest_dispatch.json`, debounce 150 ms, không polling; McpTools ghi file khi dispatch và khi verdict |
| 6 | Độ trễ `Ctrl+Shift+Space` | 72 ms (Gate 4), mỗi lần đổi chế độ đều `SWP_FRAMECHANGED` | `SWP_FRAMECHANGED` chỉ khi `WS_EX_TOOLWINDOW` đổi; đọc SQLite chuyển sang `InvokeAsync(Background)` (chưa đo) |
| 7 | Nhánh ACE-Guard | Chưa từng chạy (cần Delta Force) | Kiểm thử tất định: giả lập `Win32Exception(5)` đi qua đúng khối `catch` thật, chạy thử không đổi tiến trình |
| 8 | Kiểm thử | Verify-Task04 | `Verify-Task05.ps1` (E1–E7), 4 selftest mới, pipe `SYNCSTATE` / `COMMANDMODE` / `PASSIVE` |

---

## 2. Trạng thái Gate 5 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (chưa build)

Môi trường của Claude không có .NET SDK và PowerShell: **chưa build, chưa có số liệu đo**. Phần SQL đã chạy thử trên SQLite thật (qa_report §7.1). Cần Nhật Hòa chạy script trên máy.

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
# current_task.md (và có thể GEMINI.md) trên máy đang có bản Gemini ghi nhưng chưa commit; cất tạm để checkout:
git stash push -m "gemini-local-task05" -- GEMINI.md 03_Local_Data_Vault/handoff/current_task.md
git fetch origin
git checkout claude/task-05-dev-qa-loop
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task05.ps1
```
Script tự gửi `SHUTDOWN` cho HostService đang chạy trước khi build, rồi bật lại và để nó chạy. Lần chạy đầu sẽ dựng lại bảng và tạo `structured_db\nexus_memory.pre-task05-<giờ UTC>.db` (bản sao lưu, không vào git).

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + `--selftest schema` | `-warnaserror` 0/0; đủ 3 cột mới; CHECK có `ESCALATED`; mọi dòng của bản sao lưu `pre-task05` còn nguyên trong bảng (khớp id + timestamp + squad + task) | ⏳ |
| E2 | `--selftest devqa-pass` | Dispatch từ Cockpit → `/pass Kiểm thử UDP đạt chuẩn` → SQLite và `latest_dispatch.json` đều `PASSED`, evidence đúng nguyên văn, JSON không BOM và đủ 10 trường | ⏳ |
| E3 | `--selftest devqa-strikes` | `/fail` ×3 → NEEDS_WORK r1, NEEDS_WORK r2, ESCALATED r3; đúng 1 dòng `[3-STRIKE ESCALATION]` trong `qa_report.md` (lần `/fail` thứ 4 không thêm dòng); `/needswork` + `/verdict` cho `PASSED (Retries: 1)`. Script khôi phục `qa_report.md` sau đó | ⏳ |
| E4 | McpTools `update_dispatch_verdict` ×3 + pipe `SYNCSTATE` | `"saved":true`; `latest_dispatch.json` có `source: McpTools`; Cockpit nhận đúng id + `NEEDS_WORK`; độ trễ lớn nhất < 300 ms (gồm debounce 150 ms) | ⏳ |
| E5 | `--selftest boost-acedenied` + pipe `COMMANDMODE` ×6 | `ACE-Guard Protected (Background Yield Active)`, mã 5, không exception, không đụng foreground/Whitelist; handler `Ctrl+Shift+Space` < 35 ms (bỏ lần đầu) | ⏳ |
| E6 | Kiểm tay | Trong game: dispatch → `/fail thiếu log` (thẻ vàng, Strike 1/3) → `/pass đã sửa` (thẻ xanh lá, Retries: 1); focus về game sau mỗi Enter. Tùy chọn: `/boost` khi Delta Force mở | ⏳ |
| E7 | Ngân sách + hồi quy | private WS < 60 MB, CPU nền < 1 %; `Verify-Task04 -SkipBuild` (gồm Verify-Task03) PASS | ⏳ |

Kết quả gốc: `handoff/task05_verification.json` và `handoff/task05_selftest_*.json`.

---

## 3. Điểm mở cần Architect quyết định

1. **Migration khác đặc tả** (§3.A chỉ nói `ALTER ADD COLUMN`): CHECK cũ chặn `ESCALATED`, nên Claude dựng lại bảng trong một transaction (có sao lưu `VACUUM INTO`, giữ id). Xác nhận cách làm này?
2. **Sau ESCALATED**: `/fail` vẫn tăng `retry_count` nhưng không ghi thêm báo cáo; `/pass` vẫn được phép (người ghi đè). Có muốn khóa dispatch đã escalate (chỉ Nhật Hòa mở lại) không?
3. **E3 và `qa_report.md`**: script khôi phục file sau E3 để không thêm dòng kiểm thử vào repo (dòng vẫn có trong `task05_selftest_devqa-strikes.json`). Đồng ý, hay muốn giữ dòng thật trong `qa_report.md` (`-KeepQaReportEntry`)?
4. **Cú pháp id**: `/pass #12 …` (bắt buộc `#`) để `/pass 200Hz ổn` không bị hiểu nhầm là id 200; riêng `/verdict` nhận cả số trần. Ổn không?
5. **Cách đo E5**: đo thời gian handler như con số 72 ms ở Gate 4, nhưng khởi động qua pipe thay vì phím thật. Có cần đo thêm từ lúc nhận `WM_HOTKEY` không?
6. **Còn mở từ Gate 4**: hiển thị RAM (thanh trạng thái vẫn là `WorkingSet64`, ADR-09 dùng private WS); danh sách loại trừ cho Background Yield; công tắc Game Boost một cú click; chữ "Gate: Reality Checker" bị cắt.
7. **Đặc tả vẫn chưa lên GitHub**: `current_task.md` TASK-05 chỉ có trên máy (không nhánh nào có). Lần này không hỏng ký tự nhờ `@' ... '@`, nhưng file có BOM (trái ADR-16). Đề nghị Gemini commit đặc tả, UTF-8 không BOM.
8. **Merge**: bốn nhánh `claude/task-02…05` xếp chồng. Đề xuất một PR `claude/task-05-dev-qa-loop` → `main` sau khi Gate 5 PASS.

---

## 4. Đề xuất TASK-06

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Merge + dọn nợ kỹ thuật** *(khuyến nghị)* | Một PR đưa 4 nhánh vào `main`; thanh trạng thái hiện private WS (ADR-09); sửa dòng "Gate: Reality Checker" bị cắt; bỏ chữ "iGPU UHD 730"/"144 Hz" lỗi thời trong MFD; file cấu hình (`nexus.settings.json`) thay biến môi trường cho game target, màn hình, danh sách loại trừ Background Yield; xác nhận công tắc Game Boost hai bước. | Bốn nhánh xếp chồng càng lâu càng khó merge, và mọi tính năng mới (giọng nói, Gemini API) sẽ cần chỗ lưu cấu hình. Phạm vi nhỏ, rủi ro thấp, sau Gate 5 là thời điểm có đủ bằng chứng nhất. |
| B. Gemini Multimodal Live (PCM 16 kHz) → Dispatch | Mic VAD → Gemini → `SquadDispatcher`, khóa API bằng DPAPI. | Giá trị lớn nhưng cần đo tải CPU khi chơi Delta Force và quản lý khóa; nên làm sau khi có file cấu hình (A). |
| C. Vòng QA tự động | Khi `NEEDS_WORK`, tự gửi lại nhiệm vụ cho Primary Agent kèm evidence; dừng ở strike 3. | Đóng vòng hoàn toàn tự động, nhưng cần quyết định ở §3 điểm 2 trước. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai TASK-05 (Hướng A: đóng vòng Dev-QA từ Cockpit, cầu chì 3 lần thử, đồng bộ MCP hai chiều) trên nhánh `claude/task-05-dev-qa-loop` của https://github.com/bitmap1701/NEXUS-Home-Hub. Hãy đọc `03_Local_Data_Vault/handoff/gate5_handoff.md` và `qa_report.md` §7. Trạng thái: **chưa build** (môi trường Claude không có .NET), Nhật Hòa sẽ chạy `Verify-Task05.ps1` (E1–E7) để lấy bằng chứng. Phát hiện quan trọng: đặc tả §3.A chỉ dùng `ALTER TABLE ADD COLUMN`, nhưng bảng TASK-03 có `CHECK (status IN ('DISPATCHED','NEEDS_WORK','PASSED'))` nên SQLite sẽ **từ chối lưu `ESCALATED`**; Claude dựng lại bảng trong một transaction (sao lưu `VACUUM INTO` trước, giữ nguyên id) và đã chạy thử trên SQLite thật (171 dòng giữ nguyên). Đã làm: verdict dùng chung cho Cockpit và MCP bằng một `UPDATE … RETURNING` nguyên tử, dòng `[3-STRIKE ESCALATION]` ghi đúng một lần; `/pass`, `/fail`, `/needswork`, `/verdict` (có `#id`); thẻ và feed tô màu theo trạng thái; `FileSystemWatcher` + debounce 150 ms; nhánh ACE-Guard được kiểm thử tất định bằng `Win32Exception(5)` giả lập; và chỉ dùng `SWP_FRAMECHANGED` khi `WS_EX_TOOLWINDOW` đổi để kéo độ trễ `Ctrl+Shift+Space` (72 ms) xuống dưới 35 ms (chưa đo). Lưu ý: `current_task.md` TASK-05 vẫn chỉ nằm trên máy (không có trên GitHub) và có BOM; lần sau hãy commit và lưu UTF-8 không BOM. Với vai Reality Checker: nghiệm thu Gate 5 khi có `task05_verification.json`. Với vai Architect: quyết định 8 điểm ở §3 (quan trọng nhất: xác nhận cách migration, và hành vi sau ESCALATED), rồi chốt TASK-06 (Claude khuyến nghị Hướng A: merge 4 nhánh + dọn nợ kỹ thuật + file cấu hình).
