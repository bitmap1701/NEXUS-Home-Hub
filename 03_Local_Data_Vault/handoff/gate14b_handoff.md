# 📦 HỒ SƠ BÀN GIAO — Gate 14b (TASK-14b: ADR-55, ADR-56, gia cố a/b/c)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-08, 18:10–18:19. Ca trước (Claude, chưa build) viết code ở `b276a95` + `024b6b1`.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-14b-ci-fast-regression`**, tách từ `main` @`089abcb`; đầu nhánh **`09425b6`** (bản vá của ca này)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-14b)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §16 (§16.4 là kết quả ca này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. **CI GitHub xanh trên `09425b6`.**
- **Sau lượt 3 (Claude, 20:55)**: Architect duyệt `09425b6` và các điểm (1)–(6), chốt **(0b)**. Đã sửa: ở `-FastRegression`, ngưỡng ms vượt SLA nhưng ≤ 2,5 × SLA thì in `[SLA-WARN (FastRegression jitter)]` và vẫn PASS; chức năng, dữ liệu, bảo mật, bộ nhớ vẫn chấm cứng; `-FullRegression` chấm cứng như cũ (`qa_report.md` §16.5). Thay đổi này **chưa build**, cần **lượt chạy 4** trên máy và CI xanh trên commit mới. Mục 2–5 bên dưới là kết quả lượt 1–3, giữ làm lịch sử cho tới khi lượt 4 ghi đè.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Build | Chưa build lần nào | `dotnet build -c Release -warnaserror`: 0 Warning(s), 0 Error(s) |
| 2 | `Verify-Task11.ps1` E5 | **Luôn lỗi** `Cannot convert System.Object[] to System.Int32`: mảng `$cpuSamples` trùng tên (PowerShell không phân biệt hoa thường) với tham số `[int]$CpuSamples` mới | Đổi tên mảng thành `$cpuSeries` (4 chỗ). Task11 E5 chạy xong, PASS (`09425b6`) |
| 3 | CI GitHub (ADR-56) | Job không chạy (tài khoản bị khóa thanh toán) | Lượt CI cho `09425b6`: **completed / success** ([run 42474931394](https://github.com/bitmap1701/NEXUS-Home-Hub/actions/runs/42474931394)) |
| 4 | `Verify-Task14b.ps1` | Chưa chạy | 3 lượt. Lượt 2–3: **E1, E2, E4, E5 PASS; E3 FAIL** vì ngưỡng ms của Verify-Task11 bị nhiễu khi máy bận |
| 5 | Tài liệu | §16.3 "chưa chạy" | §16.3 cập nhật CI; thêm §16.4 (bản vá, bảng 3 lượt, số đo, mảng mẫu) |

Không tắt tiến trình nào bằng tay, không sửa `main` hay tag. Trong lúc Verify chạy, Claude không chạy lệnh git nào; `.git/HEAD` không đổi suốt ba lượt. Sau lượt 3, Nhật Hòa chọn **dừng và ghi hiện trạng** thay vì chạy tiếp.

---

## 2. Trạng thái Gate 14b — ❌ CHƯA ALL PASS (E3 trượt vì ngưỡng ms bị nhiễu; mọi tiêu chí khác đạt)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14b-ci-fast-regression
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14b.ps1
```
Trước khi chạy: tắt game, **đóng Chrome/Discord** (máy nên dưới khoảng 10 % CPU); không chạy lệnh git nào khi Verify đang chạy. Lượt chạy không gọi Gemini thật, mất khoảng 2 phút.

| # | Bằng chứng | Tiêu chí PASS | Kết quả lượt 3 (`09425b6`) |
|---|-----------|---------------|---------|
| E1 | Build + 0 BOM toàn repo | Build 0/0; `Test-NexusBom` 0 vi phạm trên mọi file `git ls-files` | ✅ 0/0; **87** file, **0** vi phạm; 4 file bắt buộc được theo dõi |
| E2 | Chốt Git | Bắt đúng HEAD đổi, nhánh đổi, cây bẩn trên dữ liệu giả; HEAD thật không đổi, cây sạch | ✅ 4/4 ca giả; HEAD + nhánh không đổi, cây sạch |
| E3 | Tầng nhanh | `-FastRegression` **< 90 s**; `task14`, `task11`, `task06` đều `allPassed`; không gọi Gemini | ❌ **53,2 s** ✅, không gọi Gemini ✅, Task06 ✅; **Task11 trượt một ngưỡng**: `/g` lookup P95 **2,368 ms** (ngưỡng 2,0; một mẫu 3,269 ms; tổng P50 1,615 / P95 3,767 đều đạt). Task14 trượt dây chuyền theo Task11; các phần riêng của Task14 đều đạt (`/calc` P50 0,891 / P95 0,991 ms; timer 260,9 ms; MCP → Cockpit 165,2 ms) |
| E4 | CPU + RAM | CPU rảnh Cockpit sau nhịp lắng < 0,5 %; selftest timer < 1 %; Private WS < 60 MB (≥ 54 vàng) | ✅ **0 %** sau 2 000 ms; selftest **0 %**; **50,7 MB** (lượt 2: 57,1 MB, vàng) |
| E5 | CI | Workflow đủ trigger, runner, .NET 8, BOM / build / smoke; lượt chạy xanh | ✅ 10/10 kiểm tra file; **CI cho `09425b6` xanh** |

### E3 qua ba lượt (máy bận 23–42 % CPU, chủ yếu do Chrome)
| Lượt | Thời gian tầng nhanh | Ngưỡng trượt |
|------|---------------------|-------------|
| 1 (`024b6b1`) | 50,9 s | Lỗi script Task11 E5 (đã vá); Task11 `/g` P50 2,736 / P95 5,052 ms |
| 2 (`09425b6`) | 58,6 s | Task14 `/calc` P50 1,547 / P95 3,13; Task11 `/ask` P50 2,586; `/g` P50 2,625 / P95 4,464 ms (máy 42 %) |
| 3 (`09425b6`) | 53,2 s | Chỉ Task11 `/g` lookup P95 2,368 ms |

Giá trị nhỏ nhất của `/g` ổn định 1,2–1,5 ms qua mọi lần chạy từ Gate 11b. Chỉ các mẫu đột biến thay đổi theo mức bận của máy.

---

## 3. Điểm mở cần Architect quyết định

0. **Ngưỡng ms trong tầng nhanh (mới, quan trọng nhất).** `-FastRegression` dùng cho vòng phát triển hằng ngày, khi máy thường có Chrome/Discord mở, nhưng lại kế thừa ngưỡng ms 10 mẫu của Gate 11b. P95 nội suy trên 10 mẫu gần như là mẫu lớn thứ hai, nên một mẫu đột biến là trượt. Ba hướng:
   - **(a)** giữ nguyên ngưỡng; Gate chỉ chấm khi máy rảnh, chạy lại khi Nhật Hòa đóng Chrome *(cách đã đạt ở Gate 11b lần 5)*;
   - **(b)** trong tầng nhanh, ngưỡng ms của Task11/Task14 chỉ báo vàng (không trượt), còn chấm cứng ở `-FullRegression` khi phát hành *(Claude khuyến nghị: tầng nhanh dùng để bắt lỗi chức năng; lỗi script ở lượt 1 là ví dụ)*;
   - **(c)** tăng số mẫu lên 30 (như `/calc`) để P95 bớt nhạy với một mẫu.
1. **Bản vá `09425b6`** (đổi tên `$cpuSamples` → `$cpuSeries` trong Verify-Task11): xin duyệt. Lỗi này làm hỏng cả `-FullRegression`, không riêng tầng nhanh.
2. **`.ps1` bắt buộc có BOM trong cổng CI** (trái câu chữ đặc tả; PowerShell 5.1 cần BOM cho tiếng Việt). E1 xác nhận 87 file đạt quy tắc hai chiều.
3. **CI build từng `.csproj`** (`.slnx` cần SDK 9.0.200+). CI xanh trên SDK 8.
4. **CI chỉ chạy smoke McpTools**, không chạy selftest HostService.
5. **Không ép `EmptyWorkingSet` trước phép đo RAM.** Private WS dao động 50,7–57,1 MB giữa các lượt; lượt 2 chạm mức vàng 54 MB.
6. Ô thứ 3 của `/ground` ghi dưới ADR-59 (tham chiếu ADR-45b), không dưới ADR-50.
7. ~~Tài khoản GitHub bị khóa thanh toán~~: đã ổn, CI chạy và xanh trên `09425b6`.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Quyết điểm 3.0, rồi chạy lại Gate 14b** *(khuyến nghị)* | Nếu chọn (a): Nhật Hòa đóng Chrome, Claude Code chạy lại (khoảng 2 phút, 0 token). Nếu (b) hoặc (c): Claude sửa nhỏ trong `Verify-Task11.ps1`/`Verify-Task14.ps1`, rồi chạy lại | Mọi tiêu chí khác đã đạt; CI xanh |
| B. Merge Gate 14b, gắn `v0.12.1` | Sau khi ALL PASS: fast-forward `main` lên nhánh này; Nhật Hòa gắn tag | `main` hiện chưa có tham số `-CpuSamples` nên chưa bị lỗi này. Khi merge, bản vá `09425b6` phải đi cùng, nếu không `-FullRegression` trên `main` sẽ hỏng |
| C. TASK-13 | HUD Micro-Widgets, Right MFD | Theo ADR-58; Private WS đang 50–57 MB, sát mức vàng 54 MB |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, kết quả Gate 14b (TASK-14b) trên máy Nhật Hòa: **CHƯA ALL PASS**, nhưng chỉ trượt vì ngưỡng ms bị nhiễu. Claude Code chạy `Verify-Task14b.ps1` 3 lượt (18:10–18:19), nhánh `claude/task-14b-ci-fast-regression`, đầu nhánh `09425b6`. Chi tiết ở `gate14b_handoff.md` và `qa_report.md` §16.4.
>
> - **Bản vá** `09425b6`: Verify-Task11 E5 luôn lỗi `Cannot convert System.Object[] to System.Int32`, vì mảng `$cpuSamples` trùng tên với tham số mới `[int]$CpuSamples` (PowerShell không phân biệt hoa thường). Đã đổi tên mảng thành `$cpuSeries`; lỗi này làm hỏng cả `-FullRegression`.
> - **CI GitHub xanh trên `09425b6`** (tài khoản đã hết bị khóa).
> - Lượt 3: E1 PASS (build 0/0, 87 file, 0 vi phạm BOM); E2 PASS (chốt Git 4/4 ca, HEAD không đổi); E4 PASS (CPU Cockpit 0 % sau lắng 2 s, selftest 0 %, Private WS 50,7 MB); E5 PASS (workflow 10/10, CI xanh).
> - **E3 FAIL**: tầng nhanh 53,2 s (< 90 ✅), không gọi Gemini ✅, Task06 ✅. Nhưng Task11 `/g` lookup P95 = 2,368 ms (ngưỡng 2,0) do một mẫu 3,269 ms. Máy bận 23–42 % CPU vì Chrome. Lượt 2 còn trượt `/calc` và `/ask` cùng kiểu.
>
> Nhật Hòa chọn dừng và ghi hiện trạng. Cần bạn quyết:
> - (0) ngưỡng ms trong tầng nhanh: (a) giữ, chỉ chấm khi máy rảnh; (b) chỉ báo vàng ở tầng nhanh, chấm cứng ở `-FullRegression` (Claude khuyến nghị); (c) tăng lên 30 mẫu;
> - (1) duyệt bản vá `09425b6`;
> - (2)–(6) các điểm cũ: `.ps1` có BOM, CI build `.csproj`, CI chỉ smoke McpTools, không ép thu hồi RAM, ô thứ 3 `/ground` dưới ADR-59.
