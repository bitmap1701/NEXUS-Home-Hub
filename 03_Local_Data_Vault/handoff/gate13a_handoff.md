# 📦 HỒ SƠ BÀN GIAO — Gate 13a (TASK-13a: Cockpit Memory Slimming, nhãn Private WS, `[SLA-WARN SUMMARY]`)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-08, 22:19–22:27. Ca trước (Claude, không có Windows) viết code ở `1663ae7` + `a29c79b`.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-13a-memory-slimming`**, tách từ `main` @`54cf26c`. Đầu nhánh code là **`d5f6fcd`** (bản vá script của ca này); sau đó chỉ có commit tài liệu.
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-13a)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §17 (§17.4 là kết quả ca này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. **CI GitHub xanh trên `72c9ac8`** (gồm bản vá `d5f6fcd`).

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Số đo bộ nhớ | Chưa có (ca trước không có Windows) | Bảng "trước/sau" thật: Private WS khi rảnh **58,0 → 41,4 MB** (lượt 2), **57,7 → 40,4 MB** (lượt 1) |
| 2 | `Verify-Task13a.ps1` E3 | Lượt 1 FAIL: HUD 33,8 MB, script đo 40,4 MB (lệch 6,6 MB) | Bản vá `d5f6fcd`: chờ số đo Private WS ổn định (2 lần đọc cách 1 s lệch ≤ 0,5 MB) rồi mới đọc `MEM`. Lượt 2 lệch 1,7 MB, PASS |
| 3 | Kiểm chứng nguồn đo của nhãn HUD | Chỉ có lập luận trong code | Gọi `GetProcessMemoryInfo` EX2 từ ngoài và so với WMI trên 4 tiến trình: **trùng đến 0,1 MB** |
| 4 | Gate 13a | ⏳ chưa đủ bằng chứng | ✅ **ALL PASS (E1–E5)** ở lượt 2 |
| 5 | Tài liệu | §17.3 "chưa chạy" | Thêm §17.4 (bảng trước/sau, nguyên nhân lượt 1, số đo lượt 2); viết lại file này |

Không đổi cấu hình GC, không sửa code sản phẩm. Không tắt tiến trình nào bằng tay, không đụng `main` hay tag. Trong lúc Verify chạy, Claude không chạy lệnh git nào. Bản vá được commit trước lượt 2, vì E5 đòi cây sạch.

---

## 2. Trạng thái Gate 13a — ✅ ALL PASS (E1–E5, lượt 2, `d5f6fcd`)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-13a-memory-slimming
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13a.ps1
```
Không chạy lệnh git nào khi Verify đang chạy. Lượt chạy không tốn token Gemini, mất khoảng 3 phút. Kết quả ghi vào `task13a_verification.json` (`allPassed=True`).

### Bảng "trước / sau" (E2)
| Chỉ số | Gate 14b | Trước (tắt giảm bộ nhớ) | **Sau (bật)** |
|--------|----------|-------------------------|---------------|
| Private WS khi rảnh, lượt 2 | 57,7 MB | 58,0 MB | **41,4 MB** (−16,6) |
| Private WS khi rảnh, lượt 1 | — | 57,7 MB | **40,4 MB** (−17,3) |
| GC heap committed (lượt 2) | — | 13,43 MB | 4,08 MB |
| SQLite native (lượt 2) | — | 2,08 MB | 0 MB |
| Managed heap (lượt 2) | — | 9,5 MB | 4,13 MB |
| Chỉ mục 283 agent | — | ~251 KB | ~251 KB |
| Lần dọn khi rảnh (lượt 2) | — | không | 55,9 → 41,4 MB, 10,7 ms |

| # | Bằng chứng | Tiêu chí PASS | Kết quả lượt 2 |
|---|-----------|---------------|---------|
| E1 | Build + 0 BOM | `-warnaserror` 0/0; `Test-NexusBom` 0 vi phạm | ✅ 0/0; **91** file, **0** vi phạm |
| E2 | A/B bộ nhớ | Lượt "sau" ≤ 52,0 MB (mục tiêu ≤ 50,0); lần dọn đã chạy; selftest `mem-profile` PASS | ✅ **41,4 MB** (≤ 52 ✅, ≤ 50 ✅); dọn 10,7 ms; selftest PASS; tải 0 lỗi |
| E3 | Nhãn HUD | Lệch < 2,0 MB so với WMI; `/status` có "Private WS:"; khác Commit | ✅ HUD 39,7 so với 41,4 MB (**lệch 1,7**); Commit 82,3 MB; nguồn EX2 |
| E4 | Tầng nhanh | < 90 s, PASS, in `[SLA-WARN SUMMARY]` | ✅ **54,3 s**; 3 task PASS; `[SLA-WARN SUMMARY] Count=0 \| Details=NONE`; không có `[DRIFT-ALERT]` |
| E5 | CPU + Git + CI | CPU Cockpit < 0,5 %; HEAD không đổi, cây sạch; CI | ✅ **0 %**; HEAD không đổi, cây sạch; CI chưa có lượt cho `d5f6fcd` lúc chạy (chưa push); sau khi push, **CI xanh trên `72c9ac8`** ([run 42494921704](https://github.com/bitmap1701/NEXUS-Home-Hub/actions/runs/42494921704)) |

Số đo ms ở tầng nhanh, lượt 2 (đều dưới SLA cứng): `/calc` P50 1,026 / P95 1,402; `/ask` 1,658 / 2,105; `/g` lookup P95 0,757, tổng 1,646 / 1,988 ms.

---

## 3. Điểm mở cần Architect quyết định

1. **Duyệt bản vá `d5f6fcd`** (chỉ sửa script Verify). Sau lần dọn, Private WS còn rơi tiếp khoảng 3–4 s (GC trả bộ nhớ dần), và WMI trễ khoảng 1 s. Bản vá chờ số đo ổn định rồi mới so với nhãn. Độ lệch E3 lượt 2 vẫn sát ngưỡng (1,7 / 2,0 MB). Nếu còn dao động, có thể đọc WMI ngay sau `MEM`.
2. **Ngưỡng E2 có nên hạ không?** Đo được 40,4–41,4 MB ở giây 8–10. Khi theo dõi 18 s, Private WS ổn định ở 31–35 MB. Đề xuất giữ ≤ 52 MB làm cổng, ghi 41 MB làm mốc mới để `[DRIFT-ALERT]` có cơ sở so sánh.
3. Các điểm ca trước nêu, nay đã có số đo hỗ trợ:
   - (a) không làm LRU cho AgentRegistry/Vault: đo được chỉ mục agent khoảng 0,25 MB;
   - (b) chưa đổi `UndoLimit`;
   - (c) dọn bằng GC Aggressive và xóa pool SQLite, không `EmptyWorkingSet`; GC committed giảm 13–18 → 4 MB;
   - (d) Verify-Task14 E5 đọc RAM sau mẫu CPU.
4. Không cần các bước GC dự phòng (tắt GC đồng thời, TieredPGO, render WPF), vì E2 đã dưới mục tiêu 50 MB.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Merge Gate 13a, gắn `v0.12.2`** *(khuyến nghị)* | Nhật Hòa fast-forward `main` lên nhánh này sau khi CI xanh trên commit mới nhất, rồi gắn tag | ALL PASS; RAM giảm khoảng 17 MB |
| B. TASK-13 | HUD Micro-Widgets, Right MFD (ADR-58) | Đã có 18,6 MB dư địa tới trần 60 MB; mỗi widget nên được đo bằng E2 của Verify-Task13a |
| C. Làm E3 bền hơn | Đọc WMI ngay sau `MEM`, hoặc so với `snapshot.PrivateWorkingSetMb` cùng thời điểm | Lệch 1,7 MB sát ngưỡng 2,0 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 13a (TASK-13a) đã ALL PASS (E1–E5)** trên máy Nhật Hòa (Claude Code, 22:19–22:27), nhánh `claude/task-13a-memory-slimming`, code ở `d5f6fcd`. Chi tiết ở `gate13a_handoff.md` và `qa_report.md` §17.4.
>
> - **Bộ nhớ (E2)**: Private WS khi rảnh từ **58,0 → 41,4 MB** (lượt 1: 57,7 → 40,4), đạt cả cổng ≤ 52 lẫn mục tiêu ≤ 50 MB. Phần giảm đến từ GC committed (13,4 → 4,1 MB) và SQLite native (2,1 → 0 MB). Chỉ mục agent chỉ ~0,25 MB. Lần dọn mất 10,7 ms. Không đổi cấu hình GC.
> - **Lượt 1 E3 FAIL** (HUD 33,8 so với script 40,4 MB). Claude kiểm chứng thì `GetProcessMemoryInfo` EX2 và WMI trùng nhau đến 0,1 MB trên 4 tiến trình, nên nhãn đúng chỉ số. Lỗi do script đọc WMI lúc bộ nhớ còn đang rơi sau lần dọn (rơi tiếp khoảng 12 MB trong 3–4 s, WMI trễ khoảng 1 s). Bản vá `d5f6fcd` chỉ sửa `Verify-Task13a.ps1`: chờ hai lần đọc ổn định rồi mới so.
> - **Lượt 2**: E1 build 0/0, 91 file 0 BOM; E2 41,4 MB; E3 lệch 1,7 MB (< 2); E4 tầng nhanh 54,3 s, `[SLA-WARN SUMMARY] Count=0`, không có `[DRIFT-ALERT]`; E5 CPU 0 %, HEAD không đổi.
> - **CI GitHub xanh trên `72c9ac8`** (gồm bản vá).
>
> Cần bạn quyết:
> - (1) duyệt bản vá `d5f6fcd`;
> - (2) giữ cổng ≤ 52 MB và ghi mốc mới 41 MB (rảnh lâu còn 31–35 MB);
> - (3) duyệt merge `main` và tag `v0.12.2` (Nhật Hòa thực hiện) để mở TASK-13 với 18,6 MB dư địa.
