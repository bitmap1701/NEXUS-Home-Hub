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

## 2. Trạng thái Gate 5 — ✅ E1–E7 PASS (lần chạy 2) + E6 kiểm tay PASS

- **Lần chạy 1** (13:51, commit `59f266d`, Delta Force + SGuard đang chạy): E1–E5 PASS, E7 FAIL (Task04 E2 P95 13,7 ms; CPU nền 2,94 % đo quá sớm).
- **Lần chạy 2** (13:57, commit `9ac0282`, đã tắt game): **ALL AUTOMATED CHECKS PASS (E1–E5, E7)**; P95 dispatch 2,3 ms; CPU nền 0,57 %. **E6** Nhật Hòa kiểm tay PASS. Chi tiết: qa_report §7.5–7.6.

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
| E1 | Build + `--selftest schema` | `-warnaserror` 0/0; đủ 3 cột mới; CHECK có `ESCALATED`; mọi dòng của bản sao lưu `pre-task05` còn nguyên trong bảng (khớp id + timestamp + squad + task) | ✅ 0/0; dựng lại bảng 173 → 173 dòng (lần 1), 0 dòng thiếu |
| E2 | `--selftest devqa-pass` | Dispatch từ Cockpit → `/pass Kiểm thử UDP đạt chuẩn` → SQLite và `latest_dispatch.json` đều `PASSED`, evidence đúng nguyên văn, JSON không BOM và đủ 10 trường | ✅ #174 PASSED, evidence đúng |
| E3 | `--selftest devqa-strikes` | `/fail` ×3 → NEEDS_WORK r1, NEEDS_WORK r2, ESCALATED r3; đúng 1 dòng `[3-STRIKE ESCALATION]` trong `qa_report.md` (lần `/fail` thứ 4 không thêm dòng); `/needswork` + `/verdict` cho `PASSED (Retries: 1)`. Script khôi phục `qa_report.md` sau đó | ✅ #175 r1, r2, ESCALATED r3; 1 dòng; `PASSED (Retries: 1)` |
| E4 | McpTools `update_dispatch_verdict` ×3 + pipe `SYNCSTATE` | `"saved":true`; `latest_dispatch.json` có `source: McpTools`; Cockpit nhận đúng id + `NEEDS_WORK`; độ trễ lớn nhất < 300 ms (gồm debounce 150 ms) | ✅ lần 1: 209 / 206 / 223 ms; lần 2: 188 / 178 / 172 ms |
| E5 | `--selftest boost-acedenied` + pipe `COMMANDMODE` ×6 | `ACE-Guard Protected (Background Yield Active)`, mã 5, không exception, không đụng foreground/Whitelist; handler `Ctrl+Shift+Space` < 35 ms (bỏ lần đầu) | ✅ ACE-Guard, mã 5; max 10,5 ms (lần 1), **2,0 ms** (lần 2); Gate 4: 72 ms |
| E6 | Kiểm tay | Trong game: dispatch → `/fail thiếu log` (thẻ vàng, Strike 1/3) → `/pass đã sửa` (thẻ xanh lá, Retries: 1); focus về game sau mỗi Enter. Tùy chọn: `/boost` khi Delta Force mở | ✅ Nhật Hòa xác nhận: xanh dương → vàng Strike 1/3 → xanh lá Retries: 1, focus về game |
| E7 | Ngân sách + hồi quy | private WS < 60 MB, CPU nền < 1 %; `Verify-Task04 -SkipBuild` (gồm Verify-Task03) PASS | Lần 1 ❌ (P95 13,7 ms khi đang chơi; CPU 2,94 % đo sớm). **Lần 2 ✅** private WS 49,4 MB; CPU 0,57 %; Task04 PASS (P95 2,3 ms), Task03 PASS |

Kết quả gốc: `handoff/task05_verification.json` và `handoff/task05_selftest_*.json`.

---

## 3. Điểm mở cần Architect quyết định

0. **Ngân sách P95 10 ms khi đang chơi**: khi máy rảnh P95 là 2,3 ms, nhưng khi Delta Force + SGuard chạy thì 13,7 ms. Ngân sách 10 ms áp dụng khi máy rảnh hay cả khi đang chơi? Nếu cả khi đang chơi, TASK sau cần đo từng bước lúc có game (cờ `-WithGame`), có thể chuyển ghi SQLite/file khỏi đường găng.
9. **CPU nền có đỉnh định kỳ ~6 s** (3,2–3,7 %, nền 0,1–0,4 %): chưa rõ nguồn, đề xuất kiểm tra timer làm mới MFD.
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

> Chào Gemini, Gate 5 (TASK-05) đã đủ bằng chứng. Nhánh `claude/task-05-dev-qa-loop` của https://github.com/bitmap1701/NEXUS-Home-Hub; đọc `03_Local_Data_Vault/handoff/gate5_handoff.md` và `qa_report.md` §7. Trên máy Nhật Hòa, lần chạy 2 (đã tắt game) **Verify-Task05 ALL AUTOMATED CHECKS PASS (E1–E5, E7)** và **E6 kiểm tay PASS**: migration dựng lại bảng giữ nguyên 173/173 dòng (CHECK cũ của TASK-03 chặn `ESCALATED`, nên `ALTER ADD COLUMN` như đặc tả là không đủ — cần bạn xác nhận cách làm); `/pass` lưu evidence; `/fail` ×3 → ESCALATED kèm đúng 1 dòng `[3-STRIKE ESCALATION]`; MCP `update_dispatch_verdict` → Cockpit 172–188 ms (ngân sách 300); chế độ lệnh `Ctrl+Shift+Space` từ 72 ms xuống **2,0 ms** nhờ chỉ dùng `SWP_FRAMECHANGED` khi `WS_EX_TOOLWINDOW` đổi; nhánh ACE-Guard (Access Denied giả lập) đúng trạng thái; private WS 49,4 MB, CPU nền 0,57 %; hồi quy Task04 (P95 dispatch 2,3 ms) và Task03 PASS. Lưu ý minh bạch: lần chạy 1 khi Delta Force + SGuard đang chạy, P95 dispatch là 13,7 ms (> 10). Với vai Reality Checker: nghiệm thu Gate 5. Với vai Architect: quyết định các điểm ở §3 (đặc biệt điểm 0: ngân sách 10 ms khi đang chơi; điểm 1: migration; điểm 2: hành vi sau ESCALATED), rồi chốt TASK-06 (Claude khuyến nghị Hướng A: merge 4 nhánh + dọn nợ kỹ thuật + file cấu hình). Nhớ commit `current_task.md` lên GitHub và lưu UTF-8 không BOM.
