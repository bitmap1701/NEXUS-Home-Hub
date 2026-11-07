# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 3 (TASK-03) & Nhận TASK-04

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-03-agent-squad-dispatch` (tách từ `claude/task-02-monitor-routing`; cả hai chưa merge vào `main`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (bản TASK-03 của Gemini, nay đã nằm trong repo)
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
| 7 | Đường dẫn ổ đĩa | Cố định `C:\Agent\...` | `NexusPaths` tự dò thư mục gốc repo (chạy được ở `C:\` lẫn `D:\`) |
| 8 | Kiểm thử | Không có | `tools/Verify-Task03.ps1` chạy E1–E5, xuất `task03_verification.json` |

Không đổi kiến trúc tiến trình. Không có thao tác Cấp Đỏ nào được tự chạy (script Registry chỉ chạy khi Nhật Hòa tự gọi).

---

## 2. Trạng thái Gate 3 — ⚠️ CHƯA ĐỦ BẰNG CHỨNG

Code **chưa được build**, E1–E5 **chưa chạy**. Môi trường của Claude không có .NET SDK, PowerShell hay quyền truy cập NuGet. Phần đã kiểm chứng ngoài .NET bằng mô phỏng Python: chỉ mục ra 283 agent thật; câu E3 định tuyến vào Squad1.

**Đề nghị Reality Checker: chưa PASS Gate 3** cho tới khi có `task03_verification.json` với `allPassed: true`. Gate 2 cũng vẫn chờ bằng chứng E1–E5 của `gate2_handoff.md`.

### Lệnh chạy (máy Nhật Hòa, ở thư mục gốc repo)
```powershell
git fetch origin
git checkout claude/task-03-agent-squad-dispatch
git pull
# Dừng HostService cũ đang chạy (nếu có) để E5 kiểm tra đúng bản mới
Get-Process NEXUSHome.HostService -ErrorAction SilentlyContinue | Stop-Process
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task03.ps1
```
> Lệnh `Stop-Process` dừng một tiến trình của chính dự án, do bạn tự chạy. Agent không tự chạy lệnh này.

| # | Bằng chứng | Tiêu chí PASS | Script kiểm |
|---|-----------|---------------|-------------|
| E1 | Build | `dotnet build -c Release -warnaserror` exit 0 | ✅ |
| E2 | MCP contract | `tools/list` có đủ `list_agent_squads`, `dispatch_agent_squad`, `update_dispatch_verdict` | ✅ |
| E3 | Dispatch + SQLite | Câu UDP → `Squad1`; SystemInstruction có persona; `dispatchId > 0`; cập nhật verdict cho id đó thành công, id không tồn tại bị từ chối; P95 `elapsedMs` < 50 ms trên 20 lần | ✅ |
| E4 | Path traversal | `preferredAgentId = "../../GEMINI.md"` → `isError: true`, thông báo `Rejected` | ✅ |
| E5 | Gate 2 regression | Pipe JSONL trả `ok: true`; log của lần khởi động gần nhất có `RenderMode=SoftwareOnly` và `HUD style: NOACTIVATE\|TOOLWINDOW` | ✅ |
| E6 | Kiểm tra tay | Cockpit hiện trên MSI G272; đang chơi game bấm `Alt+Space` → game giữ focus; click vào HUD → game vẫn giữ focus; `Ctrl+Shift+Space` → gõ được lệnh | Tay |
| E7 | CPU khi Cockpit mở | Task Manager: CPU của `NEXUSHome.HostService` khi Cockpit đang hiện (chế độ `SoftwareOnly`) | Tay |

Nếu E1 lỗi: dán log lỗi cho ca kế tiếp. Áp dụng cầu chì 3 lần thử.

---

## 3. Điểm mở cần Architect quyết định

1. **Quyết định 5 không khớp hiện trạng**: `CockpitUI` đang là thư viện chạy *trong* tiến trình `HostService`, chưa từng tách. Chọn một:
   - **(a)** Giữ một tiến trình như hiện tại (không tốn công; wake ~ms), sửa lại câu chữ của quyết định 5.
   - **(b)** Tách `CockpitUI` thành `.exe` riêng do `HostService` giám sát qua pipe. Đây là thay đổi lớn, nên thành một task riêng, và phải đo lại ngân sách wake < 16 ms.
2. **`SoftwareOnly` và CPU khi chơi game**: render bằng CPU không đụng GTX 1660 nhưng chiếm CPU của game. Nếu E7 cho thấy tốn CPU đáng kể, có chuyển sang `Hardware` + `GpuPreference=1` (Cấp Đỏ, Nhật Hòa tự chạy script) không?
3. **Squad5 primary**: chọn `testing-tool-evaluator` (đánh giá phần cứng) hay `support-finance-tracker` (ngân sách)? Mảng "Fitness" chưa có agent chuyên trách trong The Agency.
4. **Định nghĩa "6-Part Agent Anatomy"**: Claude đã diễn giải thành Identity / Mission / Critical Rules / Hardware / Deliverables & QA / Workflow. Cần Architect xác nhận hoặc sửa.
5. **Merge**: hai nhánh `claude/task-02-*` và `claude/task-03-*` xếp chồng nhau. Đề xuất mở 1 PR `claude/task-03-agent-squad-dispatch` → `main` sau khi Gate 2 + Gate 3 PASS.

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

> Chào Gemini, Claude đã triển khai TASK-03 (Agent Squad Dispatch) trên nhánh `claude/task-03-agent-squad-dispatch` của https://github.com/bitmap1701/NEXUS-Home-Hub. Hãy đọc `03_Local_Data_Vault/handoff/gate3_handoff.md` và `qa_report.md` §5. Lưu ý: code chưa được build (Claude không có .NET SDK); Nhật Hòa sẽ chạy `01_Foundation_Engine/tools/Verify-Task03.ps1` và gửi `task03_verification.json`. Với vai Reality Checker: chỉ PASS Gate 3 khi có file đó với `allPassed: true`. Với vai Architect: quyết định 5 điểm mở ở §3, quan trọng nhất là quyết định 5 (tách tiến trình) vì hiện trạng không khớp. Sau đó chốt TASK-04 (Claude khuyến nghị Hướng A: nối Cockpit vào Dispatch) và ghi đặc tả vào `current_task.md` rồi commit lên repo, thay vì chỉ ghi file trên máy.
