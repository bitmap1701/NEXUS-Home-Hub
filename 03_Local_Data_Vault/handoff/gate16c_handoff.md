# 📦 HỒ SƠ BÀN GIAO — Gate 16c (TASK-16c: Cockpit UI v2 phần nhìn theo 6 bảng thiết kế, ADR-68 → `v0.16.5`)

- **Ca thực thi**: Claude, 2026-11-11
- **Gửi tới**: Claude Code trên máy Nhật Hòa (chạy Verify), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-16c-cockpit-ui-v2-visual`**, tách từ `main` @`60322e2` (`v0.16.0`). Đặc tả: `current_task.md` (`1763b81`)
- **Chi tiết kỹ thuật**: `qa_report.md` §24
- **Trạng thái build**: Claude **chưa build** trên máy mình. CI (Windows runner) build `-warnaserror` xanh và selftest `ui-v2-visual` PASS. **Chưa có số đo trên máy Nhật Hòa.**

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Bố cục | Terminal 3 cột `LEFT MFD \| CENTER \| RIGHT MFD` | 2 cột: hội thoại `*` + cột phải 300 px (`THẺ ĐÃ GHIM n/5`, Hẹn giờ, `LIÊN MINH`) |
| 2 | Thanh đỉnh | Dòng chữ trạng thái | 44 px: chấm + `NEXUS COCKPIT` + `COMMAND`/`PASSIVE`, chip RAM / CPU / WAL, `Gọn · Chuẩn · Cao`, `Xuyên thấu`, `F1` |
| 3 | Mức 40 px | Phần trên của giao diện cũ | Một dòng `● NEXUS · RAM … · trạng thái`. Vẫn cắt thân như Gate 16b, `Window.Height` không đổi |
| 4 | Hỏi–đáp | Danh sách lịch sử ở LEFT MFD + khung trả lời | Chuỗi thẻ ≤ 20 mục (Card Pool 20 view), dòng `/calc` có nhãn `cục bộ · 0 token`, thẻ mở có nút Copy / Ghim / Hỏi tiếp / Chạy lại + chip 1–4 |
| 5 | Đang chạy | Không có tiến trình, không hủy được | Viền xanh, đồng hồ giây (timer chỉ tồn tại khi có yêu cầu bay), 4 bước, **`Hủy Esc`** = `CancellationTokenSource` → `[ĐÃ HỦY]`, bỏ phản hồi đến muộn, 0 token |
| 6 | Ô lệnh | Ô chữ trơn | `NEXUS›`, bảng `/` (VỪA DÙNG + TẤT CẢ, 18 lệnh, lọc, Tab) và bảng `@` (lọc registry, Tab → `@<agentId> `). `@<agentId> việc` giao đúng agent |
| 7 | Chế độ lười | Chỉ có phím | Thẻ VIỆC TIẾP THEO (Alt+Enter) + sơ đồ 4 bước, thẻ ảnh clipboard (Enter / 2 / 3, kiểm một lần mỗi lần đánh thức) |
| 8 | `Alt+A` | Khối chữ | Bảng: Gate + huy hiệu, E1–E5, VIỆC KẾ TIẾP, `Alt+G` lời nhắn Gemini, `Alt+K` prompt Claude Code, ĐÃ ĐÓNG (sắp theo số), THÀNH VIÊN, BẢNG TIN |
| 9 | 6 công tắc nhanh | Cột phải | Đáy bảng F1 (giữ tên, handler, trường `UISTATE`) |
| 10 | `Ctrl+Shift+Space` | Tự tick ô Mic | Không bao giờ đụng tới micro |
| 11 | Phông chữ | Consolas | Segoe UI Variable + Cascadia Mono có sẵn trong Windows, 0 byte nhúng |
| 12 | Kiểm thử | — | Selftest `ui-v2-visual` (CI) + `Verify-Task16c.ps1` (E1–E5); ADR-68 vào sổ (số trống kế tiếp ADR-69), ROADMAP thêm TASK-16c |

---

## 2. Trạng thái Gate 16c — ⏳ chưa đủ bằng chứng (chờ chạy Verify trên máy Nhật Hòa)

| # | Bằng chứng | Hiện có | Còn thiếu |
|---|------------|---------|-----------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, ADR-68 trong sổ, không nhúng font, không animation, selftest `ui-v2-visual` (+ `@<agentId>` với registry thật) | CI `653f8c8` xanh; selftest PASS trên runner (Core 4/4, Hủy 4/4, cửa sổ 19/19) | Chạy trên máy Nhật Hòa |
| E2 | Hủy trên HostService thật (thẻ chạy → `CANCEL` → vẫn `[ĐÃ HỦY]` sau 3,7 s); thanh 44 px, 2 cột / 300 px, dòng 40 px, pool ≤ 20, dòng `cục bộ · 0 token` | Runner: Hủy xong 10,8 ms sau `Cancel()`, 0 token | Đo trên máy thật |
| E3 | Bảng `/` (17 lệnh, VỪA DÙNG + TẤT CẢ, `/ca` → `/calc`, Tab), `@sec` trên registry thật, VIỆC TIẾP THEO, thẻ ảnh → `/img` (mock), F1, `Alt+A` + `Alt+G` / `Alt+K` | Runner: bảng `/` mở trong 4,0 ms | Chạy với registry agent trên máy |
| E4 | ColdFirstWake (chỉ báo cáo), Warmup ≤ 60 ms, N = 10 P50 ≤ 10 / P95 ≤ 16 ms | — | **Chưa đo** |
| E5 | RAM rảnh ≤ 45 MB, CPU 0 %, không còn timer, chuỗi `Verify-Task15` (→ 16b → 16 → 13 → 12 → 13a → 14…03, `-SkipLive`) PASS, `zeroLiveCalls`, git, CI | — | **Chưa đo** |

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-16c-cockpit-ui-v2-visual
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task16c.ps1
```
Kết quả ghi vào `03_Local_Data_Vault/handoff/task16c_verification.json`. Bảng `Alt+A` đọc E1–E5 từ file này. Lệnh chạy tự build, chuỗi hồi quy đầy đủ mất khoảng 12–15 phút. Khi đang sửa lỗi, có thể thêm `-FastE5`, nhưng cần một lần chạy không có `-FastE5` để đóng Gate.

### Sau khi Architect đóng dấu (Nhật Hòa làm, Claude không push được `main` hay tag)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-16c-cockpit-ui-v2-visual
git push origin main
git tag -a v0.16.5 -m "Gate 16c: TASK-16c Cockpit UI v2 visual layer (ADR-68)"
git push origin v0.16.5
```

---

## 3. Điểm mở cần Architect quyết định

1. **VỪA DÙNG** của bảng `/` chỉ giữ trong bộ nhớ: mất khi khởi động lại HostService, 0 lần ghi đĩa. Có cần lưu vào `cockpit_ui.json` không? Claude khuyến nghị **không**.
2. **Control cũ để ẩn**: 54 tên control cũ vẫn còn (ẩn) để `UISTATE` và chuỗi `Verify-Task04..16b` không gãy. Có nên mở một task dọn dẹp sau khi chuyển các script kiểm thử sang trường `visual` mới không?
3. **Ngưỡng registry ở E3**: script yêu cầu ít nhất 100 agent và ghi lại số thật, vì đặc tả nói 283 nhưng con số trên máy có thể khác. Chấp nhận, hay đòi đúng 283?
4. **TASK-17 sau Gate 16c**: không còn cột LEFT MFD. Các trang MFD (Alt+Q) nay hiện ở dải thông tin trên đầu cột hội thoại. Trang `NET` của TASK-17 đặt ở dải này, hay thành một thẻ ở cột phải?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Claude Code chạy `Verify-Task16c.ps1` ngay** *(khuyến nghị)* | Lệnh ở mục 2. Nếu trượt, gửi `task16c_verification.json` (hoặc ảnh màn hình E1–E5) cho Claude | E4 / E5 chỉ đo được trên máy Nhật Hòa |
| B. Sau khi PASS và có tag `v0.16.5` | Làm tiếp TASK-17 (ADR-67) trên nhánh `claude/task-17-net-radar-voice-ptt` (đặc tả `5e07d39`), sau khi rebase lên `v0.16.5` | Trang NET và nút mic gắn vào giao diện mới, không phải làm hai lần |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **TASK-16c (ADR-68 → `v0.16.5`) đã viết xong** trên nhánh `claude/task-16c-cockpit-ui-v2-visual`. CI build `-warnaserror` xanh, selftest `ui-v2-visual` PASS trên Windows runner. **Chưa có số đo trên máy Nhật Hòa**: E4 (WAKE) và E5 (RAM, CPU, hồi quy) đang chờ Claude Code chạy `Verify-Task16c.ps1`.
>
> Đã làm theo 4 quyết định của bạn:
> 1. Phông Segoe UI Variable + Cascadia Mono, không nhúng file nào.
> 2. 6 công tắc nhanh xuống đáy bảng F1, thanh đỉnh có nút Xuyên thấu, cột phải 300 px chỉ còn Thẻ ghim n/5, Hẹn giờ, Liên minh. Trường `UISTATE` và lệnh pipe cũ giữ nguyên.
> 3. `Hủy Esc`: `CancellationTokenSource` cho ask / ground / refine / ctx. Thẻ chuyển `[ĐÃ HỦY]`, phản hồi đến muộn bị bỏ qua, 0 token. Trên runner, yêu cầu kết thúc 10,8 ms sau khi hủy (mock chờ 3 s).
> 4. `Ctrl+Shift+Space` không còn tick ô Mic.
>
> Phần nhìn theo 6 bảng thiết kế:
> - 2 cột, thanh đỉnh 44 px với chip, dòng đơn 40 px (vẫn cắt thân như Gate 16b);
> - chuỗi thẻ ≤ 20 dùng Card Pool, dòng `cục bộ · 0 token`;
> - thẻ đang chạy có tiến trình, đồng hồ giây (timer chỉ tồn tại khi có yêu cầu bay) và `Hủy Esc`;
> - bảng `/` (VỪA DÙNG + TẤT CẢ, lọc, Tab) và `@agent` (lọc registry, `@<agentId> việc` giao đúng agent);
> - thẻ VIỆC TIẾP THEO, thẻ ảnh clipboard;
> - bảng `Alt+A` với E1–E5 + `Alt+G` / `Alt+K`;
> - không Storyboard hay animation, mọi brush đều Freeze.
>
> `Verify-Task16c.ps1`:
> - E1: build, BOM, ADR-68, không font nhúng, không animation, selftest.
> - E2: Hủy trên HostService thật + bố cục.
> - E3: `/`, `@sec` trên registry thật, lười, ảnh, F1, Alt+A / G / K.
> - E4: ColdFirstWake chỉ báo cáo, Warmup ≤ 60, N = 10 P50 ≤ 10 / P95 ≤ 16 ms.
> - E5: RAM ≤ 45 MB, CPU 0 %, chuỗi Verify-Task15 → 16b → … → 03 `-SkipLive`, zeroLiveCalls.
>
> Hai chỗ lệch nhỏ so với đặc tả:
> - Chuỗi hồi quy chạy theo thứ tự `15 → 16b → 16 → …` (cùng bộ script).
> - Hủy được kiểm qua pipe `CANCEL`, cùng hàm với phím Esc.
>
> Mời bạn quyết 4 điểm mở trong `gate16c_handoff.md` §3:
> 1. VỪA DÙNG chỉ giữ trong bộ nhớ.
> 2. Task dọn các control cũ đang ẩn.
> 3. Ngưỡng registry ≥ 100 thay vì đúng 283.
> 4. Trang NET của TASK-17 đặt ở dải MFD hay ở cột phải.
>
> Sau khi Verify ALL PASS, mời bạn đóng dấu `v0.16.5` rồi cho làm tiếp TASK-17.
