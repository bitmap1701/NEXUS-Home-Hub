# 📦 HỒ SƠ BÀN GIAO — Gate 15 (TASK-15: `/ctx` Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist, ADR-62)

- **Ca thực thi**: Claude (ca viết code), 2026-11-10 khuya; Claude Code trên máy Nhật Hòa (ca chạy Verify), 2026-11-10 khuya
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-15-active-window-ctx-safeguard`**, tách từ `main` @`939da6b` (`v0.15.5`)
- **Đặc tả**: `current_task.md` (TASK-15). Chi tiết kỹ thuật: **`qa_report.md` §22**
- **Trạng thái build**: **đã build và chạy Verify trên máy Nhật Hòa** (`f997075`, lần chạy 1): build `-warnaserror` 0/0; **E1–E3 PASS; E4 trượt WAKE P95; E5 trượt chuỗi hồi quy** (chi tiết mục 2). CI xanh.

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

## 2. Trạng thái Gate 15 — ❌ chưa PASS (máy Nhật Hòa, `f997075`, lần chạy 1): E1–E3 PASS; E4 trượt ngưỡng WAKE P95; E5 trượt chuỗi hồi quy — dừng theo cầu chì

| # | Bằng chứng | Trạng thái |
|---|------------|-----------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ctx-safeguard` (ca biên) | ✅ build 0/0, Zero-BOM 123 file 0 vi phạm, selftest pass |
| E2 | 10 + 10 mẫu bị chặn 100 %, P95 ≤ 0,5 ms, 0 token, không lọt tiêu đề vào SQLite / WAL / `latest_dispatch.json` | ✅ 10/10 + 10/10 bị chặn, P95 **0,008 / 0,013 ms**; `vgc` đang chạy → `anticheat-running`; dấu hiệu không có trong `latest_dispatch.json`, SQLite, WAL; 0 tin nhắn gửi đi |
| E3 | `/ctx status` và `/ctx <câu hỏi>` trên cửa sổ thường, 25 cache hit P95 ≤ 3,0 ms, MCP ≤ 2,0 ms | ✅ cửa sổ thật `OK` (lần quét thật đầu tiên 9,0 ms, khiên 10,7 ms); clipboard 24 / 1 000 / bí mật 0 ký tự; 25/25 cache hit P95 **2,172 ms**; MCP ấm P95 0,085 ms (lạnh 15,8 ms, chỉ ghi lại) |
| E4 | WAKE 40 → 330 px P50 ≤ 10 / P95 ≤ 16 ms; lịch sử không có dòng test | ❌ P50 7,566 ✅ / **P95 28,126 ms** ❌. Mẫu đầu **44,17 ms**, 9 mẫu còn lại 6,88–8,52 ms. Verify-Task15 không ghi phân rã WAKE. Cùng lượt chạy, E4 của Verify-Task16b đạt (P50 7,85 / P95 9,57 ms). Lịch sử: 0 dòng `/ctx` test ✅ |
| E5 | Private WS lắng ≤ 45,0 MB, CPU rảnh 0,0 %, chuỗi Verify-Task16b (→ Task16 … Task03), git | ❌ **38,1 MB** ✅, CPU **0 %** ✅, git ✅; **chuỗi Verify-Task16b exit 1** ❌ vì 2 lỗi không thuộc code TASK-15 (xem dưới) |

**Hai lỗi trong chuỗi hồi quy (không do code TASK-15):**
1. `Verify-Task16b.ps1:319` so cứng `TaskId -eq 'TASK-16b'`, nên `altEnterCopies` không thể đạt trên nhánh TASK-15. Đây là lỗi script; phần còn lại của `Alt+Enter` đều đúng.
2. **`-SkipLive` không chặn được Gemini thật.** `Verify-Task11.ps1:347` gọi Task10 không kèm `-SkipLive`, nên Task10 → 09 → 08 → 06 vẫn gọi Gemini thật. Lần này `/qa` của Task10 (#9007) **hết giờ 25 s** phía Gemini. Gate 16b lần 2 và 3 cũng đã gọi thật (#7664, #7699, #8311, #8346), nên câu "0 token" trong hồ sơ Gate 16b không đúng.

**Dừng ở lần 1, không vá, không chạy lại**: E4 là trượt ngưỡng. Vá 2 lỗi script rồi chạy lại cũng chỉ là thử lại cho tới khi mẫu lạnh của E4 biến mất. Phân tích đầy đủ: `qa_report.md` §22.4.

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
6. **E4 WAKE P95 28,1 ms do một mẫu lạnh 44,17 ms** (ca Verify): mẫu đầu ngay sau E3, còn 9 mẫu khác 6,9–8,5 ms. Chọn một trong:
   - (a) Verify-Task15 ghi phân rã WAKE như Verify-Task16b rồi chạy lại để tìm phần chậm;
   - (b) thêm 1 lần làm ấm trước 10 mẫu (đổi ý nghĩa phép đo);
   - (c) coi lần đánh thức đầu sau `/ctx` là chi phí thật cần sửa trong code.
7. **Chuỗi hồi quy** (ca Verify):
   - (a) sửa `Verify-Task16b.ps1:319` để đọc task hiện hành thay vì so cứng `TASK-16b`;
   - (b) để `-SkipLive` truyền xuống Task10 → 09 → 08 → 06. Nếu cần, thêm `-SkipLive` cho các script cũ.

   Duyệt cả hai? (b) cũng làm câu "0 token" trong các hồ sơ Gate trở nên đúng.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Ca Claude: vá 2 lỗi chuỗi hồi quy + ghi phân rã WAKE trong Verify-Task15** *(khuyến nghị)* | Điểm 7a, 7b và 6a. Không đổi ngưỡng. Rồi Claude Code chạy lần 2 | Hai lỗi script chắc chắn làm E5 trượt trên mọi nhánh sau TASK-16b; phân rã WAKE cho biết mẫu 44 ms chậm ở đâu |
| B. Architect quyết điểm 1–7 | Trước hoặc cùng A | Điểm 6 quyết cách xử lý E4 |
| C. Phase 4 (giọng nói, PTT FIFINE K669, `/net`) | Theo ROADMAP | Sau khi Gate 15 đóng |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 15 (TASK-15, ADR-62) chưa PASS trên máy Nhật Hòa**: lần chạy 1, `f997075`. **E1–E3 PASS; E4 và E5 trượt.** Claude Code dừng theo cầu chì, không vá, không chạy lại. Chi tiết: `qa_report.md` §22.4, `gate15_handoff.md` mục 2–3.
>
> - **E1** ✅ build 0/0, Zero-BOM 123 file, selftest `ctx-safeguard` pass.
> - **E2** ✅ 10/10 chống gian lận + 10/10 nhạy cảm bị chặn, P95 0,008 / 0,013 ms; `vgc` đang chạy → `anticheat-running`; tiêu đề không lọt vào `latest_dispatch.json` / SQLite / WAL.
> - **E3** ✅ `/ctx` trên cửa sổ thật OK (lần quét thật đầu 9,0 ms); clipboard ≤ 1 000 ký tự, bí mật không gửi; 25/25 cache hit P95 2,17 ms; MCP ấm P95 0,085 ms.
> - **E4** ❌ WAKE P50 7,57 ms ✅ nhưng **P95 28,1 ms** (ngưỡng 16): mẫu đầu ngay sau E3 mất **44,17 ms**, 9 mẫu còn lại 6,9–8,5 ms. Verify-Task15 không ghi phân rã nên chưa biết phần chậm. Cùng lượt chạy, E4 của Verify-Task16b đạt (P95 9,57 ms).
> - **E5** ❌ RAM rảnh 38,1 MB ✅, CPU 0 % ✅, nhưng chuỗi Verify-Task16b trượt vì 2 lỗi không thuộc code TASK-15:
>   - (a) `Verify-Task16b.ps1:319` so cứng `TASK-16b`, nên `altEnterCopies` không đạt được trên nhánh khác;
>   - (b) `-SkipLive` không truyền từ Task11 xuống Task10 → 06, nên chuỗi vẫn gọi Gemini thật. Lần này `/qa` của Task10 hết giờ 25 s. Gate 16b lần 2–3 cũng đã gọi thật, nên câu "0 token" trong hồ sơ Gate 16b là sai.
>
> Cần bạn quyết thêm:
> - (6) E4: ghi phân rã WAKE rồi chạy lại / thêm làm ấm / coi là chi phí thật cần sửa?
> - (7) duyệt vá 2 lỗi chuỗi hồi quy (đọc task hiện hành thay vì so cứng; truyền `-SkipLive` xuống tới Task06)?
>
> Phần triển khai (không đổi so với bản trước):
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
> Bước tiếp đề xuất: bạn quyết điểm 1–7; ca Claude vá 7a/7b và ghi phân rã WAKE (6a), không đổi ngưỡng; rồi Claude Code chạy lần 2.
