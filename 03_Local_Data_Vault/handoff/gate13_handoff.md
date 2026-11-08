# 📦 HỒ SƠ BÀN GIAO — Gate 13 (TASK-13: HUD Micro-Widgets, `Alt+C` / `Alt+P`, Right MFD, ADR-60)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-08 → 09, 23:57–00:00. Ca trước (Claude, không có Windows) viết code ở `73bdfb0` + `e62ddb7`.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-13-hud-micro-widgets`**, tách từ `main` @`eab13f3`. Đầu nhánh code là **`e62ddb7`**; sau đó chỉ có commit tài liệu của ca này.
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-13)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §18 (§18.4 là kết quả ca này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. **CI GitHub xanh trên `e62ddb7`.**

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Build trên máy | Chỉ có CI | `dotnet build -c Release -warnaserror` 0/0; 96 file, 0 BOM; 0 compiled Regex |
| 2 | Clipboard, Right MFD, MCP | Chưa chạy thật | Chạy trên HostService thật có Cockpit: clipboard Windows đúng từng khối; Right MFD hiện ghim; MCP → Cockpit 131,9 ms, giữ foreground |
| 3 | Gate 13 | ⏳ chưa đủ bằng chứng | ✅ **ALL PASS (E1–E5) ngay lượt 1**, không sửa code hay script |
| 4 | Tài liệu | §18.3 "chưa chạy" | Thêm §18.4 (bảng E1–E5, số đo, quan sát giao diện); viết lại file này |

Không tắt tiến trình nào bằng tay, không đụng `main` hay tag. Trong lúc Verify chạy, Claude không chạy lệnh git nào. `mfd_pins.json` và clipboard dạng chữ đã được script khôi phục; `mfd_pins.json` hiện là `[]`.

---

## 2. Trạng thái Gate 13 — ✅ ALL PASS (E1–E5, lượt 1, `e62ddb7`)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-13-hud-micro-widgets
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1
```
Không chạy lệnh git nào khi Verify đang chạy, và không copy/paste trong lúc đó (script ghi vào clipboard). Lượt chạy không tốn token Gemini, mất khoảng 3 phút. Kết quả ghi vào `task13_verification.json` (`allPassed=True`). Máy lúc chạy bận khoảng 40 % CPU (Chrome, Discord).

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + Regex | `-warnaserror` 0/0; 0 vi phạm BOM; 0 compiled Regex | ✅ 0/0; **96** file, **0** vi phạm; **0** compiled Regex |
| E2 | Tách thẻ + `/copy` | 2 khối code + 1 checklist; tách 4 KB ≤ 0,5 ms; clipboard Windows đúng từng khối; 30 × `COPYWIDGET` P50 ≤ 1,5 / P95 ≤ 2,5 ms | ✅ `[2 Code Blocks \| 1 Checklist \| Alt+C: Copy #1 \| Alt+P: Pin]`; tách **0,015 ms**; `/copy 1`, `/copy 2` đúng; **P50 0,302 / P95 0,433 ms** |
| E3 | Vòng đời ghim | `/pins` = 2; không BOM; 6 ghim còn 5 (FIFO); khóa `AIza…` bị chặn | ✅ `PINNED MFD CARDS (2/5)`; FIFO đúng; `[PIN BLOCKED] Secret Guard`; không BOM |
| E4 | MCP → Cockpit | Right MFD hiện trong < 300 ms, foreground không đổi | ✅ **131,9 ms**; `0x20CF6` → `0x20CF6` |
| E5 | RAM + CPU + hồi quy | Sau 5 ghim + tải: Private WS ≤ 52 MB; CPU < 0,5 %; tầng nhanh < 90 s có `[SLA-WARN SUMMARY]`; Git; CI | ✅ **40,7 MB**; **0 %**; **55,2 s**, `Count=0`, không `[DRIFT-ALERT]`; HEAD không đổi; CI xanh |

Số đo ms trong tầng nhanh (đều dưới SLA cứng): `/calc` 1,166 / 1,696; `/ask` 1,422 / 1,806; `/g` lookup P95 1,129, tổng 2,117 / 3,379 ms.

---

## 3. Điểm mở cần Architect quyết định

1. **Quan sát giao diện mới (nhỏ):** ô thẻ code trên Right MFD in dòng tiêu đề (`csharp: var listener = …`) rồi lặp lại đúng dòng đó ở phần xem trước, vì tiêu đề lấy từ dòng đầu của khối code. Có bỏ dòng đầu khỏi phần xem trước khi nó trùng tiêu đề không? Đây là sửa nhỏ một chỗ, không ảnh hưởng tiêu chí Gate; Claude chưa sửa vì Gate không yêu cầu.
2. **Lịch sử clipboard Windows (Win+V)** vẫn giữ các khối code thử sau mỗi lần Verify (script chỉ khôi phục nội dung hiện hành). Chấp nhận, hay script nên đánh dấu dữ liệu loại khỏi lịch sử (`ExcludeClipboardContentFromMonitorProcessing`)?
3. Các điểm ca trước nêu (1)–(5) ở §18.2, nay đã có số đo hỗ trợ:
   - clipboard Win32 thử một lần: P95 0,433 ms;
   - `/pin` không ghi đè `latest_dispatch.json`;
   - `/pin 8888` là ghi chú;
   - ô ghim là `TextBlock`;
   - phản hồi không có thẻ thì ghim thành `note`.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Merge Gate 13, gắn tag** *(khuyến nghị)* | Nhật Hòa fast-forward `main` lên `claude/task-13-hud-micro-widgets` rồi gắn tag (ví dụ `v0.13.0`) | ALL PASS ngay lượt 1, CI xanh, RAM không tăng |
| B. Sửa nhỏ điểm 3.1 | Bỏ dòng trùng tiêu đề khỏi phần xem trước thẻ code | Giao diện gọn hơn, rủi ro thấp |
| C. TASK-12 | `/img`, `/snap` (ADR-61) | Bước tiếp theo theo ADR-58. Còn khoảng 19 MB dư địa RAM; ảnh tốn bộ nhớ, nên mỗi bước cần đo bằng E2 của Verify-Task13a |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 13 (TASK-13, ADR-60) đã ALL PASS (E1–E5) ngay lượt 1** trên máy Nhật Hòa (Claude Code, 23:57–00:00), nhánh `claude/task-13-hud-micro-widgets`, code ở `e62ddb7`. Không phải sửa code hay script. Chi tiết ở `gate13_handoff.md` và `qa_report.md` §18.4.
>
> - E1: build 0/0; 96 file, 0 BOM; 0 compiled Regex.
> - E2: tách 2 code + 1 checklist, 4 KB trong 0,015 ms; clipboard Windows đúng từng khối; 30 × `COPYWIDGET` P50 **0,302** / P95 **0,433 ms**.
> - E3: Right MFD `PINNED MFD CARDS (2/5)`, FIFO giữ đúng 5, Secret Guard chặn `AIza…`, `mfd_pins.json` không BOM.
> - E4: MCP `manage_hud_widgets` pin → Cockpit **131,9 ms**, foreground không đổi.
> - E5: sau 5 ghim, Private WS **40,7 MB** (Gate 13a: 41,4); CPU 0 %; tầng nhanh 55,2 s, `[SLA-WARN SUMMARY] Count=0`; CI xanh.
>
> Máy chạy lúc bận khoảng 40 % CPU.
>
> Cần bạn quyết:
> - (1) ô thẻ code trên Right MFD lặp dòng đầu (tiêu đề và xem trước trùng nhau): có sửa nhỏ không;
> - (2) lịch sử clipboard Win+V còn các khối thử sau Verify: chấp nhận hay loại khỏi lịch sử;
> - (3) duyệt merge `main` và gắn tag (Nhật Hòa thực hiện).
