# 📦 HỒ SƠ BÀN GIAO — Gate 15 (TASK-15: `/ctx` Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist, ADR-62)

- **Ca thực thi**: Claude (ca viết code), 2026-11-10 khuya
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-15-active-window-ctx-safeguard`**, tách từ `main` @`939da6b` (`v0.15.5`)
- **Đặc tả**: `current_task.md` (TASK-15). Chi tiết kỹ thuật: **`qa_report.md` §22**
- **Trạng thái build**: **chưa build trên máy Nhật Hòa**. CI xanh (build `-warnaserror`, Zero-BOM, selftest `ctx-safeguard` 24/24 trên runner). **Chưa có số đo RAM / CPU / WAKE của máy.**

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Ngữ cảnh cửa sổ | Không có | `/ctx status` (xem NEXUS đang thấy gì, 0 token), `/ctx <câu hỏi>` (gửi kèm app + tiêu đề + chữ vừa copy, ≤ 1 200 ký tự) |
| 2 | Khiên chống gian lận | Chỉ Combat Shield của `/snap` (quét bằng `Process.GetProcesses`) | Tiến trình cửa sổ đích (quyền truy vấn tối thiểu) + Toolhelp32 tên tiến trình đang chạy (dùng lại 2 s); không đọc được tên = chặn |
| 3 | Khiên cửa sổ nhạy cảm | Privacy Guard của `/snap` | Denylist 25 từ khóa trên tên tiến trình + tiêu đề; chỉ báo loại, tiêu đề bị bỏ, không lưu ở đâu |
| 4 | Cache | — | Hỏi lại trên cùng cửa sổ = cache hit (vân tay `CTX_V1`), 0 token |
| 5 | MCP | — | `get_active_window_context` (JSON, 0 token) |
| 6 | Luồng đánh thức | `_previousForeground` = `GetForegroundWindow()` | **Không đổi** (đã đúng yêu cầu "chỉ một lời gọi"), nên WAKE của Gate 16b không bị chạm |
| 7 | ROADMAP / ADR | — | TASK-16b `v0.15.5` (ADR-66), TASK-15 `v0.16.0` (ADR-62) |

Không đụng `main` hay tag, không thao tác Cấp Đỏ, không timer mới, không gọi API trả phí.

---

## 2. Trạng thái Gate 15 — ⏳ chưa đủ bằng chứng (chờ chạy trên máy)

| # | Bằng chứng | Trạng thái |
|---|------------|-----------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ctx-safeguard` (ca biên) | CI xanh (runner); **chờ máy** |
| E2 | 10 + 10 mẫu bị chặn 100 %, P95 ≤ 0,5 ms, 0 token, không lọt tiêu đề vào SQLite / WAL / `latest_dispatch.json` | **chờ máy** (runner: P95 0,006 / 0,002 ms, không lọt) |
| E3 | `/ctx status` và `/ctx <câu hỏi>` trên cửa sổ thường, 25 cache hit P95 ≤ 3,0 ms, MCP ≤ 2,0 ms | **chờ máy** (runner: cache hit P95 1,65 ms) |
| E4 | WAKE 40 → 330 px P50 ≤ 10 / P95 ≤ 16 ms; lịch sử không có dòng test | **chờ máy** |
| E5 | Private WS lắng ≤ 45,0 MB, CPU rảnh 0,0 %, chuỗi Verify-Task16b (→ Task16 … Task03), git | **chờ máy** |

### Lệnh chạy (Claude Code trên máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-15-active-window-ctx-safeguard
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task15.ps1
```
- Chạy bằng Windows PowerShell 5.1 (STA) để kiểm được clipboard.
- Trong lúc chạy: không chạy lệnh git, không bấm vào HUD, không copy/paste, để yên chuột.
- E5 gọi `Verify-Task16b.ps1`, cái này lại chạy cả chuỗi Task16 → … → Task03, nên lâu (lần trước khoảng 15–20 phút). `-FastE5` chỉ để thử nhanh.
- Gemini chạy mock, 0 token. Verify tắt các móc giả lập (`CTXSIM off`) và khôi phục `latest_dispatch.json`, `cockpit_ui.json`, chữ trong clipboard.
- Nếu máy đang chạy một tiến trình chống gian lận (ví dụ Vanguard `vgc`), phép thử trên cửa sổ thật sẽ báo `anticheat-running`. Đó là hành vi đúng và được tính đạt.

---

## 3. Điểm mở cần Architect quyết định

1. **Lần quét tiến trình đầu tiên mất vài ms** (runner 4,4 ms), sau đó dùng lại 2 s. Cửa sổ game / chống gian lận vẫn chặn trong µs. Nhưng cửa sổ nhạy cảm gặp lúc chưa có bản quét thì bị chặn sau vài ms, vượt 0,5 ms. Giữ thứ tự của đặc tả (quét hết rồi mới đọc tiêu đề), hay so tiêu đề trước để chặn nhạy cảm luôn nhanh?
2. **Game nằm trong danh sách "đang chạy"**: một game trong danh sách mở nền thì `/ctx` từ chối mọi cửa sổ. Giữ, hay chỉ áp cho dịch vụ chống gian lận?
3. **"Vừa copy"** = clipboard đổi kể từ câu `/ctx` trước; `--no-clip` để bỏ.
4. **Gợi ý ảnh của TASK-16b** đọc số thứ tự clipboard sau khi đo WAKE (không đọc nội dung). Có tính là "đọc Clipboard trong luồng WAKE" không?
5. **Từ khóa rộng** (`secret`, `password`) chặn cả tab code như `PasswordHasher.cs`. Chấp nhận theo đặc tả?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 15 trên máy** *(khuyến nghị)* | Claude Code chạy lệnh ở mục 2, ghi kết quả vào `qa_report.md` §22.4 và mục 2 của file này | Cần số máy, nhất là lần quét Toolhelp32 thật và WAKE |
| B. Architect quyết điểm 1–5 | Song song với A | Điểm 1 ảnh hưởng thứ tự khiên |
| C. Phase 4 (giọng nói, PTT FIFINE K669, `/net`) | Theo ROADMAP | Sau khi Gate 15 đóng |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **TASK-15 (ADR-62) đã triển khai xong** trên `claude/task-15-active-window-ctx-safeguard` (tách từ `main` @`939da6b`, `v0.15.5`). CI xanh: build `-warnaserror`, Zero-BOM, selftest `ctx-safeguard` **24/24** trên runner. **Chưa build và chưa chạy Verify trên máy Nhật Hòa.** Chi tiết: `qa_report.md` §22, `gate15_handoff.md`.
>
> Đã làm:
> - **`/ctx status`** (0 token) và **`/ctx <câu hỏi>`** (khối ≤ 1 200 ký tự, cache hit theo vân tay `CTX_V1`).
> - **MCP `get_active_window_context`**.
> - **Khiên 1**: tên tiến trình qua `PROCESS_QUERY_LIMITED_INFORMATION`; danh sách đang chạy bằng Toolhelp32, dùng lại 2 s; không đọc được tên thì chặn.
> - **Khiên 2**: denylist trên tên tiến trình + tiêu đề, chỉ báo loại, tiêu đề bị bỏ.
> - **Luồng WAKE không đổi**: `GetForegroundWindow` đã được lưu từ Gate 4.
> - ROADMAP: `v0.15.5` = TASK-16b, `v0.16.0` = TASK-15.
>
> Runner: chặn P95 0,006 / 0,002 ms; không lọt tiêu đề vào `latest_dispatch.json` / SQLite / WAL; 0 token; 25 cache hit P95 1,65 ms.
>
> Cần bạn quyết:
> - (1) lần quét tiến trình đầu tiên mất vài ms (runner 4,4 ms, sau đó dùng lại 2 s): giữ thứ tự đặc tả (quét hết rồi mới đọc tiêu đề), hay so tiêu đề trước để chặn nhạy cảm luôn < 0,5 ms;
> - (2) game trong danh sách đang mở nền cũng chặn mọi `/ctx`: giữ hay chỉ áp cho dịch vụ chống gian lận;
> - (3) "vừa copy" = clipboard đổi kể từ câu `/ctx` trước;
> - (4) gợi ý ảnh của TASK-16b đọc số thứ tự clipboard sau WAKE (không đọc nội dung): có tính là vi phạm không;
> - (5) từ khóa rộng như `password` chặn cả tab code — chấp nhận?
>
> Bước tiếp: Claude Code chạy `Verify-Task15.ps1` trên máy.
