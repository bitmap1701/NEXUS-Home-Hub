# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 3 (TASK-03) & Nhận TASK-04

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-03-agent-squad-dispatch` (tách từ `claude/task-02-monitor-routing`; cả hai chưa merge vào `main`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (bản TASK-03 của Gemini, nay đã nằm trong repo)
- **Commits trong ca**: `efffe8b` (TASK-03), `83f1bfc` (WFAC010 + 283 agent), `fd2b857` (chẩn đoán script), `b76dc68` (BOM stdin MCP)
- **Chi tiết kỹ thuật**: `qa_report.md` §5

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Kho agent | `AgentRegistry` chỉ liệt kê tên thư mục; đọc được file bất kỳ qua `../` | Chỉ mục metadata 283 agent (2 cách bố trí), có alias, loại trùng, chống path traversal |
| 2 | 5 hồ sơ hạt nhân | Không có | Tự tạo trong `02_Agents_Registry`: chép bản thật nếu đã cài, nếu chưa thì tạo file giữ chỗ có đánh dấu |
| 3 | Điều phối | Ô lệnh chỉ ghi chữ vào SQLite | `SquadDispatcher`: 5 squad, định tuyến theo từ khóa, gói SystemInstruction 6 phần |
| 4 | SQLite WAL | 2 bảng | Thêm `agent_dispatch_logs` (8 cột theo đặc tả + `CHECK`) |
| 5 | MCP tools | 4 tools | 7 tools: thêm `list_agent_squads`, `dispatch_agent_squad`, `update_dispatch_verdict`; lỗi trả `isError` hoặc JSON-RPC `-32603` |
| 6 | Gate 2 quyết định 1–4 | Chưa có | Pipe JSONL `NEXUS_Home_Cockpit_Pipe`; `SoftwareOnly`; PerMonitorV2 + dự phòng góc phải màn chính; `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` |
| 7 | Đường dẫn ổ đĩa | Cố định `C:\Agent\...` | `NexusPaths` tự dò thư mục gốc repo. Đã xác nhận máy dùng `C:\Agent\NEXUS_Home_Hub` (không có ổ `D:`) |
| 8 | Kiểm thử | Không có | `tools/Verify-Task03.ps1` chạy E1–E5, xuất `task03_verification.json` |
| 10 | Game Boost | Target cố định PUBG; không thấy target vẫn dọn RAM **cả game đang chơi** (Delta Force còn 424 K, "Not responding") | Target theo tiền tố (`NEXUS_GAME_PROCESS`, mặc định `DeltaForceClient`); không thấy target thì không dọn gì (chưa build lại) |
| 9 | MCP server stdin | **Request đầu tiên của mỗi phiên bị bỏ qua** khi client là PowerShell 5.1 (BOM UTF-8) | Bỏ BOM ở đầu dòng; JSON hỏng trả lỗi `-32700` (lỗi có từ Phase 1, phát hiện nhờ E3) |

Không đổi kiến trúc tiến trình. Không có thao tác Cấp Đỏ nào được tự chạy (script Registry chỉ chạy khi Nhật Hòa tự gọi).

---

## 2. Trạng thái Gate 3 — ✅ E1–E6 PASS; E7 còn thiếu một ảnh đo

Chạy trên máy Nhật Hòa lúc 2026-11-07 12:31 (`C:\Agent\NEXUS_Home_Hub`, .NET SDK 10.0.401) bằng `01_Foundation_Engine/tools/Verify-Task03.ps1`, tại commit `b76dc68` → **`ALL PASS (E1-E5)`**. Bằng chứng gốc: `03_Local_Data_Vault/handoff/task03_verification.json`.

| # | Bằng chứng | Kết quả |
|---|-----------|---------|
| E1 | `dotnet build -c Release -warnaserror` | ✅ 0 Warning(s), 0 Error(s) |
| E2 | `tools/list` | ✅ 7 tools, đủ `list_agent_squads`, `dispatch_agent_squad`, `update_dispatch_verdict` |
| E3 | Dispatch + SQLite | ✅ Squad1 / `engineering-backend-architect` / QA `testing-reality-checker`; persona thật 13.038 ký tự; dispatch #64; cập nhật verdict thành công, id không tồn tại bị từ chối; **P95 0,454 ms** (20 lần, trong tiến trình) |
| E4 | Path traversal | ✅ `../../GEMINI.md` bị từ chối |
| E5 | Gate 2 regression | ✅ Pipe JSONL `ok=True`; `RenderMode=SoftwareOnly`; `NOACTIVATE\|TOOLWINDOW`; Cockpit trên **MSI G272** (`\\.\DISPLAY6`, match=FriendlyName) |
| — | Registry | 283 agent; cả 5 squad có primary thật |
| E6 | Kiểm tay: game giữ focus khi `Alt+Space` và khi click HUD; `Ctrl+Shift+Space` gõ được lệnh | ✅ Game mượt, Cockpit trên G272, click HUD không mất focus; `telemetry` + Enter trả kết quả. ⚠️ Chế độ gõ lệnh wake 28,7–79 ms (> 16 ms); chế độ xem 0,74–3,10 ms |
| E7 | Kiểm tay: CPU của HostService khi Cockpit mở (`SoftwareOnly`) | ⏳ 0% CPU / 22,7 MB (chưa rõ Cockpit đang hiện hay ẩn) |

**Đề nghị Reality Checker**: Gate 3 đủ bằng chứng để PASS phần TASK-03. Verify-Task03 chạy lại sau bản sửa Game Boost (`812eb80`) vẫn ALL PASS. E7 (CPU khi Cockpit hiện) mới có một lần đo 0% / 22,7 MB, chưa rõ trạng thái hiện/ẩn, nên có thể PASS có điều kiện.

**Gate 2 (từ `gate2_handoff.md`)**: lần chạy này cũng kiểm chứng được E1 (build sạch) và E2 (Cockpit nhận đúng MSI G272 theo EDID) của Gate 2. Còn chờ: số đo wake latency mới (`--benchmark`) và kiểm tay focus/GPU, tức E6–E7 ở trên.

Lệnh chạy lại khi cần:
```powershell
cd C:\Agent\NEXUS_Home_Hub
git pull
Get-Process NEXUSHome.HostService -ErrorAction SilentlyContinue | Stop-Process
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task03.ps1
```
Mỗi lần chạy thêm 21 dòng thử nghiệm vào `agent_dispatch_logs`.

---

## 3. Điểm mở cần Architect quyết định

1. **Quyết định 5 không khớp hiện trạng**: `CockpitUI` đang là thư viện chạy *trong* tiến trình `HostService`, chưa từng tách. Chọn một:
   - **(a)** Giữ một tiến trình như hiện tại (không tốn công; wake ~ms), sửa lại câu chữ của quyết định 5.
   - **(b)** Tách `CockpitUI` thành `.exe` riêng do `HostService` giám sát qua pipe. Đây là thay đổi lớn, nên thành một task riêng, và phải đo lại ngân sách wake < 16 ms.
2. **`SoftwareOnly` và CPU khi chơi game**: render bằng CPU không đụng GTX 1660 nhưng chiếm CPU của game. Nếu E7 cho thấy tốn CPU đáng kể, có chuyển sang `Hardware` + `GpuPreference=1` (Cấp Đỏ, Nhật Hòa tự chạy script) không?
3. **Squad5 primary**: chọn `testing-tool-evaluator` (đánh giá phần cứng) hay `support-finance-tracker` (ngân sách)? Mảng "Fitness" chưa có agent chuyên trách trong The Agency.
4. **Định nghĩa "6-Part Agent Anatomy"**: Claude đã diễn giải thành Identity / Mission / Critical Rules / Hardware / Deliverables & QA / Workflow. Cần Architect xác nhận hoặc sửa.
5. **Game Boost (sự cố §5.4–5.5 qa_report)**: sau bản sửa, Game Boost không còn dọn RAM của game, nhưng **không nâng được độ ưu tiên của Delta Force** (`0 tiến trình`), nhiều khả năng do anti-cheat ACE-Guard. Chọn: (a) tắt hẳn Game Boost cho game có anti-cheat (Claude khuyến nghị, vì mở handle tới tiến trình được anti-cheat bảo vệ là rủi ro); (b) chỉ giữ phần dọn RAM app nền, có danh sách giới hạn; (c) giữ nguyên. Ngoài ra: có yêu cầu xác nhận trước khi chạy công tắc Cấp Vàng không, vì với `NOACTIVATE`, click vào HUD dễ bấm nhầm?
6. **Chế độ gõ lệnh 79 ms**: chấp nhận vì người dùng chủ động gõ, hay đặt ngân sách riêng cho chế độ này?
7. **Merge**: hai nhánh `claude/task-02-*` và `claude/task-03-*` xếp chồng nhau. Đề xuất mở 1 PR `claude/task-03-agent-squad-dispatch` → `main` sau khi Gate 2 + Gate 3 PASS.

---

## 4. Đề xuất TASK-04 (Gemini Architect chốt)

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Nối Cockpit vào Dispatch** *(khuyến nghị)* | Ô lệnh `@agent <yêu cầu>` trong Cockpit gọi `SquadDispatcher` (cùng tiến trình), hiện Squad/Agent/QA và `dispatchId` trên Center HUD; thêm lệnh pipe `DISPATCH`. | Hiện dispatch mới chỉ gọi được qua MCP; Cockpit vẫn chỉ ghi chữ vào SQLite. Đây là mảnh còn thiếu để phím tắt `Ctrl+Shift+Space` thực sự "gọi agent". Phạm vi nhỏ, chạy hoàn toàn local. |
| B. Gemini Multimodal Live (WebSocket + PCM 16 kHz) | Luồng giọng nói, kết quả đổ về Dispatch. | Cần quản lý khóa API bằng DPAPI, quyền mic, tải CPU khi chơi game. Nên làm sau A. |
| C. Tách tiến trình CockpitUI | Chỉ khi điểm mở 1 chọn (b). | Thay đổi kiến trúc lớn. |

**Tiêu chí nghiệm thu gợi ý cho TASK-04A**: từ lúc gõ `@agent tối ưu socket` + Enter tới khi HUD hiện kết quả < 100 ms; 0 lệnh Cấp Đỏ chạy khi chưa có HITL; build `-warnaserror` sạch; Verify-Task03 vẫn PASS (không hồi quy).

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã hoàn tất TASK-03 (Agent Squad Dispatch) trên nhánh `claude/task-03-agent-squad-dispatch` của https://github.com/bitmap1701/NEXUS-Home-Hub. Kiểm thử thật trên máy Nhật Hòa: **Verify-Task03 E1–E5 ALL PASS** (build `-warnaserror` sạch; 283 agent; câu UDP vào Squad1; P95 dispatch dưới 2 ms; path traversal bị chặn; Cockpit nhận đúng MSI G272), **E6 kiểm tay PASS** (đang chơi Delta Force, `Alt+Space` hiện Cockpit mà game giữ focus; `Ctrl+Shift+Space` gõ lệnh được). Quá trình chạy thật phát hiện và sửa 4 lỗi, trong đó có 2 lỗi từ Phase 1: MCP server bỏ qua request đầu tiên khi client là PowerShell (BOM); và **Game Boost dọn RAM của chính game đang chơi** (nhắm cứng vào PUBG, Delta Force tụt còn 424 K và "Not responding"). Hãy đọc `03_Local_Data_Vault/handoff/gate3_handoff.md` và `qa_report.md` §5. Với vai Reality Checker: nghiệm thu Gate 3 (E7 còn thiếu một ảnh đo CPU). Với vai Architect: quyết định 7 điểm mở ở §3, quan trọng nhất là quyết định 5 (CockpitUI hiện chạy chung tiến trình, chưa từng tách) và Game Boost với game có anti-cheat ACE-Guard. Máy Nhật Hòa không có ổ `D:`, dự án nằm ở `C:\Agent\NEXUS_Home_Hub`. Sau đó chốt TASK-04 (Claude khuyến nghị Hướng A: nối Cockpit vào Dispatch), ghi đặc tả vào `current_task.md` và commit lên repo.
