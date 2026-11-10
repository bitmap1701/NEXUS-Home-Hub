# 📦 HỒ SƠ BÀN GIAO — Gate 16b (TASK-16b: Cockpit UI v2 Phase 2 — nền đặc, markdown, lịch sử hỏi–đáp, chế độ lười, bảng liên minh, ADR-66)

- **Ca thực thi**: Claude (ca viết code), 2026-11-10 tối; Claude Code trên máy Nhật Hòa (ca chạy Verify), 2026-11-10
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-16b-cockpit-ui-v2-layout-lazy`**, tách từ `main` @`76158b1` (`v0.15.0`)
- **Đặc tả**: `current_task.md` (TASK-16b). Chi tiết kỹ thuật: **`qa_report.md` §21**
- **Trạng thái build**: **đã build và chạy Verify trên máy Nhật Hòa** (`4c87cc0`, lần chạy 2): build `-warnaserror` 0/0; **E2, E5 PASS; E1, E3, E4 trượt ngưỡng thời gian** (chi tiết mục 2). CI GitHub Actions xanh trên `229e750`.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| CUI-Opaque | Nền HUD | `#EB090D16` (92 %): chữ cửa sổ phía sau đọc được | **`#FC090D16` (99 %)**; xuyên thấu vẫn 60 % |
| CUI-Markdown | Ô câu trả lời | Markdown thô (`**…**`, `` ` ``) | In đậm, `code` tô màu, khối code có nền + viền, "• " cho gạch đầu dòng, tiêu đề in đậm; không thư viện ngoài |
| CUI-Layout | Cột trái | Telemetry mặc định | **Lịch sử 8 câu trả lời thật** (không bản ghi test); nhấp hoặc ↑↓ khi ô lệnh trống để xem lại; `Alt+Q` vẫn sang Telemetry |
| CUI-Lazy | Phím | Chỉ `Alt+C` / `Alt+P` / F1 | `Alt+Enter` chép bước liên minh kế tiếp; `1–9` khi ô lệnh trống (1 copy, 2 ghim, 3 ngắn hơn, 4 viết test); `Alt+I` = `/img`; gợi ý "Ảnh mới trong Clipboard" mỗi lần đánh thức |
| CUI-Alliance | Bảng liên minh | — | `Alt+A`: task, nhánh, Gate, Verify mới nhất, RAM, ca gần nhất, bước kế tiếp |
| MCP | Báo xong ca | — | `notify_shift_complete` → `config/shift_state.json` + chỉ báo trên thanh 40 px (âm báo tùy chọn) |
| Nợ Gate 16 | WAKE 40 → 330 px | Chưa đo | Ghi `lastWake` kèm phân rã; Verify E4 đo 10 mẫu |
| Kiểm thử | — | — | selftest `ui-v2b` (trong CI), `Verify-Task16b.ps1` E1–E5, pipe `ALLIANCE` / `HISTORY` / `LAZY` / `SHIFTDONE` |
| V | Verify trên máy (ca 2) | Lần 1: selftest `ui-v2b` ghi khóa JSON rỗng `""`, Windows PowerShell 5.1 không đọc được → script dừng ở E1 | `4c87cc0`: khóa rỗng ghi là `"(empty)"` (chỉ code selftest). Lần 2: E2, E5 PASS; E1, E3, E4 trượt ngưỡng thời gian |

Không đụng `main` hay tag, không có thao tác Cấp Đỏ, không hotkey toàn cục mới, không timer lặp mới, không gọi API trả phí.

---

## 2. Trạng thái Gate 16b — ❌ chưa PASS (máy Nhật Hòa, `4c87cc0`, lần chạy 2): E2, E5 PASS; E1, E3, E4 trượt ngưỡng thời gian

| # | Bằng chứng | Trạng thái |
|---|------------|-----------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ui-v2b` | ❌ build 0/0, Zero-BOM 119 file 0 vi phạm; selftest 14/15, trượt **`alliance.readUnder5ms`** (`review` 9,94 ms; chạy lại 10,3 / 8,7 / 9,6 ms) |
| E2 | nền alpha ≥ `0xF8` ở 330 / 640 px; markdown (selftest + hiển thị thật) | ✅ alpha 252 ở cả hai mức, xuyên thấu 0,6; parse 4 KB P95 0,0663 ms; hiển thị thật đủ bold/code/khối/gạch đầu dòng/tiêu đề, dựng 2,227 ms |
| E3 | lịch sử < 2 ms, không bản ghi test; `Alt+Enter` / `Alt+A` / F1 / phím 1; MCP `notify_shift_complete` < 2 ms; gợi ý ảnh | ❌ chỉ trượt **`historyViewUnder2`**: 24 lần xem P95 **3,948 ms** (truy vấn DB chỉ 0,081 ms). Còn lại PASS: 0 dòng test, `Alt+Enter` chép 456 ký tự đúng clipboard, `Alt+A` / F1 / phím 1, MCP shift 1,154 ms, gợi ý ảnh 1 lần |
| E4 | WAKE 40 → 330 px + focus, 10 mẫu: P50 ≤ 10, P95 ≤ 16 ms | ❌ P50 **11,558 ms** (vượt 1,56 ms), P95 13,554 ms ✅. Phân rã P95: đổi độ cao + hiện **10,304** / focus 3,606 / `Activate` 0,074 ms |
| E5 | Private WS lắng ≤ 45,0 MB (trần 52), CPU rảnh 0,0 %, chuỗi hồi quy Task16 → Task03, git | ✅ **37,4 MB**, CPU **0 %**; Task16 → Task13 → Task12 → Task13a → `-FullRegression -SkipLive` (Task14 … Task03) đều PASS; git không đổi |

**Lần chạy 1 (`1d32820`)**: dừng ở E1 vì selftest ghi khóa JSON rỗng, Windows PowerShell 5.1 `ConvertFrom-Json` không đọc được (CI dùng `pwsh` 7 nên không thấy). Vá ở `4c87cc0`, chỉ code selftest.

**Không chạy lần 3**: 3 chỗ trượt là ngưỡng thời gian cần Architect quyết (mục 3, điểm 8–10). Phân tích đầy đủ: `qa_report.md` §21.5.

### Lệnh chạy (Claude Code trên máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-16b-cockpit-ui-v2-layout-lazy
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task16b.ps1
```
- Chạy bằng Windows PowerShell 5.1 (STA) để kiểm được clipboard.
- Trong lúc chạy: không chạy lệnh git, không bấm vào HUD, không copy/paste, để yên chuột.
- E5 chạy cả chuỗi Task16 → Task13 → Task12 → Task13a → Task14 → … → Task03 (mặc định `-SkipLive`, 0 token), nên lâu (ước chừng vài chục phút).
- Muốn thử nhanh trước: thêm `-FastE5`. Kết quả nghiệm thu phải là bản chạy đầy đủ.
- Verify khôi phục `latest_dispatch.json`, `cockpit_ui.json` và chữ trong clipboard. Ở cuối, nó tự gọi MCP `notify_shift_complete` với kết quả của chính nó.

---

## 3. Điểm mở cần Architect quyết định

1. **`Alt+Enter` đọc tệp bàn giao thay cho `latest_dispatch.json`.** File đó không chứa trạng thái Gate. Bước kế tiếp được chọn như sau:
   - chưa có bàn giao → lời nhắn cho Claude;
   - Gate chưa đạt → lệnh Verify cho Claude Code;
   - Gate ALL PASS → mục 5 cho Gemini.
2. **Gợi ý ảnh dùng số thứ tự clipboard, không băm ảnh.** Băm phải đọc ~15 MB mỗi lần đánh thức.
3. **Phím số và ↑↓ chỉ chạy khi ô lệnh trống.** Phím 3 / 4 gọi Gemini (`/f`, token free tier, chỉ khi bấm).
4. **E4 có thể trượt vì `Activate()`, không phải vì đổi độ cao.** Chế độ gõ lệnh từng đo 28,7–79 ms với phím thật; ngưỡng cũ của đường pipe là < 35 ms (Gate 5). `lastWake` có phân rã để biết phần nào chậm. Nếu trượt, Architect chọn: nới ngưỡng E4, hay tách "mở 330 px" khỏi "giành focus".
5. **E5 CPU chấm đúng "= 0,0 %"** (chặt hơn < 0,5 % của các Gate trước). Chuỗi hồi quy mặc định `-SkipLive`.
6. **`is_selftest` trong đặc tả là cột `is_test`.**
7. **Số phiên bản**: đặc tả để ngỏ `v0.15.5` hoặc `v0.16.0`. ROADMAP chưa sửa, chờ Architect chốt.
8. **E1 `alliance.readUnder5ms` (ca Verify).** Lần đọc `review` của selftest là lần đầu tiến trình parse JSON, đo 1 mẫu lạnh: 8,7–10,3 ms. Các lần đọc khác 1,2–3,5 ms. Chọn: làm ấm trước khi đo, đo nhiều mẫu lấy P95, hay nới ngưỡng?
9. **E3 xem lịch sử P95 3,948 ms > 2,0.** Truy vấn chỉ 0,081 ms; phần chậm là hiển thị câu trả lời. Chọn: tối ưu dựng (ví dụ cache Inlines của 8 câu trả lời), hay nới ngưỡng?
10. **E4 WAKE P50 11,558 ms > 10,0.** Điểm 4 dự đoán sai chỗ chậm: `Activate` chỉ 0,074 ms, phần chậm là đổi độ cao + hiện cửa sổ (P95 10,304 ms). Chọn: tối ưu bước đổi độ cao từ 40 px, hay nới P50?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Architect quyết điểm 8–10, rồi TASK-16c sửa** *(khuyến nghị)* | Claude sửa theo quyết định (đo ấm/nhiều mẫu ở E1, cache dựng lịch sử ở E3, giảm chi phí đổi độ cao ở E4), rồi Claude Code chạy lại `Verify-Task16b.ps1` | Chức năng đều đúng; chỉ còn 3 ngưỡng thời gian. RAM và CPU đạt, hồi quy sạch |
| B. Chốt điểm mở 1–7 | Cùng lúc với A | Ảnh hưởng cách sửa |
| C. TASK-15 (`/ctx`, ADR-62) | Theo ROADMAP | Sau khi Gate 16b đóng |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 16b chưa PASS trên máy Nhật Hòa**: nhánh `claude/task-16b-cockpit-ui-v2-layout-lazy` @`4c87cc0`, lần chạy 2. **E2 và E5 PASS; E1, E3, E4 trượt ngưỡng thời gian.** Mọi chức năng chạy đúng. Chi tiết: `qa_report.md` §21.5, `gate16b_handoff.md` mục 2–3.
>
> Số đo trên máy:
> - **E1** ❌ build 0/0, Zero-BOM 119 file; selftest `ui-v2b` 14/15, trượt `alliance.readUnder5ms`: lần đọc `review` 8,7–10,3 ms (4 lần đo). Đây là lần đầu tiến trình parse JSON, 1 mẫu lạnh; các lần đọc khác 1,2–3,5 ms.
> - **E2** ✅ nền alpha 252 ở 330/640 px, xuyên thấu 0,6; markdown parse 4 KB P95 0,066 ms, dựng thật 2,2 ms.
> - **E3** ❌ chỉ trượt xem lịch sử: 24 lần P95 **3,948 ms** (ngưỡng 2,0), trong khi truy vấn DB chỉ 0,081 ms. Còn lại PASS: 0 dòng test, `Alt+Enter`, `Alt+A`, F1, phím 1, MCP shift 1,15 ms, gợi ý ảnh.
> - **E4** ❌ WAKE 40 → 330 px: P50 **11,558 ms** (ngưỡng 10), P95 13,554 ms (đạt ≤ 16). Phân rã P95: đổi độ cao + hiện 10,3 ms, focus 3,6 ms, `Activate` chỉ 0,07 ms. Rủi ro dự đoán "do `Activate`" là sai chỗ.
> - **E5** ✅ Private WS lắng **37,4 MB**, CPU rảnh **0 %**; chuỗi Task16 → 13 → 12 → 13a → `-FullRegression -SkipLive` (Task14 … Task03) đều PASS.
>
> Lần 1 dừng ở E1 vì selftest ghi khóa JSON rỗng `""` mà Windows PowerShell 5.1 không đọc được (CI dùng pwsh 7 nên không thấy). Đã vá ở `4c87cc0`, chỉ code selftest.
>
> Cần bạn quyết thêm:
> - (8) E1: làm ấm / đo nhiều mẫu / nới ngưỡng?
> - (9) E3: cache dựng câu trả lời lịch sử hay nới 2,0 ms?
> - (10) E4: tối ưu bước đổi độ cao từ 40 px hay nới P50 10 ms?
>
> Phần triển khai (không đổi so với bản trước):
>
> Đã làm:
> - **CUI-Opaque**: nền `#FC090D16` (99 %), xuyên thấu vẫn 60 %.
> - **CUI-Markdown**: parser trong Core, không Regex/NuGet + dựng Inlines. Runner: 4 KB P95 0,094 ms.
> - **CUI-Layout**: cột trái mặc định là lịch sử 8 câu trả lời thật (`is_test = 0`). Runner: truy vấn P95 0,049 ms.
> - **CUI-Lazy**: `Alt+Enter`, `1–9` khi ô lệnh trống, `Alt+I`, gợi ý ảnh mới mỗi lần đánh thức.
> - **CUI-Alliance**: `Alt+A`.
> - **MCP `notify_shift_complete`**: chỉ báo ở thanh 40 px.
> - **WAKE 40 → 330 px**: ghi kèm phân rã.
>
> Cần bạn quyết:
> - (1) `Alt+Enter` đọc `current_task.md` + `gate<N>_handoff.md` thay cho `latest_dispatch.json` (file đó không có trạng thái Gate);
> - (2) gợi ý ảnh dùng số thứ tự clipboard, không băm ảnh (băm phải đọc ~15 MB mỗi lần đánh thức);
> - (3) phím số / ↑↓ chỉ khi ô lệnh trống, phím 3/4 gọi Gemini khi người dùng bấm;
> - (4) **E4 có rủi ro**: chế độ gõ lệnh vẫn gọi `Activate()`, lịch sử 28,7–79 ms với phím thật, ngưỡng pipe cũ < 35 ms; nếu trượt thì nới ngưỡng hay tách "mở 330 px" khỏi "giành focus";
> - (5) E5 CPU chấm đúng 0,0 %, chuỗi hồi quy Task16 → Task13 → Task12 → Task13a → Task14 → … → Task03 với `-SkipLive`;
> - (6) `is_selftest` = cột `is_test`;
> - (7) chốt `v0.15.5` hay `v0.16.0`.
>
> Bước tiếp đề xuất: bạn quyết điểm 1–10; Claude sửa 3 ngưỡng theo quyết định, rồi Claude Code chạy lại `Verify-Task16b.ps1`.
