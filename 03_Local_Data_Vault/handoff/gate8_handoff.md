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

## 2. Trạng thái Gate 8 — 🟡 Lần chạy 1: E1, E4 PASS; E2, E3, E5 FAIL — đã sửa, chờ lần chạy 2

Lần chạy 1 (22:47–22:49, commit `10d8deb`): build 0/0, 0 `RegexOptions.Compiled`, QA tự động đúng chức năng (`--auto-qa` → PASSED; `/qa` ×3 → ESCALATED r3, 1 dòng; không khóa → bỏ qua, không strike; MCP → Cockpit 155,9 ms), private WS **38,6 MB**. Trượt vì thời gian: Cold Call #1 = 409 ms (toàn bộ ở `DispatchAndDbMs`, đo trong ~2 s sau khi HostService khởi động), P95 11,3 ms, `/qa` lần đầu 10,19 ms; và Task04 E6 đo CPU một cửa sổ 3 s được 1,47 % (kéo hồi quy 05–07 trượt theo). Phân tích và bản sửa: qa_report §10.4.

**Lưu ý quan trọng (qa_report §10.1)**: ADR-28 cho rằng 2,9 s là do DPAPI/`HttpClient` chạy trên luồng đồng bộ, nhưng code TASK-07 đã gọi DPAPI trong `Task.Run` và tạo `HttpClient` lúc khởi động, không phải trong `/ask`. Nguyên nhân thật chưa rõ; `AskSyncBreakdown` ở E2 sẽ cho biết bước nào chậm.

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-08-gemini-qa-loop
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task08.ps1 -SkipBuild
```
(Lần chạy 2 sau bản sửa cần build lại: bỏ `-SkipBuild`.) Không cần `git stash` lần này (đặc tả đã nằm trên `main`). Hồi quy đầy đủ (Verify-Task07 → 06 → 05 → 04 → 03) mất khoảng 5–6 phút. Biến mock chỉ được đặt cho tiến trình con của script, không bao giờ cho HostService chạy lâu dài.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + `--selftest schema08` + quét mã | `-warnaserror` 0/0; có `qa_response`, `qa_latency_ms`, số dòng trước = sau; 0 lần `RegexOptions.Compiled` trong `.cs` | ⏳ |
| E2 | `--selftest ask-cold` (mock) | Cold Call #1 `TotalSyncMs` < 25 ms (LiveCombat 40); P95 10 lần sau < 10 ms (LiveCombat 25); in đủ 4 thành phần; chạy mock không tạo `HttpClient`. In thêm số đo không warm-up để tham khảo | ⏳ |
| E3 | `--selftest autoqa`, `qa-strikes`, `qa-missingkey` | 3a: `/ask --auto-qa Thiết kế gói tin UDP có seqId` → `PASSED`, có cả `ai_response` lẫn `qa_response` trong SQLite và `latest_dispatch.json`; 3b: `/qa` ×3 với `NEEDS_WORK` → r1, r2, `ESCALATED` r3, đúng 1 dòng escalation, phần đồng bộ `/qa` < 10 ms; 3c: không khóa → `[LOCAL QA SKIPPED]`, không đổi trạng thái | ⏳ |
| E4 | MCP `run_gemini_qa_review` + pipe `SYNCSTATE` | MCP trả `PASSED`; Cockpit đang chạy nhận `qaResponse` mới, độ trễ < 300 ms | ⏳ |
| E5 | RAM + hồi quy | Private WS của HostService < 60 MB (mục tiêu 45–54); Verify-Task07/06/05/04/03 ALL PASS | ⏳ |

Kết quả gốc: `handoff/task08_verification.json`, `handoff/task08_selftest_*.json`.

**Kiểm tay nên làm (khi có khóa Gemini)**: trong game `Ctrl+Shift+Space` → `/ask --auto-qa Thiết kế gói tin UDP có seqId cho Delta Force overlay` → Enter: bàn phím về game ngay; thẻ trên MSI G272 lần lượt hiện THINKING → AI RESPONSE → `[QA REVIEWING... Reality Checker]` → khung QA REVIEW và trạng thái mới.

---

## 3. Điểm mở cần Architect quyết định

1. **Nguyên nhân 2,9 s / 409 ms**: số đo lần 1 cho thấy toàn bộ nằm ở `DispatchAndDbMs` và cả hai lần đều xảy ra ngay sau khi HostService khởi động (tranh chấp giữa hai tiến trình), không phải DPAPI/`HttpClient` như ADR-28 giả định. Script giờ chỉ khởi động HostService trước E4. Đồng ý cách đo này phản ánh đúng thực tế?
2. **Cold Call #1 đo sau warm-up** (giống HostService thật khi khởi động); số đo không warm-up chỉ in để tham khảo. Chấp nhận cách đo này?
3. **QA không tính strike khi lỗi API/mạng/timeout** (thêm ngoài đặc tả). Nếu không, mạng chập chờn có thể đẩy dispatch sang `ESCALATED`.
4. **Không có dòng `VERDICT:`** → `NEEDS_WORK` và tính 1 strike (theo "nguyên tắc Reality Checker" của đặc tả). Có muốn coi đó là "QA không chạy" thay vì strike?
5. **`run_gemini_qa_review` là Cấp Vàng** (đổi `status`). Đặc tả không ghi cấp.
6. **Commit tài liệu Gate 7 (`11d6785`) không có trong `main`**: lần fast-forward `main` tới `f9a173e` đã bỏ qua nó. Nhánh này mang theo (`316ed18`), nên sẽ vào `main` cùng TASK-08.
7. **Chi phí Gemini của `--auto-qa`**: mỗi `/ask --auto-qa` gọi 2 lần (Primary + QA). Có cần giới hạn số lần gọi/phút trong `nexus_config.json`?

---

## 4. Đề xuất TASK-09

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Lần chạy thật với khóa + hội thoại nhiều lượt** *(khuyến nghị)* | Kiểm thử E2E với khóa thật (đo `ai_latency_ms`, `qa_latency_ms`, xử lý 429); `/ask #id <tiếp>` gửi kèm lượt trước (giới hạn token); `/retry #id` để Primary sửa theo nhận xét QA rồi QA chấm lại (vòng Dev↔QA thật sự, dừng ở strike 3); giới hạn số lần gọi/phút. | Đến giờ mọi bài kiểm thử Gemini đều chạy bằng mock. Vòng `/retry` biến cầu chì 3 lần thử thành quy trình tự sửa đúng nghĩa. |
| B. Giọng nói (PCM 16 kHz + VAD) → `/ask` | Mic → Gemini Live → lệnh Cockpit. | Cần đo CPU/âm thanh khi chơi; sau A. |
| C. Dọn giao diện & hiệu năng nền | Đỉnh CPU ~6 s, RAM private trên thanh trạng thái, chữ bị cắt, MFD lỗi thời. | Nhỏ, có thể gộp vào A. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai TASK-08 trên nhánh `claude/task-08-gemini-qa-loop` của https://github.com/bitmap1701/NEXUS-Home-Hub (tách từ `main` @`86520e9`). Hãy đọc `03_Local_Data_Vault/handoff/gate8_handoff.md` và `qa_report.md` §10. Trạng thái: **chưa build** (môi trường Claude không có .NET); Nhật Hòa sẽ chạy `Verify-Task08.ps1` (E1–E5). Đã làm: `AskSyncBreakdown` (`GuardMs`/`DispatchAndDbMs`/`TaskSpawnMs`/`TotalSyncMs`) cho `/ask` và `/qa`; `NexusRuntime.WarmUp()` chạy nền khi HostService khởi động; bỏ cả 4 `RegexOptions.Compiled`; `Lazy<HttpClient>` với `UseProxy = false`; cột `qa_response`, `qa_latency_ms`; `/qa [#id] [bằng chứng]`, `/ask --auto-qa`, `autoQaAfterAsk`; QA dùng persona của đúng `qa_agent`, đọc `VERDICT:` rồi đi qua máy trạng thái ADR-19 (cầu chì 3 lần → `ESCALATED`, 1 dòng escalation); mock `NEXUS_GEMINI_QA_MOCK_VERDICT`; MCP `run_gemini_qa_review` (Cấp Vàng); khung QA REVIEW trên Cockpit. **Đối chiếu quan trọng**: ADR-28 giả định 2,9 s là do DPAPI/`HttpClient` chạy đồng bộ, nhưng code TASK-07 đã gọi DPAPI trong `Task.Run` và tạo `HttpClient` lúc khởi động — nguyên nhân thật chưa rõ, E2 sẽ cho biết bước nào chậm. Thêm ngoài đặc tả: lỗi API/mạng khi QA không tính strike. Lưu ý: commit tài liệu Gate 7 (`11d6785`) không có trong `main` (fast-forward tới `f9a173e` đã bỏ qua) — nhánh này mang theo. Điểm cần bạn quyết định ở §3. Claude khuyến nghị TASK-09 Hướng A: kiểm thử E2E với khóa thật + hội thoại nhiều lượt + `/retry` để Primary tự sửa theo nhận xét QA.
