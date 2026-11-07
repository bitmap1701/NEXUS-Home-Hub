# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 2 & Nhận TASK-03

- **Ca thực thi**: Claude (tiếp quản từ Antigravity), 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-02-monitor-routing` (chưa merge vào `main`)
- **Commits trong ca**:
  - `40c93c2` fix(task-02): pin Cockpit to MSI G272, log hotkeys, measure idle CPU
  - `5bb0c9e` feat(task-02): Alt+Space shows Cockpit without stealing game focus (option B)
- **Chi tiết kỹ thuật**: xem `qa_report.md` §4.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Cockpit hiển thị trên MSI G272 (iGPU UHD 730) | ❌ Mở trên màn chính LG/GTX 1660 (`SystemParameters.WorkArea`) | ✅ `MonitorLocator.cs` tìm màn theo tên EDID `"G272"`, dự phòng màn phụ đầu tiên; tự định vị lại khi cắm/rút màn |
| 2 | Log nhận diện phím tắt (tiêu chí nghiệm thu 3) | ❌ Không có; `RegisterHotKey` lỗi im lặng | ✅ `HostLog.cs` → `handoff/hostservice.log` + cờ `--console` |
| 3 | Số đo CPU/GPU khi ẩn | ❌ Gán cứng `0.0` | ✅ CPU đo thật (2 s); GPU ghi `null` + chú thích |
| 4 | Focus khi đang chơi game | ❌ `Alt+Space` cướp focus khỏi game | ✅ **Phương án B** (Nhật Hòa duyệt): `Alt+Space` chỉ hiện để xem; `Ctrl+Shift+Space` mới focus ô lệnh |

Kỷ luật: Minimal Change, không đổi kiến trúc, không thêm tính năng ngoài đặc tả. Không đụng Registry hay lệnh Cấp Đỏ.

---

## 2. Trạng thái Gate 2 — ⚠️ CHƯA ĐỦ BẰNG CHỨNG

Code của ca này **chưa được build** (môi trường của Claude không có .NET SDK). Báo cáo đo đạc TASK-02 trước đó có số CPU/GPU gán cứng và Cockpit hiển thị sai màn, nên **số liệu Gate 2 cũ không còn hợp lệ**.

**Đề nghị Reality Checker: chưa PASS Gate 2** cho tới khi Nhật Hòa hoặc Antigravity nộp đủ bằng chứng dưới đây.

### Bằng chứng cần nộp (chạy trên máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin; git checkout claude/task-02-monitor-routing; git pull
dotnet build .\01_Foundation_Engine\NEXUSHomeHub.slnx -c Release -warnaserror
.\01_Foundation_Engine\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe --benchmark
.\01_Foundation_Engine\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe --console
```

| # | Bằng chứng | Tiêu chí PASS |
|---|-----------|---------------|
| E1 | Log `dotnet build` | `0 Warning(s), 0 Error(s)` |
| E2 | `hostservice.log` | Có dòng `Target monitor: MSI G272 (...) match=FriendlyName` và 4 dòng `[HOTKEY] Registered ...` |
| E3 | `task02_benchmark.json` mới | `TargetMonitor` chứa `G272`; `AverageWarmWakeupLatencyMs < 16`; `IdleCpuPercentWhenHidden` là số đo thật |
| E4 | Thử tay phương án B | Mở game/IDE ở màn chính → `Alt+Space` → phím gõ vẫn vào game; `Ctrl+Shift+Space` → con trỏ ở ô `NEXUS>` |
| E5 | Task Manager → Details → cột GPU engine | `NEXUSHome.HostService.exe` chạy trên GPU của Intel UHD 730, không phải GTX 1660 |

Nếu E1 thất bại: dán lỗi build cho ca kế tiếp. Theo cầu chì 3 lần thử, sau 3 lần build lỗi liên tiếp thì dừng và báo cáo vào `qa_report.md`.

---

## 3. Điểm mở cần Architect quyết định

1. **Khóa iGPU bằng Windows Graphics Settings** (Registry `UserGpuPreferences`): đây là thao tác **Cấp Đỏ**, cần HITL. Chỉ cần làm nếu E5 không đạt.
2. **Độ trễ "wake"** hiện chỉ đo tới khi lệnh hiện cửa sổ trả về, chưa đo tới khung hình đầu tiên. Có cần đo tới frame (`CompositionTarget.Rendering`) cho Gate 2 không?
3. **Debounce 150 ms dùng chung** cho mọi tổ hợp phím. Có tách riêng theo từng phím không?
4. **Đường dẫn gốc**: `C:\Agent\NEXUS_Home_Hub` (theo GEMINI.md) hay `D:\NEXUS_Home_Hub`? Code mặc định `C:\Agent\...` (ghi đè bằng `NEXUS_VAULT_PATH`).
5. **Merge**: mở PR `claude/task-02-monitor-routing` → `main` sau khi Gate 2 PASS.

---

## 4. Đề xuất TASK-03 (Gemini Architect chốt)

Antigravity đã đề xuất 2 hướng. Đánh giá của ca Claude:

| Hướng | Phụ thuộc | Rủi ro | Nhận xét |
|-------|-----------|--------|----------|
| **A. Agent Squad Dispatch + Semantic Memory** | Pipe `NEXUSHome.Cockpit.v1`, SQLite WAL, `AgentRegistry` (đã có) | Thấp: chạy 100% local | Ô lệnh hiện chỉ lưu lệnh vào SQLite (`[ORCHESTRATOR] Dispatched...`), chưa định tuyến tới agent thật. Đây là mảnh còn thiếu để Cockpit "làm được việc". |
| **B. Gemini Multimodal Live (WebSocket + PCM 16 kHz + Screen Capture)** | Khóa API qua DPAPI, mic, quyền chụp màn hình | Cao hơn: mạng, quyền riêng tư, tải CPU khi chơi game | Nên làm sau khi có đường dispatch ổn định để nhận kết quả từ Live. |

**Khuyến nghị: TASK-03 = Hướng A**, phạm vi gợi ý:
1. Parser lệnh `@agent <slug> <yêu cầu>` trong `CockpitShellWindow` → tra `AgentRegistry` (283 agents tại `C:\Agent\.agents\skills`).
2. Lệnh pipe mới `DISPATCH <slug> <payload>` trả về trạng thái hàng đợi.
3. Ghi mỗi lượt dispatch vào `agent_memories` với `GovernanceGate` xếp cấp Xanh/Vàng/Đỏ trước khi thực thi.
4. Tiêu chí nghiệm thu đo được: tra agent < 50 ms, 0 lệnh Cấp Đỏ chạy khi chưa có HITL, build `-warnaserror` sạch.

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude vừa hoàn tất một ca trên NEXUS Home Hub. Hãy đọc `03_Local_Data_Vault/handoff/gate2_handoff.md` và `qa_report.md` §4 trên nhánh `claude/task-02-monitor-routing` của https://github.com/bitmap1701/NEXUS-Home-Hub. Với vai Reality Checker: xác nhận Gate 2 còn thiếu bằng chứng nào (E1–E5) và quyết định 5 điểm mở ở §3. Với vai Architect: chốt TASK-03 (Claude khuyến nghị Hướng A, Agent Squad Dispatch) rồi ghi đặc tả vào `03_Local_Data_Vault/handoff/current_task.md`.
