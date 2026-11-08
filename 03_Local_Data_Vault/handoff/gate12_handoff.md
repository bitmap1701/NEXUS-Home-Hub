# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12 + TASK-12b: Multimodal Vision `/img` & `/snap`, ép bộ nhớ đường ảnh, ADR-61)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-09, 02:49–03:00 (nghiệm thu lại sau TASK-12b). Ca viết code TASK-12b (Claude) làm tới `31548f8`; lượt 1–3 của Gate 12 cũ ở §19.4.
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
     - **(a)** *(Claude khuyến nghị)* giải mã theo dòng qua COM WIC (`IWICBitmapScaler` / `IWICBitmapSourceTransform`), bỏ GDI+ ở đường giải mã. Ca viết code đã để dành bước này ở §3.1 của handoff trước.
     - **(b)** giải mã và nén trong một tiến trình con ngắn hạn, để toàn bộ bộ nhớ native trả về khi tiến trình đó thoát.
     - **(c)** chấp nhận mục tiêu rảnh sau phiên vision khoảng 52 MB (cổng cứng hiện tại); hiện chỉ dư 0,3–1,2 MB.
2. **Đỉnh đã đạt.** `/img` clipboard từ khoảng 145 xuống 65,6 MB, nhưng mức nền ngay trước đó là 64,8 MB, tức trên 60 MB sẵn trước khi đọc ảnh. Mô hình hai tầng chỉ cảnh báo mềm ở 120 MB nên không chặn, nhưng nếu muốn "đỉnh < 60 MB" thì phải hạ mức nền trước.
3. **Hàm đo dùng chung hoạt động tốt**: 8/8 lần đọc trùng nhau tới 0,1 MB, Task13a E3 lệch còn 0,1 MB. Đề nghị duyệt.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. TASK-12c: giải mã WIC COM theo dòng** *(khuyến nghị)* | Ca viết code thay đường giải mã GDI+ bằng WIC COM (scaler theo dòng, không dựng bitmap 2K đầy đủ); giữ nén JPEG; Claude Code nghiệm thu lại bằng 3 lệnh ở mục 2 | Phần còn lại nằm trong bộ nhớ native của GDI+; GC đã sạch |
| B. Chấp nhận và merge | Architect hạ mục tiêu rảnh sau vision về 52 MB; Nhật Hòa merge + gắn tag | Mọi tiêu chí chức năng và đỉnh đã đạt; lề bộ nhớ vẫn mỏng |
| C. Dọn nhánh thử | Nhật Hòa xóa `claude/task-12-ci-probe` trên GitHub | Chỉ dùng để chạy selftest trên runner |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, kết quả nghiệm thu lại Gate 12 sau **TASK-12b** trên máy Nhật Hòa (Claude Code, 02:49–03:00), nhánh `claude/task-12-multimodal-vision` @`31548f8`: **CHƯA ALL PASS**. Chỉ E5 trượt, và trượt ở mục tiêu bộ nhớ của TASK-12b; cổng cứng 52 MB vẫn đạt. Chi tiết ở `gate12_handoff.md` và `qa_report.md` §19.6.
>
> - **Đỉnh `/img` clipboard: ~145 MB → 65,6 MB** (chỉ +0,8 MB so với nền; giữ clipboard ~80 ms). Đỉnh `/img --file` 2K 73,6 MB.
> - **Trung vị RAM rảnh sau phiên vision: 50,8 / 51,7 MB** (2 lượt, 8/8 lần đọc trùng) so với mục tiêu **≤ 45,0** ❌. Selftest `vision-mem`: +5,8 MB (mục tiêu ≤ 4,3), bậc 10,9 → 15,7 MB ở ảnh 2K thứ 6, lặp lại ở cả 2 lượt.
> - Phần còn lại là **bộ nhớ native của GDI+**: managed sau nén chỉ 1,21 MB, GC committed 1,45 MB. Không dùng vision (Task13a/13 cùng code) thì rảnh 35,1 / 35,9 MB.
> - E1–E4 PASS: build 0/0, 103 file 0 BOM; JPEG 65 900 B, sha khớp ở cả 3 đường; cache 25/25, selftest P95 **2,056 ms** (trước 2,988), Cockpit 2,81 ms; khiên OK, MCP → Cockpit 157–165 ms. Verify-Task13a và Verify-Task13 đều ALL PASS; HUD lệch 0,1 MB.
> - Claude Code không sửa gì: không có lỗi script; phần trượt là của sản phẩm.
>
> Cần bạn quyết:
> - (1) làm TASK-12c (giải mã WIC COM theo dòng, bỏ GDI+ ở đường giải mã; Claude khuyến nghị), hay tiến trình con, hay hạ mục tiêu rảnh sau vision về 52 MB;
> - (2) mức nền trước `/img` đã khoảng 65 MB, nên "đỉnh < 60 MB" chưa thể đạt nếu không hạ nền;
> - (3) duyệt hàm đo dùng chung.
