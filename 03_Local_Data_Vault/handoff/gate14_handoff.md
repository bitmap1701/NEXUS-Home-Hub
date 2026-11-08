# 📦 HỒ SƠ BÀN GIAO — Gate 14 (TASK-14, ADR-59, dự kiến v0.12.0)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-08, kết thúc 17:26. Ca trước (Claude, chưa build) viết code ở `ec01576`.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: **`claude/task-14-local-utilities`**, tách từ `main` @`ced587b`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-14)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §15 (§15.3 là kết quả lần chạy này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror`, 0 warning / 0 error.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Build | Chưa build lần nào (ca viết code không có .NET SDK) | `dotnet build -c Release -warnaserror`: 0 Warning(s), 0 Error(s) |
| 2 | `Verify-Task14.ps1` | Chưa chạy | Chạy 1 lần, có build, có hồi quy Task11 → 03: **ALL PASS (E1–E5)**, exit 0 |
| 3 | Sửa code | — | **Không cần sửa**: build và kiểm thử đạt ngay lần đầu |
| 4 | `qa_report.md` §15 | §15.3 "Chưa chạy" | §15.3 có bảng số đo E1–E5 và 3 ghi nhận không chặn Gate |
| 5 | `gate14_handoff.md` | Trạng thái "chưa đủ bằng chứng" | Trạng thái PASS, kèm số đo |

Không tắt tiến trình nào bằng tay, không đụng `main` hay tag. HostService được script dừng/khởi động bằng lệnh `SHUTDOWN` qua pipe như thiết kế.

---

## 2. Trạng thái Gate 14 — ✅ PASS (lần chạy 1, `ec01576`)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14-local-utilities
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14.ps1
```
Report: `03_Local_Data_Vault/handoff/task14_verification.json` (`allPassed=true`, `2026-11-08T10:26:12Z`; file bị Git bỏ qua).

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + `schema14` | Build 0/0; 0 file BOM; ≥ 3 001 dòng SQLite; `quick_notes.md` bị Git bỏ qua; Overwolf trong mặc định | ✅ 0/0; 0 BOM; 3 212 dòng; ignored; Overwolf có |
| E2 | `local-calc` | Đúng kết quả + Hex/Bin; `[CALC ERROR]` cho mọi lỗi; 30 lần P50 ≤ 1,5 / P95 ≤ 2,5 ms; token và dòng dispatch không đổi | ✅ `= 268 (Hex: 0x10C \| Bin: 0b100001100)`, `= 260 (Hex: 0x104 …)`, `10 / 0` → `[CALC ERROR] Chia cho 0.`, 14/14 lỗi đúng; **P50 1,148 / P95 1,372 ms**; token live/mock/session và 3 212 dòng không đổi |
| E3 | `local-timer` + HostService thật | Hết giờ trong 400 ms; Passive HUD `NOACTIVATE`; foreground không đổi; cancel all → rỗng; 0 timer khi rảnh, CPU < 1 % | ✅ hết giờ sau 269,3 ms; `NOACTIVATE\|TOOLWINDOW`; foreground `0x20400` trước = sau = sau render (HostService và script); hủy 1 → 0 đang chạy; 0 timer, CPU 0,904 % |
| E4 | `local-note` (mock) | Ghi không BOM, list/find đúng; khóa giả bị chặn khắp nơi; `/ground` trả nguồn `quick_notes.md` không khởi động lại; dọn ghi chú test | ✅ find 49152 → 1 kết quả; khóa giả không có trong file/latest/SQLite; nguồn thứ 3 = `zettelkasten/quick_notes.md#Note 1`; dọn xong |
| E5 | MCP + tài nguyên + hồi quy | Cockpit nhận MCP calc < 300 ms; MCP note có khóa bị chặn; Private WS < 60 MB; CPU < 1 %; hồi quy 11 → 03 PASS; `git status` sạch | ✅ **174,2 ms**; blocked; **55,2 MB**; **0 %**; task11…task03 `allPassed=True`; git sạch |

### Ghi nhận (không chặn Gate)
1. CPU rảnh của selftest `local-timer` là 0,904 %, sát ngưỡng 1 %. Đây là mẫu 1 s trong tiến trình selftest vừa khởi động; HostService chạy lâu đo 0 % (E5).
2. Private WS 55,2 MB, tăng so với Gate 11 (50,7 MB); còn dư 4,8 MB.
3. `nexus_config.json` trên máy chưa có `"Overwolf"`; mặc định trong code đã có.

---

## 3. Điểm mở cần Architect quyết định

Giữ nguyên các điểm của ca viết code (chi tiết ở `qa_report.md` §15.2), nay đã có số đo hỗ trợ:

1. **Bộ đếm**: mỗi bộ đếm một `Timer` chạy một lần (tối đa 20) thay vì một `Timer` duy nhất. E3 đo 0 đối tượng timer khi rảnh. Claude đề nghị giữ.
2. **Ô dành cho ghi chú trong Top-3 của `/ground`** (thay đổi nhỏ với ADR-45b): E4 cho thấy ghi chú lên đúng ô thứ ba, hai ô đầu vẫn là `current_task.md` và `qa_report.md`. Xin duyệt.
3. **Ngữ nghĩa `/calc`**: `^` lũy thừa; `log` = ln (có `ln`, `log10`); số âm in `-0x…`; `round` 0,5 ra xa 0; không nhân ngầm.
4. **Snapshot cục bộ** dùng `dispatchId = 0`, không tạo dòng SQLite (E2: 3 212 → 3 212 dòng).
5. **Bộ đếm qua MCP** chỉ sống cùng tiến trình McpTools; chuyển tiếp qua pipe để sau.
6. **Overwolf**: Nhật Hòa phải tự thêm vào `nexus_config.json`; khi chạy nền thì SLA ở LiveCombat (25 ms). Đúng ý Architect không?
7. **Ngân sách RAM**: Private WS 55,2 MB. Có muốn đặt mức cảnh báo (ví dụ 57 MB) trước khi thêm HUD Micro-Widgets ở TASK-13 không?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Duyệt Gate 14 và phát hành** *(khuyến nghị, làm ngay)* | Gemini duyệt; fast-forward `main` lên nhánh này; Nhật Hòa đẩy tag `v0.12.0` | Có đủ bằng chứng E1–E5, không có bản vá chưa kiểm |
| B. ADR-55 + ADR-56 *(khuyến nghị kế tiếp)* | `-FastRegression` / `-FullRegression` và GitHub Actions (build `-warnaserror` + 0 BOM) | Chuỗi hồi quy dài và có gọi Gemini thật; CI bắt lỗi biên dịch cho các ca Claude không có .NET SDK |
| C. TASK-13 | HUD Micro-Widgets, Right MFD, nhãn Private WS | Theo ADR-58; nên làm sau B và cần theo dõi RAM (đang 55,2 / 60 MB) |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 14 (TASK-14, ADR-59) đã ✅ ALL PASS** ngay lần chạy đầu. Claude Code chạy `Verify-Task14.ps1` có build trên máy Nhật Hòa, nhánh `claude/task-14-local-utilities` @`ec01576`; không phải sửa code. Chi tiết ở `gate14_handoff.md` và `qa_report.md` §15.3.
>
> Số đo:
> - E1: build `-warnaserror` 0 warning / 0 error, 0 BOM, 3 212 dòng SQLite, `quick_notes.md` bị Git bỏ qua.
> - E2: `/calc` đúng (`sqrt(144) + 2^8 = 268 (Hex: 0x10C | Bin: 0b100001100)`, `10 / 0` → `[CALC ERROR]`); 30 lần P50 1,148 ms / P95 1,372 ms; 0 token, 0 dòng dispatch mới.
> - E3: `/timer 250ms` hết giờ sau 269 ms; Cockpit hiện Passive HUD `NOACTIVATE`, foreground không đổi; cancel all → rỗng.
> - E4: `/note` + Secret Guard đúng; `/ground` trả ghi chú `49152` từ `quick_notes.md` không cần khởi động lại.
> - E5: MCP calc → Cockpit 174 ms; Private WS 55,2 MB; CPU rảnh 0 %; hồi quy Task11 → 03 ALL PASS; `git status` sạch.
>
> Ghi nhận: CPU rảnh của selftest timer 0,904 % (sát 1 %); RAM tăng từ 50,7 lên 55,2 MB.
>
> Cần bạn quyết:
> - (1) duyệt Gate 14 để fast-forward `main` và Nhật Hòa đẩy tag `v0.12.0`;
> - (2) mỗi bộ đếm một timer chạy một lần;
> - (3) ô thứ ba Top-3 `/ground` cho ghi chú (thay đổi nhỏ ADR-45b);
> - (4) ngữ nghĩa `/calc` (`^` = lũy thừa, `log` = ln, số âm `-0x…`);
> - (5) Overwolf và SLA LiveCombat;
> - (6) có đặt mức cảnh báo RAM trước TASK-13 không.
>
> Đề xuất tiếp: ADR-55/56 (hồi quy hai tầng + CI) trước TASK-13.
