# 📦 HỒ SƠ BÀN GIAO — Gate 16b (TASK-16b: Cockpit UI v2 Phase 2 — nền đặc, markdown, lịch sử hỏi–đáp, chế độ lười, bảng liên minh, ADR-66)

- **Ca thực thi**: Claude (ca viết code), 2026-11-10 tối
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-16b-cockpit-ui-v2-layout-lazy`**, tách từ `main` @`76158b1` (`v0.15.0`)
- **Đặc tả**: `current_task.md` (TASK-16b). Chi tiết kỹ thuật: **`qa_report.md` §21**
- **Trạng thái build**: **chưa build trên máy Nhật Hòa**. CI GitHub Actions xanh trên `229e750` (đầu nhánh lúc bàn giao: build `-warnaserror`, Zero-BOM, PS 5.1 parse, selftest `ui-v2b` PASS trên runner). **Chưa có số đo RAM / CPU / WAKE của máy.**

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

Không đụng `main` hay tag, không có thao tác Cấp Đỏ, không hotkey toàn cục mới, không timer lặp mới, không gọi API trả phí.

---

## 2. Trạng thái Gate 16b — ⏳ chưa đủ bằng chứng (chờ chạy trên máy)

| # | Bằng chứng | Trạng thái |
|---|------------|-----------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ui-v2b` | CI xanh (runner); **chờ máy** |
| E2 | nền alpha ≥ `0xF8` ở 330 / 640 px; markdown (selftest + hiển thị thật) | **chờ máy** (runner: parse 4 KB P95 0,094 ms) |
| E3 | lịch sử < 2 ms, không bản ghi test; `Alt+Enter` / `Alt+A` / F1 / phím 1; MCP `notify_shift_complete` < 2 ms; gợi ý ảnh | **chờ máy** (runner: truy vấn lịch sử P95 0,049 ms) |
| E4 | WAKE 40 → 330 px + focus, 10 mẫu: P50 ≤ 10, P95 ≤ 16 ms | **chờ máy** (rủi ro, xem mục 3) |
| E5 | Private WS lắng ≤ 45,0 MB (trần 52), CPU rảnh 0,0 %, chuỗi hồi quy Task16 → Task03, git | **chờ máy** |

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

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 16b trên máy** *(khuyến nghị)* | Claude Code chạy lệnh ở mục 2, ghi kết quả vào `qa_report.md` §21.5 và mục 2 của file này | Cần số RAM, CPU, WAKE của máy; E4 là chỗ rủi ro nhất |
| B. Chốt điểm mở 1–7 | Đặc biệt điểm 4 nếu E4 trượt | Ảnh hưởng cách sửa |
| C. TASK-15 (`/ctx`, ADR-62) | Theo ROADMAP | Sau khi Gate 16b đóng |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **TASK-16b (ADR-66) đã triển khai xong** trên `claude/task-16b-cockpit-ui-v2-layout-lazy` (tách từ `main` @`76158b1`). CI xanh: build `-warnaserror`, Zero-BOM, selftest `ui-v2b` (15 kiểm) PASS trên runner. **Chưa build và chưa chạy Verify trên máy Nhật Hòa**, nên chưa có số RAM/CPU/WAKE của máy. Chi tiết: `qa_report.md` §21, `gate16b_handoff.md`.
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
> Bước tiếp: Claude Code chạy `Verify-Task16b.ps1` trên máy.
