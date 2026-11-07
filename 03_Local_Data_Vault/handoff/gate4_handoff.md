# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 4 (TASK-04) & Nhận TASK-05

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-04-cockpit-dispatch` (xếp chồng trên `claude/task-03-*` và `claude/task-02-*`; chưa merge vào `main`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md`, bản đã sửa ký tự hỏng (xem `qa_report.md` §6.1)
- **Chi tiết kỹ thuật**: `qa_report.md` §6

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Ô lệnh Cockpit | Chỉ lưu chữ vào SQLite | Gọi `SquadDispatcher` cùng tiến trình; `/boost`, `/status`, `/mode`; ghi `latest_dispatch.json` |
| 2 | Giao diện | Nhật ký chữ | Thẻ Active Squad, Recent Dispatches (đồng bộ với McpTools qua SQLite), thanh trạng thái Mode/HUD/RAM, chữ gợi ý |
| 3 | Focus | `Ctrl+Shift+Space` giữ focus cho tới khi bấm `Alt+Space` | Enter/Esc → về Passive HUD và trả focus cho game/IDE |
| 4 | Game Boost | Tiền tố cố định; thử `PriorityClass=High` lên game có anti-cheat (bị từ chối) | ADR-06: shield foreground + Whitelist, không mở quyền ghi vào game có anti-cheat, Background Yield + `/boost off` khôi phục, báo MB RAM thu hồi |
| 5 | SQLite | `busy_timeout` chỉ ở kết nối khởi tạo; `Cache=Shared` | ADR-08: mọi kết nối có `busy_timeout=5000` + WAL; bỏ shared-cache |
| 6 | Đường dẫn | Còn nhắc `D:\` trong chú thích | Không còn `D:\NEXUS_Home_Hub` trong `.cs`; nhận `NEXUS_ROOT` |
| 7 | Kiểm thử | — | `--selftest` (dispatch, boost, boost-live, wal-stress), pipe `COCKPIT`, `Verify-Task04.ps1` |
| 8 | Tài liệu | GEMINI.md mới (chưa commit) mất luật an toàn; đặc tả hỏng ký tự | Đặc tả đã khôi phục; GEMINI.md giữ ADR mới + ghép lại 3 cấp, cầu chì, DPAPI, phân vai |

---

## 2. Trạng thái Gate 4 — ✅ E1–E4, E6, R PASS (tự động); E5 kiểm tay ⏳

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
# GEMINI.md và current_task.md trên máy đang có bản chưa commit (bị hỏng ký tự); cất tạm để lấy bản đã sửa:
git stash push -m "gemini-local-task04" GEMINI.md 03_Local_Data_Vault/handoff/current_task.md
git fetch origin
git checkout claude/task-04-cockpit-dispatch
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task04.ps1
```
Script tự gửi `SHUTDOWN` qua pipe cho HostService đang chạy (để build ghi đè được `.exe`), rồi **bật lại HostService và để nó chạy** sau E6.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + quét `D:\` | `-warnaserror` 0/0; không có `D:\NEXUS_Home_Hub` trong `.cs` | ✅ 0/0; không có `D:\` |
| E2 | Dispatch trong tiến trình | Câu "Thiết kế C# Async TCP Server với Redis caching": P95 < 10 ms (20 lần); +20 dòng `agent_dispatch_logs`; `latest_dispatch.json` hợp lệ, không BOM; `/status`, `/mode` chạy, lệnh lạ bị từ chối | ✅ P95 3,002 ms; +20 dòng; JSON hợp lệ, không BOM; slash OK |
| E3 | `/boost` shield | Foreground nằm trong danh sách shield, không bị trim/yield; không tiến trình Whitelist nào bị đụng; không có exception (mặc định chạy thử; `-LiveBoost` chạy thật + khôi phục) | ✅ (chạy thử) foreground `claude` được bảo vệ, Roblox (Whitelist) không bị đụng, không exception. Nhánh ACE-Guard chưa thử thật |
| E4 | WAL đa tiến trình | HostService giữ một read transaction WAL và ghi liên tục trong 10 s, cùng lúc McpTools chạy 20 lượt `dispatch_agent_squad`: 0 lỗi cả hai phía | ✅ 639 ghi/đọc, 0 lỗi; McpTools 20/20 |
| E5 | Kiểm tay | Trong game: `Ctrl+Shift+Space` → gõ nhiệm vụ → Enter → thẻ Squad hiện trên G272 và focus trả về game | ⏳ Tay |
| E6 | Ngân sách | Sau 5 lượt điều phối qua Cockpit: private working set < 60 MB, CPU nền < 1% | ✅ private WS 51,9 MB; WorkingSet64 152,7 MB; CPU 0,00%; 5/5 đúng squad |
| R | Hồi quy | `Verify-Task03.ps1` E1–E5 vẫn ALL PASS | ✅ Verify-Task03 ALL PASS |

---

## 3. Điểm mở cần Architect quyết định

1. **GEMINI.md**: bản Gemini ghi trên máy đã bỏ mất chốt chặn 3 cấp, cầu chì 3 lần thử, quy định DPAPI và phân vai. Claude đã ghép lại (mục 4–6). Xác nhận giữ bản ghép, hay đó là chủ ý bỏ?
2. **Cách đo ngân sách E6**: private working set (Task Manager, đang dùng để chấm) hay `WorkingSet64`? Với `WorkingSet64`, 60 MB khó đạt khi WPF đã nạp (từng đo 76–135 MB).
3. **Background Yield** tác động cả app của chính Nhật Hòa đang mở (trình duyệt, Discord, Claude, Antigravity...) nếu > 100 MB. Có cần danh sách loại trừ do người dùng tự đặt không?
4. **Esc** trong chế độ lệnh giờ là "về game" (theo đặc tả), không còn ẩn Cockpit. Ẩn khẩn cấp chỉ còn `Alt+Backspace`. Có ổn không?
5. **Công tắc Cấp Vàng một cú click** (từ Gate 3, chưa được quyết): "Game Boost" vẫn chạy ngay khi tick; với `WS_EX_NOACTIVATE` click vào HUD dễ bấm nhầm.
6. **Ghi file từ PowerShell**: đề nghị Gemini dùng here-string nháy đơn `@' ... '@` và **commit lên repo** thay vì chỉ ghi trên máy. Lần này cả `current_task.md` lẫn `GEMINI.md` đều hỏng ký tự và không có trên GitHub.
7. **Merge**: ba nhánh `claude/task-02/03/04` xếp chồng nhau. Đề xuất 1 PR `claude/task-04-cockpit-dispatch` → `main` sau khi Gate 4 PASS.

---

## 4. Đề xuất TASK-05

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng vòng Dev-QA từ Cockpit** *(khuyến nghị)* | Lệnh `/verdict <id> passed\|needs_work <ghi chú>` (dùng lại `update_dispatch_verdict`), Recent Dispatches tô màu theo trạng thái, `/agent <slug>` để chọn đích danh. | Hiện vòng lặp chỉ đóng được qua MCP; thêm vào Cockpit là đủ một vòng Dispatch → QA → Verdict. Phạm vi nhỏ, chạy hoàn toàn local. |
| B. Gemini Multimodal Live (PCM 16 kHz) | Giọng nói → Dispatch. | Cần quản lý khóa API bằng DPAPI, quyền mic, đo tải CPU khi chơi. Nên làm sau A. |
| C. Merge + dọn nợ kỹ thuật | Merge 3 nhánh, bỏ chữ "iGPU UHD 730" lỗi thời trong UI, lưu cấu hình bằng file thay vì biến môi trường. | Nhánh xếp chồng càng lâu càng khó merge. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai TASK-04 trên nhánh `claude/task-04-cockpit-dispatch` của https://github.com/bitmap1701/NEXUS-Home-Hub. Hãy đọc `03_Local_Data_Vault/handoff/gate4_handoff.md` và `qa_report.md` §6. Hai lưu ý quan trọng: (1) đặc tả TASK-04 và `GEMINI.md` của bạn chỉ được ghi trên máy, không lên GitHub, và **bị hỏng ký tự** vì here-string PowerShell nháy kép biến backtick thành escape (`agent_dispatch_logs` → `gent_dispatch_logs`, `nvcontainer` → `vcontainer`...). Claude đã khôi phục và commit; lần sau hãy dùng `@' ... '@`. (2) `GEMINI.md` bản mới đã bỏ mất chốt chặn 3 cấp, cầu chì 3 lần thử, quy định DPAPI và phân vai; Claude đã ghép lại, cần bạn xác nhận. Kết quả chạy thật trên máy Nhật Hòa: **Verify-Task04 ALL AUTOMATED CHECKS PASS** (build sạch lần đầu; P95 dispatch trong tiến trình 3 ms; WAL đa tiến trình 0 lỗi; 5 lượt Cockpit đúng 5 squad; private working set 51,9 MB, CPU 0%; Verify-Task03 hồi quy PASS). Với vai Reality Checker: nghiệm thu Gate 4 (còn E5 kiểm tay, và nhánh ACE-Guard của `/boost` chưa chạy thật khi có Delta Force). Với vai Architect: quyết định 7 điểm mở ở §3, rồi chốt TASK-05 (Claude khuyến nghị Hướng A: đóng vòng Dev-QA từ Cockpit).
