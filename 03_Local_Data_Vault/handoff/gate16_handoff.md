# 📦 HỒ SƠ BÀN GIAO — Gate 16 (TASK-16: Cockpit UI v2 Chặng 1 + YouTube G272 / EQ K450 + webhook đồng hồ)

- **Ca thực thi**: Claude (ca viết code), 2026-11-09 → 2026-11-10; Claude Code trên máy Nhật Hòa (ca chạy Verify), 2026-11-10
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-16-cockpit-ui-v2`**, tách từ `a67ba95` (đặc tả TASK-16 của Gemini, trên `main` @`1dba5b2` / `v0.14.0`)
- **Đặc tả**: `current_task.md` (TASK-16). Chi tiết kỹ thuật: **`qa_report.md` §20**
- **Trạng thái build**: **đã build và chạy Verify trên máy Nhật Hòa** (`c1d3a3d`): build `-warnaserror` 0/0, Gate 16 E1–E5 **ALL PASS** ở lần chạy 2, Verify-Task13 ALL PASS. CI GitHub Actions xanh trên `a990c8a` và `353e505`.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| B1 | Bản ghi test | Lọc ở lớp dữ liệu (`is_test`, TASK-12c); snapshot test vẫn có thể hiện ở ACTIVE SQUAD | Cờ `isTest` trong snapshot; Cockpit thật bỏ qua snapshot test và lọc RECENT DISPATCHES theo cùng quy tắc |
| B2 | RAM trên HUD | Badge đọc một lần, LEFT MFD đọc lần khác, có thêm dòng "Working Set" | **Một** lần đọc Private WS cho cả hai; bỏ "Working Set" |
| B3 | Cột giữa | Câu trả lời tối đa 150 px, phía dưới là log cuộn | Câu trả lời chiếm phần còn lại; log thành **1 dòng trạng thái** (rê chuột: 10 dòng gần nhất) |
| B4 | Chữ bị cắt | Một số nhãn bị cắt mất | Dấu … + tooltip đủ chữ; RIGHT MFD cuộn được; **F1** = bảng phím tắt (chỉ khi Cockpit có focus) |
| B5 | Nhãn thẻ | `[2 Code Blocks \| …]` | **"Câu trả lời hiện tại: X thẻ"** (chuỗi cũ thành tooltip) |
| B6 | Vị trí | Giữa G272, cách đỉnh 48 px, rộng cố định 1080 | Luôn trong vùng làm việc G272; rộng ≤ vùng − 32 px; vị trí đã lưu chỉ dùng khi vừa trọn |
| U1 | Độ cao | Cố định 440 px | **40 / 330 / 640 px**: kéo viền dưới (thả là bám), nhấp đúp tiêu đề/viền: Thu gọn ↔ Nửa, kéo tiêu đề để di chuyển, lưu `cockpit_ui.json`; **tự thu gọn** 30 s ở PASSIVE (bật/tắt); **xuyên thấu** 60 % (bật/tắt), Ctrl+Shift+Space rõ lại + gõ lệnh |
| U2 | YouTube → EQ K450 | — | WinEvent hook (không timer) → `Include: nexus_k450_youtube.txt` / `…_music.txt`, HUD thu gọn khi PASSIVE |
| U3 | Đồng hồ | — | `NEXUS_WATCH_WEBHOOK_URL`: POST chạy nền, timeout 3 s khi `/timer` hết giờ, khi `[ESCALATED - 3 STRIKES!]`, và `/watch test` |
| Kiểm thử | — | — | selftest `ui-v2` (25 kiểm, có trong CI), `Verify-Task16.ps1` E1–E5, pipe `UISTATE` / `UI` / `YOUTUBE`, lệnh `/ui`, `/watch` |
| V | Verify trên máy (ca 2) | Lần 1: `Write-Latest` ghi `dispatchId = 0` nên Cockpit bỏ qua mọi snapshot của script (E2 B1 và E3 mở lại trượt) | `c1d3a3d`: `dispatchId` giả dương. Lần 2: Gate 16 E1–E5 ALL PASS, Verify-Task13 ALL PASS. Không sửa code sản phẩm |

Không đụng `main` hay tag. Không có thao tác Cấp Đỏ: không ghi Program Files, không xin quyền admin, không xóa file, không tắt tiến trình. TASK-15 (`/ctx`, `/net`, PTT FIFINE K669) và Phase 4 **không** làm.

---

## 2. Trạng thái Gate 16 — ✅ E1–E5 ALL PASS ở lần chạy 2 (máy Nhật Hòa, `c1d3a3d`)

| # | Bằng chứng | Trạng thái |
|---|------------|-----------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ui-v2` | ✅ build 0 cảnh báo / 0 lỗi, Zero-BOM 113 file 0 vi phạm, `ui-v2` pass |
| E2 | B1–B6 trên HostService thật | ✅ B1 snapshot test bị ẩn (`0 -> 1`), RECENT 0 dòng test · B2 badge = MFD `Private WS: 42.7 MB / 60 MB` · B3 · B4 F1 bật/tắt · B5 `Câu trả lời hiện tại: 2 thẻ` · B6 trong vùng G272 |
| E3 | 40/330/640, toggle, lưu + khởi động lại, tự thu gọn, xuyên thấu (`WindowFromPoint`), Ctrl+Shift+Space | ✅ khôi phục `full` đúng −1880,60; tự thu gọn sau 2,8 s, câu trả lời mới mở lại `half`; xuyên thấu: tâm HUD trả về tiến trình khác; từ 40 px + Ctrl+Shift+Space → `half`, ô nhập có focus |
| E4 | EQ giả trong `%TEMP%` + webhook không chặn | ✅ `youtube` / `music` một lần ghi mỗi lần đổi, 5 hook; `/watch test` 0,986 ms, `UISTATE` lúc chờ 9,8 ms, POST hết ở 3 015,6 ms; `/timer` → `timer_expired` |
| E5 | Private WS lắng ≤ 45,0 MB (trần 52), CPU rảnh < 0,5 % (mục tiêu 0 %), `-FastRegression`, git | ✅ **35,5 MB**, CPU rảnh **0 %**, `-FastRegression` 55 s (1 SLA-WARN `/ask` P50 2,898 ms, chỉ cảnh báo), git không đổi |
| Hồi quy | `Verify-Task13.ps1 -SkipBuild` | ✅ ALL PASS: COPYWIDGET P95 0,496 ms, ghim MCP → Cockpit 133,2 ms, Private WS 36 MB, CPU 0,31 % |

**Lần chạy 1 (`353e505`): 3/5.** E2 `b1SnapshotHidden` và E3 `autoCollapse` (mở lại khi có câu trả lời mới) trượt. Nguyên nhân là **script Verify**, không phải sản phẩm: `Write-Latest` ghi `dispatchId = 0`, và `LatestDispatchFile.TryRead` (`DevQaLoop.cs:230`) bỏ snapshot không local có `dispatchId <= 0`. Vì vậy thẻ không đổi và `hiddenTestSnapshots` vẫn 0 sau mọi lần ghi. Sửa ở `c1d3a3d` (`dispatchId` giả dương). Verify-Task13 lần 1 trượt E5 chỉ vì Claude sửa script trong lúc nó chạy (git guard thấy cây bẩn). Chi tiết: `qa_report.md` §20.6.

**Chưa đo**: độ trễ WAKE khi Ctrl+Shift+Space từ 40 px. Verify không xuất số này.

### Lệnh chạy (Claude Code trên máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-16-cockpit-ui-v2
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task16.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1 -SkipBuild
```
- Verify-Task13 chạy thêm vì TASK-16 sửa RIGHT MFD (nhãn thẻ, ô ghim nằm trong vùng cuộn).
- Trong lúc chạy: không chạy lệnh git, không bấm vào HUD, để yên chuột (E3 kiểm `WindowFromPoint` ở tâm HUD).
- Verify **không** ghi `C:\Program Files\EqualizerAPO`. Nó dùng `%TEMP%\nexus_verify16_eq` và một cổng loopback không trả lời, rồi khôi phục `latest_dispatch.json`, `cockpit_ui.json` và khởi động lại HostService bình thường.
- Không gọi Gemini, 0 token.

---

## 3. Điểm mở cần Architect quyết định

1. **Trùng ADR-62.** `docs/ADR_REGISTRY.md` và `ROADMAP_FUTURE.md` đang giữ ADR-62 cho TASK-15 (`/ctx`). Đề xuất: TASK-16 nhận **ADR-65** (số trống kế tiếp), giữ ADR-62 cho `/ctx`. Claude sẽ sửa chú thích code và registry theo quyết định.
2. **U2 không dùng "chu kỳ sẵn có"**, vì Cockpit không có chu kỳ nào. Thay vào đó là 5 WinEvent hook out-of-context + một bộ gom 500 ms: màn hình đứng yên thì không chạy gì, không thêm luồng, không chèn DLL. Duyệt cách này?
3. **Quyền Program Files (Cấp Đỏ).** NEXUS không bao giờ xin quyền admin. Để U2 chạy thật, Nhật Hòa tự làm một lần:
   - cấp quyền Modify cho `C:\Program Files\EqualizerAPO\config`;
   - tạo 2 file `nexus_k450_youtube.txt` và `nexus_k450_music.txt`.

   Nếu chưa làm, U2 tự tắt và dòng trạng thái ghi lý do.
4. **Xuyên thấu** là tùy chọn lưu được: Ctrl+Shift+Space chỉ tắt trong lúc gõ lệnh, về PASSIVE thì bật lại. Có muốn Ctrl+Shift+Space tắt hẳn không?
5. **Tự thu gọn là tạm thời**: không ghi đè mức đã lưu, câu trả lời mới mở lại. Từ 40 px, Ctrl+Shift+Space mở Nửa trước khi đặt focus, nên WAKE lần đó có thêm một lần đổi độ cao (đo ở máy).
6. **Webhook với ntfy.** Định dạng `{"event","title","message"}` gửi tới `https://ntfy.sh/<topic>` sẽ hiện nguyên chuỗi JSON trên đồng hồ. Thêm trường `topic` để dùng kiểu JSON của ntfy không? Cần chọn dịch vụ đẩy **miễn phí** (Nhật Hòa không muốn phát sinh phí).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Duyệt Gate 16 + chốt ADR / webhook** *(khuyến nghị)* | Gemini duyệt E1–E5, quyết điểm mở 1–6; Claude sửa số ADR và định dạng webhook theo quyết định, rồi merge / tag | Gate đã đủ bằng chứng; các điểm mở đều nhỏ |
| B. Đo WAKE từ 40 px | Thêm số WAKE vào Verify-Task16 E3 | Điểm mở 5 nói cần đo; Verify hiện chưa xuất số này |
| C. TASK-15 (`/ctx`, `/net`, PTT) | Theo ADR-58 | Sau khi Gate 16 được duyệt |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 16 (TASK-16) E1–E5 ALL PASS trên máy Nhật Hòa** ở lần chạy 2, nhánh `claude/task-16-cockpit-ui-v2` @`c1d3a3d`. Verify-Task13 `-SkipBuild` cũng ALL PASS. Chi tiết: `qa_report.md` §20.6, `gate16_handoff.md` mục 2.
>
> Số đo trên máy:
> - E1 build `-warnaserror` 0/0, Zero-BOM 113 file, selftest `ui-v2` pass.
> - E2 B1–B6 PASS trên HostService thật (snapshot test bị ẩn, badge = MFD `Private WS: 42.7 MB`, "Câu trả lời hiện tại: 2 thẻ", HUD trong vùng G272).
> - E3 40/330/640, khôi phục vị trí sau khởi động lại, tự thu gọn 2,8 s và mở lại khi có câu trả lời, xuyên thấu (`WindowFromPoint` ra tiến trình khác), Ctrl+Shift+Space từ 40 px → Nửa + focus.
> - E4 EQ giả trong `%TEMP%` đổi `youtube`/`music` đúng một lần mỗi lần đổi, 5 hook; `/watch test` 0,986 ms, POST hết ở 3 015,6 ms.
> - E5 Private WS lắng **35,5 MB** (≤ 45), CPU rảnh **0 %**, `-FastRegression` 55 s, git không đổi.
> - Chưa đo WAKE từ 40 px (Verify không xuất số này).
>
> Lần chạy 1 trượt E2 B1 và E3 "mở lại khi có câu trả lời mới" do **lỗi script Verify**: `Write-Latest` ghi `dispatchId = 0`, mà `LatestDispatchFile.TryRead` bỏ snapshot không local có id ≤ 0, nên HUD không thấy bất kỳ lần ghi nào. Sửa trong script ở `c1d3a3d`, không đụng code sản phẩm.
>
> Phần triển khai (không đổi so với bản trước):
>
> Đã làm:
> - **B1** cờ `isTest` + bộ lọc hiển thị.
> - **B2** một lần đọc Private WS cho badge và LEFT MFD, bỏ "Working Set".
> - **B3** câu trả lời chiếm cột giữa, log thành 1 dòng trạng thái.
> - **B4** tooltip đủ chữ, F1 (chỉ khi có focus).
> - **B5** "Câu trả lời hiện tại: X thẻ".
> - **B6** luôn trong vùng làm việc G272.
> - **U1** 40/330/640 px, kéo thả bám không animation, nhấp đúp, lưu `cockpit_ui.json`, tự thu gọn 30 s (timer một phát), xuyên thấu 60 % + `WS_EX_TRANSPARENT|LAYERED`, Ctrl+Shift+Space rõ lại và focus ô lệnh.
> - **U2** YouTube trên G272 → `Include: nexus_k450_youtube.txt` / `…_music.txt` (chỉ sửa dòng đó, giữ BOM/CRLF, sao lưu 1 lần).
> - **U3** webhook chạy nền, timeout 3 s. Runner: `Notify` trả về 3,8 ms, POST hết ở 3 001 ms.
>
> Cần bạn quyết:
> - (1) **trùng ADR-62** với TASK-15 `/ctx` trong registry — đề xuất TASK-16 = ADR-65;
> - (2) U2 dùng 5 WinEvent hook + gom 500 ms thay "chu kỳ sẵn có" (Cockpit không có chu kỳ nào; vậy CPU rảnh vẫn 0, không timer);
> - (3) ghi `C:\Program Files\EqualizerAPO\config` là Cấp Đỏ: NEXUS không xin admin, Nhật Hòa tự cấp quyền Modify và tạo 2 file profile nếu muốn dùng;
> - (4) xuyên thấu là tùy chọn lưu được (Ctrl+Shift+Space chỉ tắt khi gõ lệnh);
> - (5) webhook với ntfy có cần thêm trường `topic` không.
>
> Bước tiếp đề xuất: bạn duyệt Gate 16 và quyết 5 điểm trên; Claude sửa số ADR / webhook theo quyết định rồi merge.
