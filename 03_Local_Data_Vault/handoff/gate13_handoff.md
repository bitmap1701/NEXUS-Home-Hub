# 📦 HỒ SƠ BÀN GIAO — Gate 13 (TASK-13: HUD Micro-Widgets, `Alt+C` / `Alt+P`, Right MFD, ADR-60)

- **Ca thực thi**: Claude, 2026-11-08 → 09 (ca đêm, sau khi Gemini đóng Gate 13a / `v0.12.2` @`4ab6727`)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude Code trên máy Nhật Hòa
- **Nhánh**: **`claude/task-13-hud-micro-widgets`**, tách từ `main` @`eab13f3`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-13)
- **Chi tiết kỹ thuật**: `qa_report.md` §18
- **Trạng thái build**: phần C# đã được **CI GitHub build xanh**. Script PowerShell, clipboard, giao diện và số đo **chưa chạy** (môi trường của Claude không có Windows).

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Tách thẻ | Không có | `WidgetCardExtractor`: thẻ code (``` / ~~~ + ngôn ngữ), checklist, utility; không Regex |
| 2 | `/copy`, `Alt+C` | Không có | Chép khối code n (mặc định #1, không có thì cả phản hồi); clipboard Win32 thử một lần, có `LastCopiedText` cho kiểm thử / MCP |
| 3 | `/pin`, `/pins`, `/unpin`, `Alt+P` | Không có | `config/mfd_pins.json` (không BOM, Git bỏ qua), tối đa 5 (`maxPinnedCards`), FIFO, chặn lộ khóa, ghi nguyên tử + mutex |
| 4 | Right MFD | Chỉ có công tắc và danh sách dispatch | Mục "PINNED MFD CARDS": thanh chỉ báo thẻ, 5 `TextBlock` cố định, dòng bộ đếm; watcher `mfd_pins.json` |
| 5 | `latest_dispatch.json` | — | Thêm `extractedCards` (≤ 5) và `pinnedCardsCount` |
| 6 | Pipe | — | `COPYWIDGET [n]`, `PINSTATE` |
| 7 | MCP | — | `manage_hud_widgets` (extract / copy / pin / unpin / list_pins) |
| 8 | Kiểm thử | — | Selftest `widgets`, `mfd-pins`; `Verify-Task13.ps1` (E1–E5); sao lưu và khôi phục `mfd_pins.json` + clipboard của Nhật Hòa |

---

## 2. Trạng thái Gate 13 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (C# đã build trên CI; chưa chạy trên máy)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-13-hud-micro-widgets
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1
```
Không chạy lệnh git nào trong repo khi Verify đang chạy. Lượt này không tốn token Gemini. Script sẽ ghi vào clipboard khoảng 40 lần (khối code thử) rồi khôi phục nội dung clipboard dạng chữ ban đầu; lịch sử clipboard (Win+V) có thể còn các mục thử.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + Regex | `-warnaserror` 0/0; 0 vi phạm BOM; 0 tùy chọn compiled Regex trong mã C# | Chưa chạy (CI: build 0/0, 0 BOM) |
| E2 | Tách thẻ + `/copy` | 2 khối code (csharp, powershell) + 1 checklist, nội dung không kèm rào; tách 4 KB ≤ 0,5 ms; trên HostService thật: clipboard Windows đúng từng khối; 30 lần `COPYWIDGET` **P50 ≤ 1,5 ms, P95 ≤ 2,5 ms** | Chưa chạy |
| E3 | Vòng đời ghim | `/unpin all` → `/pin #1` → `/pin Cổng TCP Socket UTH: 8888` → `/pins` = 2; `mfd_pins.json` không BOM; ghim thêm 6 thẻ vẫn đúng 5; `/pin AIzaSyDummy…` bị chặn, không vào file | Chưa chạy |
| E4 | MCP → Cockpit | `manage_hud_widgets` pin → Right MFD hiện trong **< 300 ms**, foreground không đổi | Chưa chạy |
| E5 | RAM + CPU + hồi quy | Sau 5 ghim + tải copy + dọn khi rảnh: Private WS (2 lần đọc ổn định) **≤ 52,0 MB**; CPU rảnh < 0,5 %; `-FastRegression` < 90 s có `[SLA-WARN SUMMARY]`; Git không đổi; CI xanh | Chưa chạy |

---

## 3. Điểm mở cần Architect quyết định

1. **Clipboard Win32 thử một lần** thay cho `Clipboard.SetText` của WPF (WPF tự thử lại khoảng 10 × 100 ms khi clipboard bận). Clipboard bận thì báo rõ và vẫn giữ `LastCopiedText`.
2. **`/pin` không ghi đè `latest_dispatch.json`** (nếu ghi đè, "phản hồi hiện hành" thành chính thông báo ghim). Cockpit đồng bộ qua watcher của `mfd_pins.json`.
3. **`/pin 8888` là ghi chú, `/pin #2` mới là chỉ số thẻ**, để ghim được số cổng / IP. Riêng MCP nhận cả `2` và `#2` là chỉ số.
4. **Ô ghim là `TextBlock` chỉ đọc**, không phải `TextBox`, nên không có bộ đệm undo nào.
5. **Phản hồi không có thẻ nào** thì `/pin` ghim cả phản hồi thành thẻ `note`.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 13** *(khuyến nghị)* | Claude Code chạy `Verify-Task13.ps1`; sửa tối thiểu nếu lỗi script / giao diện; ghi §18.4 | C# đã build, phần chạy thật chưa có bằng chứng |
| B. TASK-12 | `/img`, `/snap` (ADR-61) | Bước tiếp theo theo ADR-58; cần kiểm soát RAM vì ảnh tốn bộ nhớ |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-13 (ADR-60)** trên nhánh `claude/task-13-hud-micro-widgets` (tách từ `main` @`eab13f3`). Phần C# đã được **CI GitHub build xanh**; giao diện, clipboard và số đo cần Claude Code chạy `Verify-Task13.ps1` trên máy Nhật Hòa. Chi tiết ở `gate13_handoff.md` và `qa_report.md` §18.
>
> Đã làm:
> - `WidgetCardExtractor` (thẻ code / checklist / utility, không Regex); `extractedCards` + `pinnedCardsCount` trong `latest_dispatch.json`.
> - `/copy [n]` + `Alt+C` (clipboard Win32 thử một lần, có `LastCopiedText`).
> - `/pin`, `/pin #n`, `/pin <văn bản>`, `/pins`, `/unpin <id|all>` + `Alt+P`, lưu `config/mfd_pins.json` (tối đa 5, FIFO, Secret Guard, ghi nguyên tử + mutex).
> - Right MFD 5 `TextBlock` cố định, thanh chỉ báo thẻ, dòng bộ đếm, watcher `mfd_pins.json`.
> - Pipe `COPYWIDGET` / `PINSTATE`; MCP `manage_hud_widgets`. `Alt+C` / `Alt+P` chỉ trong cửa sổ Cockpit ở Command mode, không `RegisterHotKey`.
>
> Cần bạn quyết:
> - (1) clipboard Win32 thử một lần thay cho WPF `Clipboard.SetText`;
> - (2) `/pin` không ghi đè `latest_dispatch.json`, đồng bộ qua watcher `mfd_pins.json`;
> - (3) `/pin 8888` là ghi chú, `/pin #2` là chỉ số thẻ;
> - (4) ô ghim là `TextBlock` chỉ đọc;
> - (5) phản hồi không có thẻ thì `/pin` ghim cả phản hồi thành thẻ `note`.
