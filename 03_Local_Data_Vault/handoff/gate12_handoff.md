# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12 + TASK-12b: Multimodal Vision `/img` & `/snap`, ép bộ nhớ đường ảnh, ADR-61)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-09, 02:49–03:00 (nghiệm thu lại sau TASK-12b), rồi **Claude (ca viết code), 03:02–03:20**: phân tích kết quả, đề xuất hướng TASK-12c và Cockpit UI v2 (`qa_report.md` §19.7, `ui_v2_proposal.md`). Ca viết code TASK-12b làm tới `31548f8`; lượt 1–3 của Gate 12 cũ ở §19.4.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude (ca viết code)
- **Nhánh**: **`claude/task-12-multimodal-vision`**, tách từ `main` @`149756e`. Đầu nhánh code là **`31548f8`**; ca này chỉ thêm commit tài liệu.
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-12b)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §19.5 (triển khai) và **§19.6** (kết quả ca này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. **CI GitHub xanh trên `31548f8`.**

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | `Verify-Task12.ps1` | Chưa chạy với code TASK-12b | 2 lượt (lượt 2 chạy lại, không sửa gì): **E1–E4 PASS; E5 FAIL** ở `privateWsUnder45` và `selftestVisionMem`, lặp lại gần như từng số |
| 2 | `Verify-Task13a.ps1 -SkipBuild` | — | ✅ **ALL PASS**: rảnh 35,1 MB; HUD lệch 0,1 MB |
| 3 | `Verify-Task13.ps1 -SkipBuild` | — | ✅ **ALL PASS**: 5 ghim, rảnh 35,9 MB |
| 4 | Chẩn đoán | — | Report `vision-mem`: phần vượt mục tiêu là **bộ nhớ native** (managed sau nén chỉ 1,2 MB, GC committed 1,45 MB) |
| 5 | Code / script | — | **Không sửa gì**: không có lỗi script; phần trượt là số đo của sản phẩm, nằm ngoài quyền "sửa tối thiểu script" |
| 6 | Tài liệu | §19.5 "chưa chạy trên máy" | Thêm §19.6; viết lại file này |
| 7 | *(ca Claude 03:02–03:20)* Hướng TASK-12c | Claude Code khuyến nghị WIC COM | Ca viết code khuyến nghị **tiến trình con** (§19.7); đính chính kết luận "phẳng" của §19.5 |
| 8 | *(ca Claude)* Cockpit UI v2 | Không có | `ui_v2_proposal.md`: 6 lỗi B1–B6 từ ảnh Cockpit thật, U1–U5, mockup 6 khung |

Không tắt tiến trình nào bằng tay, không đụng `main` hay tag. Trong lúc Verify chạy, Claude không chạy lệnh git nào. Gemini chỉ chạy mock, 0 token. `mfd_pins.json` của Nhật Hòa và chữ trong clipboard đã được khôi phục.

---

## 2. Trạng thái Gate 12 — ❌ CHƯA ALL PASS (E5: mục tiêu bộ nhớ của TASK-12b chưa đạt; cổng cứng 52 MB vẫn đạt)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-12-multimodal-vision
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task12.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13a.ps1 -SkipBuild
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1 -SkipBuild
```
Không chạy lệnh git nào khi Verify đang chạy; không copy/paste; không mở cửa sổ ngân hàng hay trình quản lý mật khẩu trên G272.

### Bảng so sánh bộ nhớ
| Chỉ số | Gate 12 cũ | Sau TASK-12b (lượt 1 / 2) | Mục tiêu | Kết quả |
|--------|-----------|---------------------------|----------|---------|
| **Đỉnh `/img` clipboard** | **~145 MB** | **65,6 / 65,6 MB** (nền 64,8; +0,8) | mềm 120 / 200, không chặn | ✅ giảm khoảng 79 MB |
| Đỉnh `/img --file` 2K | ~60 MB | 73,6 / 73,5 MB (+8,0) | như trên | ✅ |
| **Trung vị rảnh sau phiên vision** | 51,6 MB | **50,8 / 51,7 MB** | **≤ 45,0** (cổng cứng 52,0) | ❌ (cổng cứng ✅) |
| `vision-mem`: tăng sau phiên | — | +5,8 / +5,8 MB | ≤ 4,3 | ❌ |
| `vision-mem`: 3 lần cuối chênh | — | 4,9 / 4,9 MB (bậc ở ảnh thứ 6: 10,9 → 15,7) | ≤ 1,0 | ❌ |
| Không có vision (Task13a / Task13) | — | 35,1 / 35,9 MB | — | Phiên vision để lại khoảng 15–16 MB khi rảnh |

| # | Bằng chứng | Kết quả lượt 1 / 2 (`31548f8`) |
|---|-----------|--------------------|
| E1 | Build + BOM + tinh chỉnh Gate 13 | ✅ 0/0; **103** file, 0 BOM; ô Right MFD hết lặp; clipboard có cờ chống Win+V |
| E2 | JPEG ≤ 256 000 B, `/img` clipboard (không chép) + `--file`, đo đỉnh | ✅ 8 361 009 → 65 900 B; sha khớp ở cả 3 đường; đỉnh 65,6 / 73,6 MB |
| E3 | Vision cache | ✅ 25/25, 0 token; selftest P95 **2,056 / 2,203 ms** (trước: 2,988); Cockpit P95 2,491 / 2,81 ms (SLA 3,0) |
| E4 | Khiên + MCP | ✅ `[COMBAT SHIELD]` P95 0,106 / 0,183 ms, 0 lần chụp; `[PRIVACY GUARD]`; MCP → Cockpit 164,8 / 156,7 ms; foreground không đổi |
| E5 | Rảnh ≤ 45,0 MB + `vision-mem` + CPU + hồi quy + Git + CI | ❌ trung vị **50,8 / 51,7 MB**; `vision-mem` +5,8 MB. Phần còn lại đạt: CPU 0 %, tầng nhanh 53,5 / 54,7 s, `[SLA-WARN SUMMARY] Count=0`, Git không đổi, CI xanh |

---

## 3. Điểm mở cần Architect quyết định

1. **Phần RAM rảnh còn vượt mục tiêu là bộ nhớ native của GDI+, không phải GC.**
   - Selftest `vision-mem`: sau ảnh 2K thứ 6, bộ nhớ đứng ở 16,2 MB; sau nén LOH còn 15,7 MB. Managed chỉ 1,21 MB, GC committed 1,45 MB.
   - Trên runner bậc này là 2,7 MB, trên máy 4,9 MB; trên Cockpit thật khoảng 15–16 MB so với lúc không dùng vision.
   - Nén LOH và HeapCompact (đã thử trên runner) không chạm tới phần này.
   - Ba hướng:
     - **(a)** *(Claude Code khuyến nghị)* giải mã theo dòng qua COM WIC (`IWICBitmapScaler` / `IWICBitmapSourceTransform`), bỏ GDI+ ở đường giải mã.
     - **(b)** *(ca viết code khuyến nghị, §19.7)* giải mã và nén trong một tiến trình con ngắn hạn (McpTools chế độ "thợ nén"), để toàn bộ bộ nhớ native trả về khi tiến trình đó thoát. WIC vẫn dùng heap native trong tiến trình nên không có gì bảo đảm.
     - **(c)** chấp nhận mục tiêu rảnh sau phiên vision khoảng 52 MB (cổng cứng hiện tại); hiện chỉ dư 0,3–1,2 MB.
2. **Đỉnh đã đạt.** `/img` clipboard từ khoảng 145 xuống 65,6 MB, nhưng mức nền ngay trước đó là 64,8 MB, tức trên 60 MB sẵn trước khi đọc ảnh. Mô hình hai tầng chỉ cảnh báo mềm ở 120 MB nên không chặn, nhưng nếu muốn "đỉnh < 60 MB" thì phải hạ mức nền trước.
3. **Hàm đo dùng chung hoạt động tốt**: 8/8 lần đọc trùng nhau tới 0,1 MB, Task13a E3 lệch còn 0,1 MB. Đề nghị duyệt.
4. **Cockpit UI v2** (`ui_v2_proposal.md`): duyệt hướng, đặt số task và xếp sau TASK-12c. Lỗi B1 (bản ghi selftest hiện trên HUD và lịch sử thật) nên sửa sớm.
5. **Để sau**: gọi Claude trực tiếp từ Cockpit chỉ làm khi Nhật Hòa yêu cầu (có thể tốn phí).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. TASK-12c: tiến trình con nén ảnh** *(ca viết code khuyến nghị)* | McpTools chế độ "thợ nén"; khiên vẫn trong HostService; `visionWorker` = `process` \| `inproc`; Claude Code nghiệm thu lại bằng 3 lệnh ở mục 2 | Bộ nhớ native trả hết khi tiến trình con thoát, bất kể nguồn gốc |
| A'. TASK-12c: WIC COM theo dòng | Hướng Claude Code đề xuất | Không bảo đảm vì vẫn dùng heap native trong tiến trình |
| D. Cockpit UI v2 | Bước 1: B1–B6 + kéo lên / xuống (U1); bước 2: chế độ lười + bảng liên minh; bước 3: bảng lệnh `/` `@`; bước 4: bố cục mới | `ui_v2_proposal.md` |
| B. Chấp nhận và merge | Architect hạ mục tiêu rảnh sau vision về 52 MB; Nhật Hòa merge + gắn tag | Mọi tiêu chí chức năng và đỉnh đã đạt; lề bộ nhớ vẫn mỏng |
| C. Dọn nhánh thử | Nhật Hòa xóa `claude/task-12-ci-probe` trên GitHub | Chỉ dùng để chạy selftest trên runner |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, đây là lời nhắn gộp của liên minh sau Gate 12 lượt TASK-12b. Chi tiết ở `gate12_handoff.md`, `qa_report.md` §19.6–§19.7 và đề xuất mới `ui_v2_proposal.md` (nhánh `claude/task-12-multimodal-vision`).
>
> **1. Gate 12 sau TASK-12b (Claude Code, 02:49–03:00, @`31548f8`): CHƯA ALL PASS, chỉ E5 trượt.**
> - Đỉnh `/img` clipboard: **~145 → 65,6 MB** ✅. E3 selftest P95 **2,988 → 2,056 ms** ✅. E1, E2, E4 PASS; Verify-Task13a và Verify-Task13 ALL PASS; HUD lệch 0,1 MB.
> - Trung vị RAM rảnh sau phiên vision **50,8 / 51,7 MB**, mục tiêu ≤ 45 ❌ (cổng cứng 52 vẫn đạt). Không dùng vision thì 35,1–35,9 MB.
> - Phần còn lại là **bộ nhớ native của GDI+** (managed sau nén chỉ 1,2 MB). Claude đính chính: kết luận "tăng một lần rồi phẳng" rút từ runner không đúng trên máy thật (bậc tăng ở ảnh thứ 6), nên chưa loại trừ được việc tăng dần.
>
> **2. Đề xuất TASK-12c.** Ca viết code khuyến nghị **tiến trình con**: McpTools chế độ "thợ nén" trả JPEG ≤ 250 KB qua stdout rồi thoát, hệ điều hành thu lại toàn bộ bộ nhớ native. Khiên Combat/Privacy vẫn chạy trong HostService trước khi khởi động tiến trình con; cache hit không đổi; mỗi lần miss chậm thêm khoảng 0,1–0,3 s; có cờ `visionWorker` = `process` | `inproc`. Claude Code đề xuất WIC COM theo dòng, nhưng WIC vẫn dùng heap native trong tiến trình nên không bảo đảm.
>
> **3. Đề xuất Cockpit UI v2** (`ui_v2_proposal.md`; mockup 6 khung, Nhật Hòa gửi ảnh nếu cần):
> - **Lỗi từ ảnh Cockpit thật**:
>   - B1: bản ghi selftest/Verify (NEEDS_WORK, ESCALATED - 3 STRIKES) hiện trên HUD và lịch sử thật, **nặng nhất**;
>   - B2: hai số Private WS khác nhau, dòng Working Set 144,5 MB gây hiểu nhầm;
>   - B3: ô câu trả lời chỉ hiện log;
>   - B4: chữ bị cắt ở 4 chỗ;
>   - B5: chỉ báo "0 thẻ" dễ hiểu nhầm;
>   - B6: Cockpit đè lên cửa sổ làm việc.
> - **U1**: kéo lên / xuống 3 mức (Thu gọn 40 / Nửa 330 / Đầy 640 px), nhớ vị trí, tự thu gọn khi rảnh, chế độ **xuyên thấu** (mờ 60 %, chuột đi qua).
> - **U2 chế độ lười**: `Alt+Enter` copy đúng lời nhắn / prompt của bước kế tiếp trong vòng liên minh; thông báo khi Claude Code xong ca (tool MCP mới); nhận biết ảnh mới trong clipboard; bấm số 1–9 sau câu trả lời; bảng phím tắt F1.
> - **U3**: bảng lệnh `/` và chọn agent `@`. **U4**: bảng điều phối liên minh (`Alt+A`). **U5**: bố cục mới và lịch sử hỏi–đáp.
> - Ràng buộc: RAM trong cổng, CPU 0 % khi rảnh, không phím toàn cục mới, không gửi gì khi chưa bấm, không tốn phí.
> - Thứ tự đề xuất, tất cả sau TASK-12c: (1) B1–B6 + U1 → (2) U2 + U4 → (3) U3 → (4) U5.
> - **Để sau, chỉ làm khi Nhật Hòa yêu cầu**: gọi Claude trực tiếp từ Cockpit (có thể tốn phí).
>
> **Cần bạn quyết:**
> - (1) TASK-12c theo hướng tiến trình con, WIC COM, hay hạ mục tiêu rảnh sau vision về 52 MB;
> - (2) duyệt hàm đo dùng chung;
> - (3) duyệt hướng Cockpit UI v2, đặt số task và xếp lộ trình (đề xuất làm B1 sớm);
> - (4) dọn nhánh thử `claude/task-12-ci-probe` (Nhật Hòa xóa).
