# 📦 HỒ SƠ BÀN GIAO — Gate 14 (TASK-14, ADR-59, dự kiến v0.12.0)

- **Ca thực thi**: Claude, 2026-11-08 (ca tối, sau khi Gemini duyệt Gate 11b)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor), Claude Code trên máy Nhật Hòa
- **Nhánh**: **`claude/task-14-local-utilities`**, tách từ `main` @`ced587b`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-14)
- **Chi tiết kỹ thuật**: `qa_report.md` §15
- **Trạng thái build**: **chưa build**. Môi trường của Claude không có .NET SDK, và lúc làm việc máy Nhật Hòa chưa nối với phiên này. File này không ghi số liệu đo.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Gate 11b | Gemini duyệt PASS; `main` đã ở `c0618fa`, tag `v0.11.1` đã có trên GitHub | Kiểm tra lại trên GitHub: tag `v0.11.1` trỏ đúng `c0618fa`, `main` = `ced587b` (thêm đặc tả TASK-14) |
| 2 | `/calc`, `/c`, `/=` | Không có | `LocalCalculator.cs`: bộ phân tích viết tay, Int64 chính xác, Dec/Hex/Bin, bitwise `<< >> & \|`, 15 hàm, `[CALC ERROR]` cho mọi lỗi |
| 3 | `/timer`, `/timers` | Không có | `LocalTimerManager.cs`: timer chạy một lần cho mỗi bộ đếm, 0 đối tượng khi rảnh; `list`, `cancel <id\|all>`, `stop`, `clear`. Hết giờ thì hiện Cockpit ở Passive HUD, không lấy focus |
| 4 | `/note` | Không có | `LocalNoteService.cs`: ghi nguyên tử vào `zettelkasten/quick_notes.md` (không BOM, có trong `.gitignore`); `list [N]`, `find`/`search`; Secret Guard |
| 5 | `/ground` thấy ghi chú mới | Bộ nhớ 5 s có thể trả kết quả cũ | `Invalidate()` sau mỗi `/note`; bộ nhớ hết hạn khi `quick_notes.md` đổi; ghi chú khớp tốt được giữ một ô trong Top-3 |
| 6 | MCP `run_local_utility` | Không có | Công cụ Cấp Xanh mới; ghi `latest_dispatch.json` để Cockpit nhận qua watcher; audit che khóa |
| 7 | `latest_dispatch.json` | Chỉ có dispatch thật (`dispatchId > 0`) | Thêm event `local_utility` / `timer_expired`, `dispatchId = 0`, `ai_model = local-utility`, token = 0, trường `local_utility` |
| 8 | Overwolf | Không có trong `combatProcessPrefixes` mặc định | Đã thêm vào mặc định và vào danh sách dự phòng của Verify-Task06 |
| 9 | Kiểm thử | — | `SelfTest.Task14.cs` (4 kịch bản) + `Verify-Task14.ps1` (E1–E5, hồi quy Task11 → 03, `git status` sạch) |
| 10 | Tài liệu | ADR-59 "Dự kiến" | ADR-59 "Đã triển khai, chờ Gate 14"; roadmap §1 ghi đúng đường dẫn `zettelkasten/quick_notes.md`; `qa_report.md` §15 |

---

## 2. Trạng thái Gate 14 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (chưa build, chưa chạy)

### Lệnh chạy (Claude Code hoặc Nhật Hòa trên máy)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14-local-utilities
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14.ps1
```
Trước khi chạy: tắt game và ứng dụng nặng (bài học Gate 11b lần 4). Không dùng `-SkipBuild`. Script gồm hồi quy Verify-Task11 → 03 (khoảng 12 phút, có một lượt gọi Gemini thật trong Task11 E5). `-SkipLive` chỉ dùng khi chưa có khóa.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + `schema14` | `dotnet build -c Release -warnaserror` 0/0; 0 file BOM (gồm `quick_notes.md`); ≥ 3 001 dòng SQLite; `quick_notes.md` bị Git bỏ qua; Overwolf trong danh sách mặc định | Chưa chạy |
| E2 | `local-calc` | `sqrt(144) + 2^8 = 268 (Hex: 0x10C \| Bin: 0b100001100)`; `0xFF + 0b101 = 260 (Hex: 0x104 \| …)`; `10 / 0` → `[CALC ERROR] Chia cho 0.`; 14 lỗi khác đều `[CALC ERROR]`, không ném ngoại lệ; 30 lần `/calc` sau warm-up: **P50 ≤ 1,5 ms, P95 ≤ 2,5 ms**; token live, mock, session, RPM, TPM và số dòng dispatch không đổi | Chưa chạy |
| E3 | `local-timer` + HostService thật | Bộ phân tích thời lượng đúng 14 trường hợp; `/timer 250ms Kiểm thử tự động Gate 14` → `/timer list` ACTIVE → hết giờ trong 400 ms, `latest_dispatch.json` = `EXPIRED`; trên HostService thật: Cockpit đang ẩn hiện ở Passive HUD (`NOACTIVATE`), `GetForegroundWindow()` không đổi (theo HostService và theo chính script); `/timer 60s ToCancel` → `/timer cancel all` → danh sách rỗng; 0 đối tượng timer, CPU rảnh < 1 % | Chưa chạy |
| E4 | `local-note` (mock) | `/note Mã cổng thử nghiệm của Gate 14 là 49152` → file không BOM, `/note list` và `/note find 49152` trả đúng; khóa giả `AIzaSyDummy…` bị chặn, không có trong `quick_notes.md`, SQLite hay `latest_dispatch.json`; `/ground Mã cổng thử nghiệm của Gate 14 là bao nhiêu?` trả nguồn `[zettelkasten/quick_notes.md#…]` chứa `49152`, không khởi động lại; ghi chú test được xóa lại | Chưa chạy |
| E5 | MCP + tài nguyên + hồi quy | `run_local_utility` calc → Cockpit nhận trong **< 300 ms** (SYNCSTATE `latencyMs`); MCP note có khóa bị chặn; Private WS < 60 MB; CPU HostService < 1 %; hồi quy Task11 → 03 ALL PASS; `git status --porcelain` rỗng | Chưa chạy |

Report: `03_Local_Data_Vault/handoff/task14_verification.json`.

---

## 3. Điểm mở cần Architect quyết định

1. **Bộ đếm**: mỗi bộ đếm một `Timer` chạy một lần (tối đa 20), thay vì một `Timer` duy nhất như bản nháp roadmap. Cả hai cách đều 0 % CPU khi rảnh. Claude đề nghị giữ.
2. **Ô dành cho ghi chú trong Top-3 của `/ground`.** Nếu chưa có ghi chú nào trong Top-3, ghi chú khớp nhiều nhất (ít nhất một nửa từ khóa) lấy ô thứ ba. Cần quy tắc này vì một dòng ghi chú ngắn thua các tài liệu bàn giao dài khi chỉ đếm từ khóa (`current_task.md` chứa nguyên câu hỏi E4). Đây là thay đổi nhỏ với ADR-45b; xin duyệt.
3. **Ngữ nghĩa `/calc`**:
   - `^` là lũy thừa;
   - `log` là logarit tự nhiên, có thêm `ln`, `log10`;
   - số âm in `-0x…` (dấu + độ lớn);
   - `round` làm tròn 0,5 ra xa số 0;
   - không nhân ngầm (`2*pi`, không `2pi`).
4. **Snapshot cục bộ** dùng `dispatchId = 0` và không tạo dòng SQLite. Thẻ Cockpit chuyển sang kết quả tiện ích; dispatch gần nhất vẫn ở SQLite và danh sách Recent.
5. **Bộ đếm qua MCP** chỉ sống cùng tiến trình McpTools. Muốn bộ đếm MCP sống cùng HostService thì cần chuyển tiếp qua pipe; đề xuất để sau.
6. **Overwolf**: `nexus_config.json` trên máy Nhật Hòa có danh sách riêng, nên Nhật Hòa cần tự thêm `"Overwolf"`. Khi Overwolf chạy nền, SLA ở LiveCombat (25 ms) suốt thời gian đó. Như vậy có đúng ý Architect không?
7. **Nhãn Private Working Set trên HUD**: để TASK-13 (cần bộ đếm hiệu năng, không còn là chỉnh nhẹ).
8. **Ngưỡng E2 1,5 / 2,5 ms** được đo trên toàn bộ đường đồng bộ, gồm cả ghi nguyên tử `latest_dispatch.json`. Gate 11b đo đường `/ask` trúng cache (có thêm giao dịch SQLite) được P50 khoảng 1,3 ms khi máy rảnh, nên dự kiến đạt. Máy bận thì có thể trượt như Gate 11b lần 4.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 14** *(khuyến nghị, làm ngay)* | Claude Code chạy `Verify-Task14.ps1` trên máy Nhật Hòa. Nếu build lỗi, sửa trên chính nhánh này (ghi vào §15.3), chạy lại đến ALL PASS. Sau khi Gemini duyệt: fast-forward `main`, Nhật Hòa đẩy tag `v0.12.0` | Code chưa build lần nào; cần bằng chứng trước mọi bước khác |
| B. ADR-55 + ADR-56 | `-FastRegression` / `-FullRegression` và GitHub Actions (build `-warnaserror` + 0 BOM) | Roadmap xếp ngay sau Gate 11b mà chưa làm. Chuỗi hồi quy nay khoảng 12 phút và có gọi Gemini thật; CI còn bắt được lỗi biên dịch mà môi trường của Claude không chạy được |
| C. TASK-13 | HUD Micro-Widgets, Right MFD, nhãn Private WS | Bước kế tiếp theo ADR-58 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-14 (ADR-59)** trên nhánh `claude/task-14-local-utilities` (tách từ `main` @`ced587b`). **Chưa build**: môi trường của Claude không có .NET SDK, nên chưa có số đo. Gate 14 cần Claude Code chạy `Verify-Task14.ps1` trên máy Nhật Hòa. Chi tiết ở `gate14_handoff.md` và `qa_report.md` §15.
>
> Đã làm:
> - `/calc` (`/c`, `/=`): bộ phân tích viết tay, Int64 chính xác, in Dec/Hex/Bin, bitwise, `[CALC ERROR]` cho mọi lỗi.
> - `/timer`: mỗi bộ đếm một timer chạy một lần, 0 đối tượng khi rảnh; hết giờ thì hiện Passive HUD (`WS_EX_NOACTIVATE`), có lệnh pipe `TIMERALERT` làm bằng chứng focus.
> - `/note`: ghi nguyên tử vào `zettelkasten/quick_notes.md` (không BOM, trong `.gitignore`), có Secret Guard; `/ground` thấy ghi chú ngay, không khởi động lại.
> - MCP `run_local_utility` (Cấp Xanh); `latest_dispatch.json` thêm event `local_utility` / `timer_expired` với token = 0, `ai_model = local-utility`.
> - Thêm Overwolf vào `combatProcessPrefixes` mặc định.
>
> Cần bạn quyết:
> - (1) mỗi bộ đếm một timer chạy một lần thay vì một timer duy nhất;
> - (2) dành ô thứ ba của Top-3 `/ground` cho ghi chú khớp ít nhất một nửa từ khóa (thay đổi nhỏ với ADR-45b);
> - (3) ngữ nghĩa `/calc`: `^` = lũy thừa, `log` = ln, số âm in `-0x…`;
> - (4) bộ đếm qua MCP chỉ sống cùng tiến trình McpTools;
> - (5) Overwolf làm SLA ở LiveCombat suốt lúc chạy nền, và Nhật Hòa phải tự thêm vào `nexus_config.json`.
>
> Đề xuất sau Gate 14: làm ADR-55/56 (hồi quy hai tầng + CI) trước TASK-13, vì chuỗi hồi quy đã khoảng 12 phút và CI bắt được lỗi biên dịch sớm.
