# TASK-10: Độ Bền Gọi Gemini API Thật (HTTP 429/5xx Backoff), Giới Hạn RPM/Token Cục Bộ & Đếm `usageMetadata` Trên HUD (Hướng A)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 9 — Mốc `v0.9.0`)
- **Nghiệm thu Gate 9**: `PASSED` 100% ở lần chạy 1 và đã fast-forward lên `main` tại commit `07fc8c4` kèm tag `v0.9.0` (Build `-warnaserror` 0/0; 0 file BOM; migration `turn_count` & `turn_history_json` bảo toàn 871/871 dòng; `/retry --auto-qa` tự sửa lỗi thành công trong 2.98 ms đồng bộ; cầu chì chặn `/retry` trên bản ghi `ESCALATED` trong 4.39 ms và cho phép vượt qua bằng `--force`; `/f` + MCP `followup_gemini_dispatch` đồng bộ lên Cockpit trong 153 ms; `Private Working Set` đạt kỷ lục `40.7 MB`, CPU nền `0%`).
- **9 Quyết định Kiến trúc Đã Chốt (ADR-40 .. ADR-48)**:
  1. **ADR-40 (`/followup` Status Preservation)**: Lệnh `/f` (không kèm `--auto-qa`) giữ nguyên `status` hiện hành của bản ghi (ví dụ `PASSED` hoặc `NEEDS_WORK`), chỉ cập nhật `ai_response`, `turn_count` và `turn_history_json`.
  2. **ADR-41 (Transactional No-Op on Failed/Skipped `/retry` & `/f`)**: Khi `/retry` hoặc `/f` không lấy được câu trả lời AI (do thiếu khóa hoặc lỗi mạng), không tăng `turn_count`, không ghi mục lỗi vào `turn_history_json` và giữ nguyên dòng SQLite.
  3. **ADR-42 (Circuit Breaker Guard on `retry_count >= 3`)**: Chốt chặn cầu chì chặn lệnh `/retry` thường khi `status == 'ESCALATED'` HOẶC `retry_count >= 3` (kể cả khi đã `PASSED` ở r3+), bắt buộc dùng `/retry --force` nếu muốn viết lại toàn bộ trên bản ghi đã cạn ngân sách 3 lần thử (trong khi `/f` vẫn hỏi tiếp bình thường).
  4. **ADR-43 (`turn_history_json` Cap Separation — `maxConversationTurns * 3` Storage vs `maxConversationTurns * 2` Dialogue Filter)**:
     - Nâng trần số phần tử lưu trong mảng `turn_history_json` lên `maxConversationTurns * 3` (18 phần tử khi `maxConversationTurns = 6`) để các bản ghi `role = "qa"` không chiếm chỗ của các cặp `user`/`model`.
     - Khi xây dựng mảng `contents` gửi lên Gemini API trong `/followup`, chỉ lọc lấy tối đa `maxConversationTurns * 2` phần tử có `role` là `"user"` hoặc `"model"` gần nhất.
  5. **ADR-44 (Dual Execution Semantics)**: Trên `CockpitUI`, `/retry` và `/f` chạy bất đồng bộ sau bước chuẩn bị `< 5 ms`; trên `McpTools`, lệnh chờ hoàn tất để trả kết quả đầy đủ qua JSON-RPC.
  6. **ADR-45 (Prompt Construction Separation)**: Giữ cấu trúc prompt 4 phần cho `/retry` và mảng `contents` đa lượt cho `/followup`.
  7. **ADR-46 (`Set-NexusGeminiKey.ps1` Validation)**: Phê chuẩn kiểm tra chống chuỗi rỗng trong script nạp khóa.
  8. **ADR-47 (Live Gemini Resilience & Telemetry Before Locking Final Production Quotas)**: Xây dựng đầy đủ cơ chế chịu lỗi HTTP 429/5xx, bộ giới hạn RPM/TPM cục bộ và bộ đếm `usageMetadata` trong TASK-10, cho phép điều chỉnh linh hoạt qua `nexus_config.json`.
  9. **ADR-48 (Shift Handover Protocol)**: Phê chuẩn `03_Local_Data_Vault/handoff/claude_shift_handover.md` làm tài liệu bàn giao ca chuẩn giữa Claude, Antigravity và Gemini.

## 2. Mục Tiêu TASK-10 (Hướng A — Live API Resilience, Rate/Token Budget Limiter & `usageMetadata` HUD Telemetry)
Chuẩn bị lớp giáp bảo vệ và đo lường hoàn chỉnh khi vận hành với khóa Gemini API thật trên máy của Nhật Hòa:
1. **Độ bền khi gọi mạng thật (HTTP 429 / 5xx Exponential Backoff + Jitter)**: Tự động thử lại có kiểm soát khi gặp lỗi giới hạn tốc độ tạm thời (`HTTP 429 Too Many Requests`) hoặc lỗi máy chủ tạm thời (`502`, `503`, `504`), tôn trọng header `Retry-After` (trong giới hạn an toàn) mà không làm treo UI.
2. **Cầu chì Ngân sách Gọi API Cục bộ (`GeminiQuotaGuard` — RPM, TPM & Daily Token Cap)**: Bảo vệ quota Gemini của người dùng khỏi việc vô tình bấm liên thanh hoặc vòng lặp script gọi quá số lượt/phút (`maxRequestsPerMinute`) hoặc quá số token/phút (`maxTokensPerMinute`) và số token/ngày (`maxDailyTokens`).
3. **Đo lường & Hiển thị Token Thực Tế (`usageMetadata` trên HUD & SQLite WAL)**: Trích xuất `promptTokenCount`, `candidatesTokenCount`, `totalTokenCount` từ phản hồi Gemini API, lưu vào SQLite WAL (cộng dồn chính xác qua các lượt `/ask`, `/qa`, `/retry`, `/f`), xuất ra `latest_dispatch.json` và hiển thị trực quan trên `CockpitUI` (màn hình MSI G272) cùng lệnh `/usage` (bí danh `/tokens`).
4. **Hoàn thiện cắt lọc `turn_history_json` theo ADR-43**.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Mở Rộng Schema SQLite WAL, `nexus_config.json` & `latest_dispatch.json`
1. **Migration SQLite WAL (`agent_dispatch_logs` — Bảo toàn 100% số dòng lịch sử hiện có)**:
   - Bổ sung 3 cột mới nếu chưa tồn tại:
     - `prompt_tokens INTEGER NOT NULL DEFAULT 0`
     - `completion_tokens INTEGER NOT NULL DEFAULT 0`
     - `total_tokens INTEGER NOT NULL DEFAULT 0`
   - Khi một `dispatchId` thực hiện nhiều bước gọi Gemini (ví dụ `/ask` rồi `/qa`, `/retry`, `/f`), các cột `prompt_tokens`, `completion_tokens`, `total_tokens` của dòng đó được **cộng dồn (`+=`)** để phản ánh chính xác tổng chi phí token của nhiệm vụ đó.
2. **Mở rộng `nexus_config.json` (Tương thích ngược & Tự phục hồi)**:
   - Bổ sung 5 tham số bảo vệ & độ bền API:
     - `"maxRequestsPerMinute": 10`
     - `"maxTokensPerMinute": 30000`
     - `"maxDailyTokens": 250000`
     - `"httpMaxRetries": 2`
     - `"httpRetryBaseDelayMs": 300`
3. **Mở rộng `latest_dispatch.json`**:
   - Bổ sung `"promptTokens"`, `"completionTokens"`, `"totalTokens"` (của bản ghi hiện tại) và `"sessionTotalTokens"` / `"dailyTotalTokens"`.

### 3.2. Bộ Đếm `usageMetadata` & Cầu Chì Ngân Sách Cục Bộ (`GeminiQuotaGuard`)
1. **Trích xuất `usageMetadata` trong `GeminiAgentClient`**:
   - Khi nhận JSON phản hồi từ Gemini API thật, đọc đối tượng `usageMetadata`:
     - `promptTokenCount` (int)
     - `candidatesTokenCount` (int)
     - `totalTokenCount` (int — nếu thiếu thì tính bằng `promptTokenCount + candidatesTokenCount`)
   - Trong chế độ Mock (`NEXUS_GEMINI_MOCK_RESPONSE` hoặc `NEXUS_GEMINI_QA_MOCK_VERDICT`), tự động ước lượng số token giả lập tất định dựa trên độ dài chuỗi (ví dụ: `promptTokens = Math.Max(10, promptText.Length / 4)`, `completionTokens = Math.Max(8, responseText.Length / 4)`, `totalTokens = promptTokens + completionTokens`) để các bài kiểm thử tự động trong `Verify-Task10.ps1` kiểm chứng chính xác 100% luồng cộng dồn token và cầu chì ngân sách.
2. **Kiểm tra Hạn mức Trước Khi Gọi (`GeminiQuotaGuard`)**:
   - Duy trì bộ đếm cửa sổ trượt 60 giây trong bộ nhớ cho số request/phút (`RPM`) và số token/phút (`TPM`), kết hợp tổng token trong ngày UTC (`SELECT COALESCE(SUM(total_tokens), 0) FROM agent_dispatch_logs WHERE timestamp_utc >= <today_start_utc>`).
   - Nếu vượt ngưỡng `maxRequestsPerMinute`, `maxTokensPerMinute`, hoặc `maxDailyTokens` trong `nexus_config.json`:
     - Chặn ngay trước khi gửi request mạng (`< 1 ms`), không làm văng ứng dụng, trả về thông báo rõ ràng: `"[QUOTA GUARD] Rate/Token limit reached (RPM/TPM/Daily). Adjust in nexus_config.json and run /reload if needed."`

### 3.3. Cơ Chế Thử Lại Thông Minh Khi Gặp HTTP 429 / 5xx (`Exponential Backoff + Jitter`)
- Trong `GeminiAgentClient`:
  - Khi gặp mã trạng thái HTTP tạm thời (`429 Too Many Requests`, `502 Bad Gateway`, `503 Service Unavailable`, `504 Gateway Timeout`), thực hiện thử lại tối đa `httpMaxRetries` lần (mặc định `2` lần thử lại = tối đa 3 lần gọi).
  - Thời gian chờ giữa các lần thử: nếu phản hồi có header `Retry-After` (số giây) nhỏ hơn hoặc bằng 3 giây thì ưu tiên dùng, ngược lại tính `delayMs = httpRetryBaseDelayMs * Math.Pow(2, attempt) + Random(0..100)` (giới hạn trần mỗi lần chờ `<= 2500 ms` để không treo lâu).
- **Hỗ trợ Giả lập Lỗi Tạm thời Tất định Cho Kiểm Thử (`NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES`)**:
  - Nếu biến môi trường `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES` được đặt bằng một số nguyên `N` (ví dụ `N = 1` hoặc `N = 2`), `GeminiAgentClient` sẽ giả lập trả về lỗi `HTTP 429 Too Many Requests` trong `N` lần thử đầu tiên của lệnh gọi, sau đó thành công ở lần thử thứ `N + 1` (nếu `N <= httpMaxRetries`) hoặc thất bại với thông báo `[GEMINI API ERROR HTTP 429]` sau khi đã thử hết `httpMaxRetries` (nếu `N > httpMaxRetries`). Ghi nhận số lần retry thực tế (`httpRetryCount`) để `Verify-Task10.ps1` kiểm chứng tự động.

### 3.4. Tinh Chỉnh Cắt Lọc `turn_history_json` (ADR-43) & Hiển Thị HUD / MCP Tool
1. **Cắt lọc `turn_history_json` theo ADR-43**:
   - Cho phép lưu tối đa `maxConversationTurns * 3` mục trong `turn_history_json` (để giữ cả mục `qa`).
   - Khi tạo mảng `contents` cho `/followup`, lọc các mục có `role == "user"` hoặc `role == "model"` và lấy tối đa `maxConversationTurns * 2` mục gần nhất.
2. **Hiển thị Token trên `CockpitUI` & Lệnh `/usage` (bí danh `/tokens`)**:
   - Trên thanh trạng thái hoặc thẻ Active Card của `CockpitUI` (màn hình **MSI Optix G272**), hiển thị ngắn gọn số token của phiên hiện tại và tổng token trong ngày (ví dụ: `Tokens: 142 (Today: 1,280)`).
   - Bổ sung lệnh `/usage` (bí danh `/tokens`): hiển thị chi tiết `RPM hiện tại / maxRequestsPerMinute`, `TPM hiện tại / maxTokensPerMinute`, `Daily Tokens / maxDailyTokens`, và tổng số lần retry HTTP 429/5xx nếu có.
3. **MCP Tool Thứ 9 (`get_gemini_usage_stats`) trong `NEXUSHome.McpTools`**:
   - Trả về JSON thống kê chi tiết mức sử dụng token trong phiên, trong ngày, trạng thái hạn mức RPM/TPM và cấu hình hiện hành.

## 4. Verification Steps (Nghiệm Thu Gate 10 — `Verify-Task10.ps1`)
1. **E1 (Build `-warnaserror`, Zero-BOM & Token Schema Migration)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; tất cả file cấu hình/handoff đều 0-byte BOM.
   - Bảng `agent_dispatch_logs` có đủ `prompt_tokens`, `completion_tokens`, `total_tokens` và bảo toàn 100% số dòng lịch sử hiện có.
2. **E2 (Token Accumulation Across `/ask` -> `/qa` -> `/f` & ADR-43 History Cap)**:
   - Chạy chuỗi `/ask` -> `/qa` -> `/f` (với Mock), xác nhận `prompt_tokens`, `completion_tokens`, `total_tokens` cộng dồn chính xác trên dòng SQLite và xuất hiện đầy đủ trong `latest_dispatch.json`.
   - Kiểm tra khi có xen kẽ các mục `qa` trong `turn_history_json`, mảng lưu trữ giữ được tới `maxConversationTurns * 3` mục và `/followup` vẫn lọc đúng các cặp `user`/`model`.
3. **E3 (HTTP 429 Exponential Backoff Recovery & Exhaustion Test)**:
   - Đặt `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="2"` (với `httpMaxRetries = 2`) -> gọi `/ask` -> xác nhận hệ thống tự động backoff thử lại 2 lần và **thành công ở lần thứ 3**.
   - Đặt `NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="3"` (vượt `httpMaxRetries = 2`) -> gọi `/ask` -> xác nhận hệ thống dừng đúng sau 2 lần retry và ghi nhận thông báo `[GEMINI API ERROR HTTP 429]` êm ái mà không crash.
4. **E4 (Client-Side RPM / Token Budget Guard & `/usage` / MCP `get_gemini_usage_stats`)**:
   - Hạ tạm thời `maxRequestsPerMinute` (hoặc `maxTokensPerMinute`) trong `nexus_config.json` + `/reload`, gửi vượt ngưỡng -> xác nhận bị chặn tức thì bởi `[QUOTA GUARD]` trong `< 2 ms`, sau đó khôi phục lại cấu hình chuẩn.
   - Gọi lệnh `/usage` và MCP tool `get_gemini_usage_stats` xác nhận trả về đúng số liệu token và hạn mức.
5. **E5 (Live Gemini E2E Check — Nếu Đã Nạp Khóa — & Full Regression Suite Task03..09)**:
   - Nếu phát hiện khóa thật trong `gemini_key.dpapi` / `GEMINI_API_KEY`: chạy 1 lượt E2E thật tới `gemini-2.5-flash` và in ra `prompt_tokens`, `completion_tokens`, `total_tokens` thật từ `usageMetadata` của Google; nếu chưa có khóa: báo `SKIPPED (No live key)` rõ ràng.
   - `Private Working Set` của `HostService` duy trì `< 60 MB` (mục tiêu vùng ~41–50 MB), CPU nền `< 1%`, và chạy lại toàn bộ `Verify-Task03.ps1` .. `Verify-Task09.ps1` đều PASS 100%.