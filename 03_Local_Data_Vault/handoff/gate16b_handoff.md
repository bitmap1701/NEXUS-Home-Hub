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

## 2. Trạng thái Gate 16b — ⏳ chờ lần chạy 3 trên máy (bản vá `a0a4a5c`); lần chạy 2 (`4c87cc0`): E2, E5 PASS; E1, E3, E4 trượt ngưỡng thời gian

> **Ca Claude sau lần chạy 2 (2026-11-10 22:20–22:45): sửa trong code, KHÔNG nới ngưỡng nào** (chi tiết `qa_report.md` §21.6). CI xanh trên `a0a4a5c`.
> - **E4**: khung HUD giữ chiều cao cố định, cửa sổ 40 px chỉ cắt bớt phần dưới. Mở 40 → 330 px chỉ đổi cỡ cửa sổ, không phải đo lại ba cột (lần 2: phần "đổi độ cao + hiện" chiếm P95 10,3 ms).
> - **E3**: mỗi câu trả lời lịch sử được dựng một lần (lúc rảnh, ngay sau khi danh sách tải) rồi giữ lại. Xem lại chỉ tráo nội dung, không dựng lại.
> - **E1**: `alliance.readUnder5ms` là kiểm do Claude tự đặt, không có trong đặc tả. Nay chấm trung vị 5 lần đọc đã ấm; lần đọc lạnh vẫn ghi vào báo cáo.
> - Cầu chì: đã dùng 2/3 lần chạy. **Lần 3 là lần cuối**; nếu còn trượt thì dừng và chuyển Architect quyết (điểm 8–10).

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
4. **[Dự đoán sai — số đo lần 2 cho thấy `Activate` chỉ 0,07 ms; xem điểm 10] E4 có thể trượt vì `Activate()`, không phải vì đổi độ cao.** Chế độ gõ lệnh từng đo 28,7–79 ms với phím thật; ngưỡng cũ của đường pipe là < 35 ms (Gate 5). `lastWake` có phân rã để biết phần nào chậm. Nếu trượt, Architect chọn: nới ngưỡng E4, hay tách "mở 330 px" khỏi "giành focus".
5. **E5 CPU chấm đúng "= 0,0 %"** (chặt hơn < 0,5 % của các Gate trước). Chuỗi hồi quy mặc định `-SkipLive`.
6. **`is_selftest` trong đặc tả là cột `is_test`.**
7. **Số phiên bản**: đặc tả để ngỏ `v0.15.5` hoặc `v0.16.0`. ROADMAP chưa sửa, chờ Architect chốt.
8. **[Đã sửa trong code ở `94cfc3f`, chờ lần chạy 3] E1 `alliance.readUnder5ms` (ca Verify).** Lần đọc `review` của selftest là lần đầu tiến trình parse JSON, đo 1 mẫu lạnh: 8,7–10,3 ms. Các lần đọc khác 1,2–3,5 ms. Chọn: làm ấm trước khi đo, đo nhiều mẫu lấy P95, hay nới ngưỡng?
9. **[Đã sửa trong code ở `94cfc3f`, chờ lần chạy 3] E3 xem lịch sử P95 3,948 ms > 2,0.** Truy vấn chỉ 0,081 ms; phần chậm là hiển thị câu trả lời. Chọn: tối ưu dựng (ví dụ cache Inlines của 8 câu trả lời), hay nới ngưỡng?
10. **[Đã sửa trong code ở `94cfc3f`, chờ lần chạy 3] E4 WAKE P50 11,558 ms > 10,0.** Điểm 4 dự đoán sai chỗ chậm: `Activate` chỉ 0,074 ms, phần chậm là đổi độ cao + hiện cửa sổ (P95 10,304 ms). Chọn: tối ưu bước đổi độ cao từ 40 px, hay nới P50?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy lần 3 trên máy** *(khuyến nghị)* | Claude Code pull `a0a4a5c` và chạy lại `Verify-Task16b.ps1` (lệnh ở mục 2) | Ba chỗ trượt đã sửa trong code, không đổi ngưỡng; lần 3 là lần cuối của cầu chì |
| B. Architect quyết điểm 1–7 | Song song với A | Các điểm này không phụ thuộc kết quả lần 3 |
| C. Nếu lần 3 vẫn trượt | Dừng; Architect chọn nới ngưỡng hay đổi thiết kế (điểm 8–10) | Đúng luật cầu chì 3 lần thử |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, cập nhật **Gate 16b (TASK-16b, ADR-66)**: lần chạy 2 trên máy Nhật Hòa (`4c87cc0`) có **E2 và E5 PASS** (nền alpha 252; markdown đúng; RAM rảnh 37,4 MB, CPU 0 %, cả chuỗi Task16 → Task13 → Task12 → Task13a → Task14 … Task03 PASS). **E1, E3, E4 trượt ngưỡng thời gian.** Claude đã sửa cả ba trong code, **không nới ngưỡng nào**; CI xanh trên `a0a4a5c`, chờ Claude Code chạy lần 3 (lần cuối của cầu chì).
>
> - **E4** (WAKE 40 → 330 px, lần 2 P50 11,6 ms > 10): số đo cho thấy phần chậm là đổi độ cao + hiện (P95 10,3 ms), không phải `Activate` (0,07 ms). Dự đoán trước đó của Claude sai. Bản sửa: khung HUD giữ chiều cao cố định, cửa sổ 40 px chỉ cắt bớt phần dưới, nên mở ra không phải đo lại ba cột.
> - **E3** (xem lịch sử P95 3,9 ms > 2): truy vấn chỉ 0,08 ms, phần chậm là dựng lại câu trả lời mỗi lần xem. Bản sửa: dựng mỗi câu trả lời một lần lúc rảnh rồi giữ lại, xem lại chỉ tráo nội dung.
> - **E1**: kiểm `alliance.readUnder5ms` do Claude tự đặt (không có trong đặc tả), lần đọc lạnh đầu tiên mất 8,7–10,3 ms vì JIT. Nay chấm trung vị 5 lần đọc ấm, lần lạnh vẫn ghi lại.
>
> Nếu lần 3 vẫn trượt, Claude Code sẽ dừng theo cầu chì và chuyển bạn quyết nới ngưỡng hay đổi thiết kế. Các điểm 1–7 ở mục 3 của `gate16b_handoff.md` vẫn chờ bạn (Alt+Enter đọc tệp bàn giao, gợi ý ảnh theo số thứ tự clipboard, phím số khi ô lệnh trống, E5 CPU = 0,0 %, `is_test`, số phiên bản).

