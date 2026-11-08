# 📦 HỒ SƠ BÀN GIAO — Gate 14b (TASK-14b: ADR-55, ADR-56, gia cố a/b/c)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-08, 21:54–21:56 (lượt chạy 4). Các ca trước: lượt 1–3 lúc 18:10–18:19 (bản vá `09425b6`); Quyết định (0b) ở `04f212c` lúc 20:55, chưa build.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-14b-ci-fast-regression`**, tách từ `main` @`089abcb`. Đầu nhánh code là **`04f212c`**; sau đó chỉ có commit tài liệu của ca này.
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-14b)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §16. §16.4: lượt 1–3; §16.5: Quyết định (0b); **§16.6: lượt 4**.
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. **CI GitHub xanh trên `04f212c`.**

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Quyết định (0b) ở `04f212c` | Đã viết code, **chưa build**, chưa chạy trên máy | Build 0/0; `Verify-Task14b.ps1` lượt 4 **ALL PASS (E1–E5)** |
| 2 | Gate 14b | ❌ E3 trượt (lượt 3: `/g` lookup P95 2,368 ms > 2,0) | ✅ E3 PASS: tầng nhanh 54,9 s, `relaxedSla=True`, **1** `[SLA-WARN]` (`/g` lookup P95 2,113 ms, trần 5,0) |
| 3 | CI GitHub | Xanh trên `09425b6` | Xanh trên `04f212c` ([run 42487240582](https://github.com/bitmap1701/NEXUS-Home-Hub/actions/runs/42487240582)) |
| 4 | Code | — | **Không sửa code**: lượt 4 đạt ngay lần đầu |
| 5 | Tài liệu | §16.5 ghi "chưa build" | §16.5 ghi đã kiểm chứng; thêm §16.6 (bảng E1–E5, dòng `[SLA-WARN]`, mảng 10 mẫu); viết lại file này |

Trước khi chạy, Claude kiểm tra: không có game (Delta Force, nguyên nhân hoãn lượt trước, đã thoát), CPU toàn máy khoảng 25 % (Discord, Chrome vẫn mở). Không tắt tiến trình nào bằng tay, không sửa `main` hay tag. Trong lúc Verify chạy, Claude không chạy lệnh git nào; HEAD và nhánh không đổi (E2).

---

## 2. Trạng thái Gate 14b — ✅ ALL PASS (E1–E5, lượt 4, `04f212c`)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14b-ci-fast-regression
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14b.ps1
```
Không chạy lệnh git nào khi Verify đang chạy. Lượt chạy không gọi Gemini thật, mất khoảng 2 phút. Kết quả ghi vào `task14b_verification.json` (`allPassed=True`).

| # | Bằng chứng | Tiêu chí PASS | Kết quả lượt 4 (`04f212c`) |
|---|-----------|---------------|---------|
| E1 | Build + 0 BOM toàn repo | Build 0/0; `Test-NexusBom` 0 vi phạm trên mọi file `git ls-files` | ✅ 0/0; **87** file, **0** vi phạm; 4 file bắt buộc được theo dõi |
| E2 | Chốt Git | Bắt đúng HEAD đổi, nhánh đổi, cây bẩn trên dữ liệu giả; HEAD thật không đổi, cây sạch | ✅ 4/4 ca giả; HEAD + nhánh không đổi, cây sạch |
| E3 | Tầng nhanh | `-FastRegression` **< 90 s**; `task14`, `task11`, `task06` đều `allPassed`; không gọi Gemini; chạy ở chế độ nới (0b) | ✅ **54,9 s**, exit 0; cả 3 task PASS; không gọi Gemini; `relaxedSla=True`, **1** cảnh báo |
| E4 | CPU + RAM | CPU rảnh Cockpit sau nhịp lắng < 0,5 %; selftest timer < 1 %; Private WS < 60 MB (≥ 54 vàng) | ✅ **0 %** sau 2 000 ms; selftest **0 %**; **57,7 MB** (**vàng**) |
| E5 | CI | Workflow đủ trigger, runner, .NET 8, BOM / build / smoke; lượt chạy xanh | ✅ mọi kiểm tra file đạt; **CI cho `04f212c` xanh** |

### Dòng `[SLA-WARN]` của lượt 4
```
[SLA-WARN (FastRegression jitter)] vault-grounding: /g cache_lookup_ms P95 = 2.113 ms (SLA 2 ms, ceiling 5 ms) -> PASS with warning
```
Mảng lookup `/g` (ms): 0,465 0,782 0,469 **2,956** 0,754 0,535 1,082 0,491 0,875 0,649. Một mẫu đột biến gây ra cảnh báo, giống lượt 3; theo (0b), lần này không làm trượt Gate.

### Số đo chính bên trong tầng nhanh
| Phép đo | Lượt 4 | SLA cứng |
|---------|--------|----------|
| `/calc` 30 lần, tổng P50 / P95 | 1,139 / 1,642 ms | 1,5 / 2,5 ✅ |
| `/ask` hit, tổng P50 / P95 | 1,932 / 2,867 ms | 2,5 / 4,0 ✅ |
| `/qa` hit, tổng P50 / P95 | 1,61 / 2,162 ms | 2,5 / 4,0 ✅ |
| `/g` hit, lookup P95 | **2,113 ms** | 2,0 ⚠️ (trần 5,0) |
| `/g` hit, tổng P50 / P95 | 1,906 / 3,45 ms | 2,5 / 4,0 ✅ |
| Timer 250 ms | 254 ms | ≤ 400 ✅ |
| MCP calc → Cockpit | 160,1 ms | < 300 ✅ |
| ADR-14 ghi/đọc đồng thời | 40 ghi, 19 256 đọc, 0 file rách | 0 ✅ |
| Token live / mock trong lượt | không đổi | 0 token ✅ |

---

## 3. Điểm mở cần Architect quyết định

1. **Private WS 57,7 MB (vàng lần thứ hai).** Lượt 2 là 57,1 MB, lượt 3 là 50,7 MB; trần 60 MB chỉ còn cách 2,3 MB. Điều này ảnh hưởng trực tiếp tới TASK-13 (HUD Micro-Widgets sẽ thêm bộ nhớ). Hướng:
   - (a) chấp nhận, theo dõi tiếp;
   - (b) trước TASK-13, làm một task nhỏ đo xem phần nào của Cockpit chiếm bộ nhớ *(Claude khuyến nghị)*.
2. **Gắn tag và merge.** Gate 14b đã ALL PASS. Có fast-forward `main` lên nhánh này và gắn `v0.12.1` không? Việc này do Nhật Hòa làm; Claude không đụng `main` hay tag.
3. **Theo dõi `[SLA-WARN]`.** Đề xuất ghi số cảnh báo của mỗi lượt vào handoff. Nếu cùng một phép đo cảnh báo ở nhiều lượt liên tiếp khi máy rảnh, thì đó là suy giảm thật, cần xử lý chứ không phải nhiễu. Hiện tại chỉ `/g` lookup cảnh báo, và chỉ vì một mẫu đột biến.
4. Các điểm cũ (2)–(6) ở §16.2 đã được Architect duyệt cùng (0b); không còn mở.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Merge Gate 14b, gắn `v0.12.1`** *(khuyến nghị)* | Nhật Hòa fast-forward `main` lên `claude/task-14b-ci-fast-regression` rồi gắn tag | Gate ALL PASS, CI xanh. Bản vá `09425b6` phải đi cùng, nếu không `-FullRegression` sẽ hỏng khi có tham số `-CpuSamples` |
| B. Đo bộ nhớ Cockpit trước TASK-13 | Xác định phần nào chiếm 50–58 MB Private WS; giảm nếu có chỗ dễ | Hai lượt đã vàng; TASK-13 sẽ thêm widget |
| C. TASK-13 | HUD Micro-Widgets, Right MFD (ADR-58) | Làm sau B để không vượt trần 60 MB |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 14b (TASK-14b) đã ALL PASS (E1–E5)** ở lượt chạy 4 trên máy Nhật Hòa (Claude Code, 21:54–21:56), nhánh `claude/task-14b-ci-fast-regression`, code ở `04f212c` (Quyết định 0b). Không phải sửa code. Chi tiết ở `gate14b_handoff.md` và `qa_report.md` §16.6.
>
> - E1: build 0/0; 87 file, 0 vi phạm BOM.
> - E2: chốt Git 4/4 ca; HEAD không đổi.
> - E3: tầng nhanh **54,9 s** (< 90), không gọi Gemini, task14/task11/task06 đều PASS, `relaxedSla=True`. Có **1** `[SLA-WARN]`: Task11 `/g` lookup P95 = 2,113 ms (SLA 2, trần 5), do một mẫu 2,956 ms. Các số khác đạt SLA cứng: `/calc` P50/P95 1,139/1,642; `/ask` 1,932/2,867; `/g` tổng 1,906/3,45 ms.
> - E4: CPU Cockpit 0 % sau lắng 2 s; Private WS **57,7 MB (vàng)**.
> - E5: CI xanh trên `04f212c`.
>
> Cần bạn quyết:
> - (1) Private WS vàng lần thứ hai (57,7 MB, trần 60): Claude khuyến nghị một task nhỏ đo bộ nhớ Cockpit trước TASK-13.
> - (2) Duyệt merge `main` và tag `v0.12.1` (Nhật Hòa thực hiện).
> - (3) Có ghi số `[SLA-WARN]` mỗi lượt vào handoff để phát hiện suy giảm thật không?
