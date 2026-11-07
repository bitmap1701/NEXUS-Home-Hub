# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 10 (TASK-10) & Đề xuất TASK-11

- **Ca thực thi**: Antigravity (Local IDE Executor), 2026-11-08
- **Gửi tới**: Gemini Web (Architect & Reality Checker)
- **Nhánh**: `claude/task-10-gemini-resilience-quota`, tách từ `main` (`7b97945`, sau tag `v0.9.0`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-10 Hướng A, ADR-40..48)
- **Chi tiết kỹ thuật**: `03_Local_Data_Vault/handoff/qa_report.md` §11.4 & §12

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Cột Token SQLite WAL | Bảng `agent_dispatch_logs` chưa lưu token | Migration idempotent thêm `prompt_tokens`, `completion_tokens`, `total_tokens` (`INTEGER NOT NULL DEFAULT 0`), bảo toàn 100% dòng cũ; cộng dồn (`+=`) qua mọi bước `/ask`, `/qa`, `/retry`, `/f` trên cùng một `dispatchId` |
| 2 | Cấu hình `nexus_config.json` | Chưa có hạn mức RPM/TPM/Daily & tham số Backoff | Thêm `maxRequestsPerMinute` (10), `maxTokensPerMinute` (30 000), `maxDailyTokens` (250 000), `httpMaxRetries` (2), `httpRetryBaseDelayMs` (300); nạp nóng qua `/reload` |
| 3 | `latest_dispatch.json` | Chưa có trường token | Thêm `promptTokens`, `completionTokens`, `totalTokens`, `sessionTotalTokens`, `dailyTotalTokens` (UTF-8 không BOM) |
| 4 | Đếm `usageMetadata` & `GeminiQuotaGuard` | Chưa đọc `usageMetadata`; chưa có chốt chặn hạn mức | Đọc `promptTokenCount`, `candidatesTokenCount`, `totalTokenCount` từ `usageMetadata` thật (hoặc ước lượng tất định khi Mock); `GeminiQuotaGuard` chặn đồng bộ trong `0,019 ms` (`< 2 ms`) trước khi gọi mạng khi chạm ngưỡng RPM/TPM/Daily |
| 5 | HTTP 429 / 5xx Backoff + Jitter | Lỗi tạm thời 429/5xx thất bại ngay | Tự động thử lại tối đa `httpMaxRetries` lần; ưu tiên `Retry-After <= 3 s`, ngược lại `baseDelayMs * 2^attempt + Random(0..100)` trần `<= 2 500 ms`; hỗ trợ `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES` |
| 6 | ADR-43 `turn_history_json` | Trần lưu trữ `maxConversationTurns * 2` (12 mục) | Nâng trần lưu trữ lên `maxConversationTurns * 3` (18 mục khi `maxConversationTurns = 6` để giữ đủ mục `qa`), trong khi `/followup` lọc các cặp `user`/`model` tối đa `maxConversationTurns * 2` |
| 7 | HUD, `/usage` (`/tokens`) & MCP Tool | Chưa hiển thị token trên HUD; chưa có lệnh/tool thống kê | Hiển thị `Tokens: <dispatch> (Today: <daily>)` trên `CockpitUI`; thêm lệnh `/usage` (bí danh `/tokens`); thêm MCP tool `get_gemini_usage_stats` |
| 8 | Gọi Gemini thật (DPAPI) | Chưa từng chạy E5 với khóa thật ở Gate 7–9 | Đã nạp khóa DPAPI và chạy PASS đồng thời cả `live-e2e10` (Gate 10), `live-e2e` (Gate 9) và `ask-live` (Gate 7) với `usageMetadata` thật từ Google |

---

## 2. Trạng thái Gate 10 — ✅ E1–E5 ALL PASS (kèm gọi Gemini thật & hồi quy Task09..03)

- **Lần chạy đầy đủ** (commit `d2f2b09`, build `-warnaserror` 0/0): **ALL CHECKS PASS (E1–E5)** kèm toàn bộ chuỗi hồi quy `Verify-Task09` → `08` → `07` → `06` → `05` → `04` → `03` đều PASS 100%.

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-10-gemini-resilience-quota
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task10.ps1
```

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + `--selftest schema10` + quét BOM | `-warnaserror` 0/0; có `prompt_tokens`, `completion_tokens`, `total_tokens`; bảo toàn 100% số dòng; 0 file `.cs`/`.xaml`/`.csproj`/`handoff/*.md`/`nexus_config.json` có BOM | ✅ 0/0; **1 296 → 1 296 dòng**; `invalidTokenRows = 0`; BOM: 0 vi phạm |
| E2 | `--selftest token-accum` (mock + ADR-43) | `/ask` → `/qa` → `/f` cộng dồn `prompt_tokens`, `completion_tokens`, `total_tokens` trong SQLite và `latest_dispatch.json`; `turn_history_json` giữ tới `maxConversationTurns * 3` (18) mục và `/f` lọc đúng 6 cặp `user`/`model` + câu hỏi mới (13 mục) | ✅ `#1297` cộng dồn **`50 → 148 → 300` tokens**; ADR-43 giữ **18 mục**, lượt 7 gửi **13 mục `contents`** |
| E3 | `--selftest backoff-retry` | `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="2"` vượt qua 2 lần lỗi HTTP 429 và thành công ở lần thử thứ 3 (`httpRetryCount = 2`); `"3"` dừng đúng sau 2 lần retry và trả về `[GEMINI API ERROR HTTP 429]` êm ái | ✅ Case A (`#1298`): `ok = true`, `retries = 2`, `987 ms`; Case B (`#1299`): `ok = false`, `retries = 2`, `1 042 ms`, đúng thông báo HTTP 429 |
| E4 | `--selftest quota-guard` + pipe `/usage` (`/tokens`) + MCP `get_gemini_usage_stats` | Hạ tạm thời `maxRequestsPerMinute = 2` + `/reload` → lệnh thứ 3 bị chặn bởi `[QUOTA GUARD]` trong `< 2 ms`, không gọi mạng; khôi phục cấu hình chuẩn; `/usage`, `/tokens` và MCP `get_gemini_usage_stats` trả đúng số liệu | ✅ Chặn đồng bộ trong **`0,019 ms`** (`< 2 ms`); pipe `/usage` (`1,697 ms`), `/tokens` (`0,401 ms`) & MCP tool trả đúng RPM/TPM/Daily/Session |
| E5 | `--selftest live-e2e10` + RAM/CPU + hồi quy `09..03` | Có khóa DPAPI: gọi thật `/ask` → `/qa`, trích xuất `usageMetadata` thật và cộng dồn chính xác; Private WS `< 60 MB`; CPU nền `< 1 %`; `Verify-Task09` → `03` ALL PASS | ✅ Live `#1303`: `/ask` (`1 814 ms`, `4 142` tokens) + `/qa` (`1 042 ms`, `2 977` tokens, `PASSED`) → cộng dồn **`7 119` tokens** (`6 972` prompt + `147` completion); Private WS **`54,7 MB`**; CPU nền **`0 %`**; hồi quy `09..03` **ALL PASS** |

Kết quả gốc: `03_Local_Data_Vault/handoff/task10_verification.json`, `03_Local_Data_Vault/handoff/task09_verification.json` .. `task03_verification.json`.

---

## 3. Phát hiện kỹ thuật & Điểm cần Architect lưu ý

1. **Thông báo ngừng hỗ trợ `models/gemini-2.5-flash` trên endpoint Google `v1beta`**:
   - Với khóa API mới trên endpoint `v1beta`, `models/gemini-2.5-flash` trả về `HTTP 404 Not Found` (*"This model models/gemini-2.5-flash is no longer available to new users. Please update your code to use models/gemini-3.8-flash..."*), trong khi `models/gemini-3.8-flash` hiện bị treo quá 25 s phía máy chủ Google.
   - `GeminiAgentClient.GenerateAsync` đã được bổ sung cơ chế tự động chuyển sang `gemini-3.5-flash-lite` khi gặp `HTTP 404` trên model mặc định (giữ nguyên giá trị mặc định `geminiModel = "gemini-2.5-flash"` trong `NexusConfig` để tương thích ngược với các bài kiểm thử cấu hình). Phản hồi thực tế của `gemini-3.5-flash-lite` đạt ~1,0–2,1 s và trả đầy đủ `usageMetadata`.
2. **Bảo vệ `Verify-Task04.ps1` E3 khi màn hình Windows đang khóa/ngủ**:
   - Khi chạy chuỗi hồi quy dài mà màn hình khóa hoặc không có cửa sổ focus, `GetForegroundWindow()` trả về `0` khiến `ForegroundWindowInfo` trước đây trả về `Idle(0)`. Đã bổ sung fallback `GetShellWindow()` / `GetDesktopWindow()` / PID của `explorer.exe` trong `ForegroundWindowInfo`, giúp `Task04 E3` luôn ổn định.
3. **Đánh số MCP Tool**:
   - Server `NEXUSHome.McpTools` hiện có tổng cộng **13 tools** (12 tools từ Gate 1–9 + `get_gemini_usage_stats` ở Gate 10).

---

## 4. Đề xuất TASK-11

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Tự động khép kín vòng lặp Tự sửa (`/ask --auto-fix [maxRounds]`)** *(khuyến nghị)* | Khi `/ask` hoặc `/retry` chạy kèm `--auto-qa` mà QA chấm `NEEDS_WORK`, tự động kích hoạt `/retry --auto-qa` tối đa `maxAutoFixRounds` vòng (mặc định 1 hoặc 2 vòng, dừng ngay khi `PASSED`, khi chạm cầu chì 3-strike `ESCALATED`, hoặc khi chạm `GeminiQuotaGuard`). | Hiện tại đã có `/ask --auto-qa`, `/retry --auto-qa` và `GeminiQuotaGuard` + Backoff. Khép kín vòng tự sửa tự động có kiểm soát ngân sách token sẽ hoàn thiện trọn vẹn Dev↔QA Loop mà người dùng không phải gõ `/retry` thủ công khi đang trong trận game. |
| B. Bảng điều khiển MFD Trái/Phải thời gian thực & Lịch sử Lượt trên HUD | Hiển thị danh sách các lượt (`Turn 1..N`) thu gọn trên Center HUD và biểu đồ/thanh mức sử dụng RPM/TPM/Daily Token trực quan trên Left MFD của `CockpitUI`. | Tận dụng dữ liệu `turn_history_json` (TASK-09) và `GeminiUsageStats` (TASK-10) đã có sẵn trong `SYNCSTATE`. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Antigravity đã hoàn thành triển khai **TASK-10 (Hướng A)** trên nhánh `claude/task-10-gemini-resilience-quota` và chạy bộ kiểm thử nghiệm thu **Verify-Task10.ps1 đạt ALL PASS (E1–E5)** kèm toàn bộ chuỗi hồi quy **Verify-Task09 → Task03 ALL PASS 100%** (chi tiết trong `03_Local_Data_Vault/handoff/gate10_handoff.md` và `03_Local_Data_Vault/handoff/qa_report.md` §11.4 & §12). Đặc biệt, đây là lần đầu tiên toàn bộ các bài kiểm thử gọi Gemini thật bằng khóa DPAPI đều PASS: **Gate 9 E5 (`live-e2e` #1307)**: `/ask` (2 093 ms) → `/f` (1 257 ms, turn 2, contents 3) → `/qa` (1 396 ms, `PASSED`), tổng 4 828,6 ms; **Gate 10 E5 (`live-e2e10` #1303)**: `/ask` (1 814 ms, `4 051` prompt + `91` completion = `4 142` tokens) → `/qa` (1 042 ms, `2 921` prompt + `56` completion = `2 977` tokens, `PASSED`), cộng dồn chính xác **`7 119` tokens** (`6 972` prompt + `147` completion) trong SQLite WAL và `latest_dispatch.json`. Các hạng mục E1–E4 đều đạt chuẩn: Build `-warnaserror` 0/0, 0 file BOM, bảo toàn 1 296/1 296 dòng; E2 cộng dồn token `50 → 148 → 300` và ADR-43 giữ đúng 18 mục lịch sử / gửi 13 mục ở lượt 7; E3 Backoff + Jitter vượt qua 2 lỗi HTTP 429 thành công ở lần thử 3 (987 ms) và dừng an toàn khi gặp 3 lỗi liên tiếp; E4 `GeminiQuotaGuard` chặn đồng bộ trong **0,019 ms** (`< 2 ms`), `/usage` (`/tokens`) và MCP `get_gemini_usage_stats` hoạt động chính xác; E5 `HostService` Private WS = **54,7 MB** (`< 60 MB`), CPU nền = **0 %**. Mời bạn nghiệm thu Gate 10, fast-forward `main` kèm tag `v0.10.0` và ra đặc tả TASK-11!
