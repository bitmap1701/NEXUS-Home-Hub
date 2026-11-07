# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 8 (TASK-08) & Nhận TASK-09

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-08-gemini-qa-loop`, tách từ `main` (`86520e9`, sau tag `v0.7.0`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-08, đã có trên `main`)
- **Chi tiết kỹ thuật**: `qa_report.md` §10

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Đo `/ask` | Chỉ có tổng thời gian | `AskSyncBreakdown`: `GuardMs`, `DispatchAndDbMs`, `TaskSpawnMs`, `TotalSyncMs` (cả `/qa`) |
| 2 | Cold-start | Lần `/ask` đầu: 72 ms (E4 Gate 7), một lần 2,9 s (E3 Gate 7) | `NexusRuntime.WarmUp()` chạy nền khi HostService khởi động (SQLite, regex, JSON; không DPAPI/HTTP) |
| 3 | Regex | 4 chỗ `RegexOptions.Compiled` | 0 (thay bằng `CultureInvariant`) |
| 4 | `HttpClient` | Tĩnh, tạo khi khởi động mọi tiến trình | `Lazy<HttpClient>` chỉ khi gửi request thật; `UseProxy = false`, `PooledConnectionLifetime = 2 phút` |
| 5 | QA | Người dùng tự gõ `/pass`, `/fail` | `/qa [#id] [bằng chứng]`, `/ask --auto-qa`, `autoQaAfterAsk`: QA Agent của squad chấm bằng Gemini rồi tự chốt `PASSED`/`NEEDS_WORK`/`ESCALATED` |
| 6 | Dữ liệu | — | Cột `qa_response`, `qa_latency_ms`; `latest_dispatch.json` thêm `qaResponse`, `qaLatencyMs` |
| 7 | Giao diện | Khung AI RESPONSE | Thêm khung QA REVIEW và nhãn `[QA REVIEWING... <agent>]` |
| 8 | MCP | 9 tool | + `run_gemini_qa_review` (Cấp Vàng) |
| 9 | Kiểm thử | — | Selftest `schema08`, `ask-cold`, `autoqa`, `qa-strikes`, `qa-missingkey`; `Verify-Task08.ps1` (E1–E5) |

---

## 2. Trạng thái Gate 8 — ✅ E1–E5 ALL PASS (lần chạy 3)

- **Lần 1** (22:47, `10d8deb`): E1, E4 PASS; E2 (Cold Call #1 409 ms), E3 (`/qa` lần đầu 10,19 ms), E5 (Task04 E6 đo CPU một cửa sổ 3 s) trượt.
- **Lần 2** (22:53, `c1e6f9a`): tách E2/E3 khỏi lúc HostService khởi động → chỉ còn E2 trượt (Cold Call #1 55 ms, 47 ms trong `Dispatch`).
- **Lần 3** (22:58, `c1d7e10`, có build): **ALL CHECKS PASS (E1–E5)**. Cold Call #1 **9,43 ms**, P95 **2,19 ms**, `/qa` ≤ 4,2 ms, MCP → Cockpit 164 ms, private WS **44,2 MB**, hồi quy 03–07 ALL PASS.
- Chưa fast-forward `main` (chờ Gemini nghiệm thu). Gọi Gemini thật vẫn SKIPPED (chưa có khóa). Chi tiết: qa_report §10.4–10.6.

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-08-gemini-qa-loop
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task08.ps1
```
Không cần `git stash` lần này (đặc tả đã nằm trên `main`). Hồi quy đầy đủ (Verify-Task07 → 06 → 05 → 04 → 03) mất khoảng 5–6 phút. Biến mock chỉ được đặt cho tiến trình con của script, không bao giờ cho HostService chạy lâu dài.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + `--selftest schema08` + quét mã | `-warnaserror` 0/0; có `qa_response`, `qa_latency_ms`, số dòng trước = sau; 0 lần `RegexOptions.Compiled` trong `.cs` | ✅ 0/0; 763 → 763 dòng; 0 lần |
| E2 | `--selftest ask-cold` (mock) | Cold Call #1 `TotalSyncMs` < 25 ms (LiveCombat 40); P95 10 lần sau < 10 ms (LiveCombat 25); in đủ 4 thành phần; chạy mock không tạo `HttpClient`. In thêm số đo không warm-up để tham khảo | ✅ Cold #1 9,43 ms (dispatch 1,73 + file 2,98 + spawn 1,30); P95 2,19 ms; không tạo `HttpClient`; không warm-up: 113,7 ms |
| E3 | `--selftest autoqa`, `qa-strikes`, `qa-missingkey` | 3a: `/ask --auto-qa Thiết kế gói tin UDP có seqId` → `PASSED`, có cả `ai_response` lẫn `qa_response` trong SQLite và `latest_dispatch.json`; 3b: `/qa` ×3 với `NEEDS_WORK` → r1, r2, `ESCALATED` r3, đúng 1 dòng escalation, phần đồng bộ `/qa` < 10 ms; 3c: không khóa → `[LOCAL QA SKIPPED]`, không đổi trạng thái | ✅ #786 PASSED; #787 r1, r2, ESCALATED r3, 1 dòng, `/qa` ≤ 4,2 ms; #788 bỏ qua, r0 |
| E4 | MCP `run_gemini_qa_review` + pipe `SYNCSTATE` | MCP trả `PASSED`; Cockpit đang chạy nhận `qaResponse` mới, độ trễ < 300 ms | ✅ #789 PASSED, 164,4 ms |
| E5 | RAM + hồi quy | Private WS của HostService < 60 MB (mục tiêu 45–54); Verify-Task07/06/05/04/03 ALL PASS | ✅ 44,2 MB; hồi quy ALL PASS (CPU 0,13 %) |

Kết quả gốc: `handoff/task08_verification.json`, `handoff/task08_selftest_*.json`.

**Kiểm tay nên làm (khi có khóa Gemini)**: trong game `Ctrl+Shift+Space` → `/ask --auto-qa Thiết kế gói tin UDP có seqId cho Delta Force overlay` → Enter: bàn phím về game ngay; thẻ trên MSI G272 lần lượt hiện THINKING → AI RESPONSE → `[QA REVIEWING... Reality Checker]` → khung QA REVIEW và trạng thái mới.

---

## 3. Điểm mở cần Architect quyết định

1. **Kết luận về 2,9 s / 409 ms** (qa_report §10.6): (a) tranh chấp với quá trình khởi động của HostService chạy song song, (b) chi phí chạy lần đầu (JIT định tuyến/registry ~76 ms, persona, INSERT đầu tiên) — nay được `WarmUp()` trả trước; **không phải DPAPI/`HttpClient`**. Đề nghị ghi lại ADR-28 theo số đo này.
2. **Cold Call #1 đo sau warm-up và khi không có tiến trình NEXUS nào đang khởi động** (giống HostService thật); số đo không warm-up (113,7 ms) chỉ in để tham khảo. Warm-up ghi 1 dòng audit `host_warmup` mỗi lần khởi động. Chấp nhận?
3. **QA không tính strike khi lỗi API/mạng/timeout** (thêm ngoài đặc tả). Nếu không, mạng chập chờn có thể đẩy dispatch sang `ESCALATED`.
4. **Không có dòng `VERDICT:`** → `NEEDS_WORK` và tính 1 strike (theo "nguyên tắc Reality Checker" của đặc tả). Có muốn coi đó là "QA không chạy" thay vì strike?
5. **`run_gemini_qa_review` là Cấp Vàng** (đổi `status`). Đặc tả không ghi cấp.
6. **Hai bài kiểm thử cũ được sửa cách đo**: Verify-Task04 E6 đo CPU 10 × 1 s, chấm 5 giây cuối (như Task05 E7); Verify-Task08 chỉ khởi động HostService trước E4.
7. **Commit tài liệu Gate 7 (`11d6785`) không có trong `main`**: lần fast-forward `main` tới `f9a173e` đã bỏ qua nó. Nhánh này mang theo (`316ed18`), nên sẽ vào `main` cùng TASK-08.
8. **Chi phí Gemini của `--auto-qa`**: mỗi `/ask --auto-qa` gọi 2 lần (Primary + QA). Có cần giới hạn số lần gọi/phút trong `nexus_config.json`?

---

## 4. Đề xuất TASK-09

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Lần chạy thật với khóa + hội thoại nhiều lượt** *(khuyến nghị)* | Kiểm thử E2E với khóa thật (đo `ai_latency_ms`, `qa_latency_ms`, xử lý 429); `/ask #id <tiếp>` gửi kèm lượt trước (giới hạn token); `/retry #id` để Primary sửa theo nhận xét QA rồi QA chấm lại (vòng Dev↔QA thật sự, dừng ở strike 3); giới hạn số lần gọi/phút. | Đến giờ mọi bài kiểm thử Gemini đều chạy bằng mock. Vòng `/retry` biến cầu chì 3 lần thử thành quy trình tự sửa đúng nghĩa. |
| B. Giọng nói (PCM 16 kHz + VAD) → `/ask` | Mic → Gemini Live → lệnh Cockpit. | Cần đo CPU/âm thanh khi chơi; sau A. |
| C. Dọn giao diện & hiệu năng nền | Đỉnh CPU ~6 s, RAM private trên thanh trạng thái, chữ bị cắt, MFD lỗi thời. | Nhỏ, có thể gộp vào A. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Gate 8 (TASK-08) đã đủ bằng chứng: trên máy Nhật Hòa, **Verify-Task08 ALL CHECKS PASS (E1–E5)** ở lần chạy 3 (nhánh `claude/task-08-gemini-qa-loop`, commit `c1d7e10`, có build `-warnaserror` 0/0; chi tiết `gate8_handoff.md`, `qa_report.md` §10.4–10.6). Số liệu: 0 `RegexOptions.Compiled`; 2 cột `qa_*` giữ 763/763 dòng; **Cold Call #1 của `/ask` = 9,43 ms** (guard 0,02 / dispatch 1,73 [persona 0,20, insert 0,55, build 0,02] / `latest_dispatch.json` 2,98 / spawn 1,30), **P95 = 2,19 ms**; `/ask --auto-qa` → Reality Checker `PASSED`; `/qa` ×3 → `ESCALATED` r3, đúng 1 dòng escalation, phần đồng bộ ≤ 4,2 ms; không khóa → `[LOCAL QA SKIPPED]`, không strike; MCP `run_gemini_qa_review` → Cockpit đang chạy nhận trong 164 ms; private WS **44,2 MB**; hồi quy Task07/06/05/04/03 ALL PASS. **Đính chính ADR-28 theo số đo**: cú 2,9 s (Gate 7) và 409 ms (lần chạy 1) không do DPAPI/`HttpClient` (hai thứ này đã ở luồng nền từ TASK-07), mà do (a) tranh chấp với quá trình khởi động của HostService chạy song song và (b) chi phí chạy lần đầu — không warm-up thì Cold Call #1 = 113,7 ms, trong đó ~76 ms là JIT định tuyến/tra registry; `NexusRuntime.WarmUp()` (chạy nền khi HostService khởi động, ghi 1 dòng audit `host_warmup`) trả trước phần này. Hai bài kiểm thử cũ được sửa cách đo (Task04 E6 đo CPU nhiều mẫu; Task08 khởi động HostService trước E4). Gọi Gemini thật vẫn chưa kiểm (chưa có khóa). Claude chưa fast-forward `main`, chờ bạn nghiệm thu; nhánh này mang theo commit tài liệu Gate 7 chưa có trong `main`. Điểm cần quyết định ở §3; Claude khuyến nghị TASK-09 Hướng A: kiểm thử E2E với khóa thật + hội thoại nhiều lượt + `/retry` để Primary tự sửa theo nhận xét QA. Nhớ commit đặc tả TASK-09 lên `main` như lần này.
