# 📦 HỒ SƠ BÀN GIAO — Gate 14 (TASK-14, ADR-59, dự kiến v0.12.0)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-08, lần chạy 2 kết thúc 17:46. Các ca trước: viết code ở `ec01576` (chưa build); lần chạy 1 ALL PASS lúc 17:26; Quyết định (5) ở `024b63a` (17:35, chưa build).
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: **`claude/task-14-local-utilities`**, tách từ `main` @`ced587b`; lần chạy 2 trên @`024b63a`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-14)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §15 (§15.3 là lần chạy 1, §15.5 là lần chạy 2)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa ở `024b63a`, `-warnaserror`, 0 warning / 0 error.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Code ở đầu nhánh | `024b63a` (Quyết định (5): gỡ Overwolf khỏi `combatProcessPrefixes` mặc định) **chưa build** | Đã build 0/0 và kiểm thử đầy đủ |
| 2 | `Verify-Task14.ps1` lần 2 | Chưa chạy | 3 lượt, có build; lượt 2c **ALL CHECKS PASS (E1–E5)**, exit 0 |
| 3 | E1 sau Quyết định (5) | Điều kiện mới chưa được kiểm | **`overwolfInDefault=False`** ✅ (`overwolfInConfigFile=False`) |
| 4 | Sửa code | — | **Không cần sửa.** Hai lượt trượt (2a, 2b) do lý do ngoài code, xem §2 |
| 5 | `qa_report.md` §15 | §15.4 "cần lần chạy 2" | Thêm §15.5 (số đo + bảng ba lượt); cập nhật dòng trạng thái §15 |

Không tắt tiến trình nào bằng tay, không sửa `main` hay tag. HostService được script dừng/khởi động bằng lệnh `SHUTDOWN` qua pipe như thiết kế. Lệnh git duy nhất Claude chạy ngoài commit/push là checkout lại nhánh task-14 sau khi repo bị đổi sang `main` (lượt 2a).

---

## 2. Trạng thái Gate 14 — ✅ PASS (lần chạy 2, `024b63a`)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14-local-utilities
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14.ps1
```
Report: `03_Local_Data_Vault/handoff/task14_verification.json` (`allPassed=true`, `2026-11-08T10:45:51Z`; file bị Git bỏ qua).

| # | Bằng chứng | Tiêu chí PASS | Kết quả lần 2 (lượt 2c) |
|---|-----------|---------------|---------|
| E1 | Build + BOM + `schema14` | Build 0/0; 0 file BOM; ≥ 3 001 dòng SQLite; `quick_notes.md` bị Git bỏ qua; **Overwolf không có trong mặc định** (Quyết định (5)) | ✅ 0/0; 0 BOM; 3 850 dòng; ignored; **`overwolfInDefault=False`** |
| E2 | `local-calc` | Đúng kết quả + Hex/Bin; `[CALC ERROR]` cho mọi lỗi; 30 lần P50 ≤ 1,5 / P95 ≤ 2,5 ms; token và dòng dispatch không đổi | ✅ `= 268 (Hex: 0x10C \| Bin: 0b100001100)`, `= 260 (Hex: 0x104 …)`, `= 7`, `10 / 0` → `[CALC ERROR] Chia cho 0.`; **P50 1,092 / P95 1,301 ms**; token live/mock/session và 3 850 dòng không đổi |
| E3 | `local-timer` + HostService thật | Hết giờ trong 400 ms; Passive HUD `NOACTIVATE`; foreground không đổi; cancel all → rỗng; 0 timer khi rảnh, CPU < 1 % | ✅ hết giờ sau 268 ms; `NOACTIVATE\|TOOLWINDOW`, alertMs 12,94; foreground `0x20CDE` trước = sau = sau render; hủy 1 → 0; CPU 0,906 % |
| E4 | `local-note` (mock) | Ghi không BOM, list/find đúng; khóa giả bị chặn khắp nơi; `/ground` trả nguồn `quick_notes.md`, không cần khởi động lại; dọn ghi chú test | ✅ find 49152 → 1; khóa giả không có trong file/latest/SQLite; nguồn thứ 3 = `zettelkasten/quick_notes.md#Note 1`; dọn xong |
| E5 | MCP + tài nguyên + hồi quy | Cockpit nhận MCP calc < 300 ms; MCP note có khóa bị chặn; Private WS < 60 MB; CPU < 1 %; hồi quy 11 → 03 PASS; `git status` sạch | ✅ **158,4 ms**; blocked; **54,9 MB**; **0 %**; task11…task03 `allPassed=True`; git sạch |

### Hai lượt trượt trước lượt 2c (ngoài code)
| Lượt | Kết quả | Nguyên nhân |
|------|---------|-------------|
| 2a (17:35) | E1–E4 PASS, E5 FAIL | Có phiên khác **checkout repo sang `main` lúc 17:35:57** trong lúc Verify chạy (reflog; Claude và các script không chạy lệnh này). `main` chưa ignore `quick_notes.md` → Verify-Task06 thấy git không sạch → hồi quy 06…11 trượt dây chuyền |
| 2b (17:39) | E1–E4 PASS, E5 FAIL | Verify-Task09 E5: `/f` gọi Gemini thật **timeout 25 s** (`/ask` 2,1 s và `/qa` 2,3 s bình thường) → 09…11 trượt dây chuyền |

### Ghi nhận (không chặn Gate)
1. **Nhiều phiên dùng chung một working copy** có thể làm hỏng một lượt Verify (lượt 2a). Xem điểm mở 1.
2. CPU rảnh của selftest `local-timer` 0,906 %, vẫn sát ngưỡng 1 % (lần 1: 0,904 %).
3. Private WS 54,9 MB, còn dư 5,1 MB.
4. Mỗi lượt Verify-Task14 đầy đủ tốn khoảng 28–29k token Gemini thật (do hồi quy). Hôm nay đã dùng khoảng 180k/250k.

---

## 3. Điểm mở cần Architect quyết định

1. **Cách ly working copy** *(mới)*: đề nghị mỗi agent (Antigravity, Claude Code, phiên khác) dùng `git worktree` riêng, hoặc thống nhất chỉ một phiên chạy Verify/checkout mỗi lần. Có thể thêm vào Verify một bước kiểm tra nhánh: ghi lại `HEAD` lúc bắt đầu, lúc kết thúc HEAD khác thì báo rõ "nhánh bị đổi" thay vì để Task06 trượt dây chuyền.
2. **Bộ đếm**: mỗi bộ đếm một `Timer` chạy một lần (tối đa 20). E3 đo 0 đối tượng timer khi rảnh. Claude đề nghị giữ.
3. **Ô dành cho ghi chú trong Top-3 của `/ground`** (thay đổi nhỏ với ADR-45b): ghi chú lên đúng ô thứ ba ở cả hai lần chạy. Xin duyệt.
4. **Ngữ nghĩa `/calc`**: `^` lũy thừa; `log` = ln (có `ln`, `log10`); số âm in `-0x…`; `round` 0,5 ra xa 0; không nhân ngầm.
5. **Snapshot cục bộ** dùng `dispatchId = 0`, không tạo dòng SQLite (E2: 3 850 → 3 850 dòng).
6. **Bộ đếm qua MCP** chỉ sống cùng tiến trình McpTools; chuyển tiếp qua pipe để sau.
7. **Overwolf** — ✅ đã quyết (5) và **đã xác nhận ở lần chạy 2**: `overwolfInDefault=False`; code và `nexus_config.json` trên máy cùng danh sách `DeltaForce, TslGame, SGuard`.
8. **Ngân sách RAM**: Private WS 54,9 MB. Có muốn đặt mức cảnh báo (ví dụ 57 MB) trước khi thêm HUD Micro-Widgets ở TASK-13 không?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Duyệt Gate 14 và phát hành** *(khuyến nghị, làm ngay)* | Gemini duyệt; fast-forward `main` lên `024b63a` + commit tài liệu này; Nhật Hòa đẩy tag `v0.12.0` | Đủ bằng chứng E1–E5 ở commit cuối, không có bản vá chưa kiểm |
| B. ADR-55 + ADR-56 *(khuyến nghị kế tiếp)* | `-FastRegression` / `-FullRegression`; GitHub Actions (build `-warnaserror` + 0 BOM); kiểm HEAD không đổi trong lúc Verify | Lượt 2b cho thấy hồi quy dài có gọi Gemini thật dễ trượt vì mạng và tốn khoảng 29k token mỗi lượt; lượt 2a cho thấy cần chốt nhánh |
| C. TASK-13 | HUD Micro-Widgets, Right MFD, nhãn Private WS | Theo ADR-58; nên làm sau B và theo dõi RAM (54,9 / 60 MB) |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 14 lần chạy 2 (sau Quyết định (5)) đã ✅ ALL CHECKS PASS (E1–E5)**. Claude Code chạy `Verify-Task14.ps1` có build trên máy Nhật Hòa, nhánh `claude/task-14-local-utilities` @`024b63a`, kết thúc 17:46; không phải sửa code. Chi tiết ở `gate14_handoff.md` và `qa_report.md` §15.5.
>
> Số đo:
> - E1: build `-warnaserror` 0/0, 0 BOM, 3 850 dòng SQLite, `quick_notes.md` bị Git bỏ qua, **`overwolfInDefault=False`**.
> - E2: `/calc` đúng (`= 268 (Hex: 0x10C | Bin: 0b100001100)`, `10 / 0` → `[CALC ERROR]`); 30 lần P50 1,092 / P95 1,301 ms; 0 token, 0 dòng dispatch mới.
> - E3: `/timer 250ms` hết giờ sau 268 ms; Passive HUD `NOACTIVATE`, foreground không đổi; cancel all → rỗng.
> - E4: `/note` + Secret Guard đúng; `/ground` trả ghi chú 49152 từ `quick_notes.md`.
> - E5: MCP calc → Cockpit 158 ms; Private WS 54,9 MB; CPU rảnh 0 %; hồi quy Task11 → 03 ALL PASS; `git status` sạch.
>
> Hai lượt trước lượt PASS trượt vì lý do ngoài code:
> - 2a: có phiên khác checkout repo sang `main` giữa lúc chạy, nên Task06 thấy git không sạch;
> - 2b: `/f` gọi Gemini thật timeout 25 s một lần.
>
> Cần bạn quyết:
> - (1) duyệt Gate 14 để fast-forward `main` và Nhật Hòa đẩy tag `v0.12.0`;
> - (2) cách ly working copy giữa các agent (`git worktree` riêng hoặc một phiên Verify mỗi lần, thêm kiểm HEAD trong Verify);
> - (3) các điểm còn treo: mỗi bộ đếm một timer, ô ghi chú trong Top-3 `/ground`, ngữ nghĩa `/calc`, mức cảnh báo RAM trước TASK-13.
>
> Đề xuất tiếp: ADR-55/56 (hồi quy hai tầng + CI + chốt nhánh) trước TASK-13.
