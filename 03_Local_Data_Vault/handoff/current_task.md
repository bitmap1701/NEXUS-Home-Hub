# TASK-09: Vòng Lặp Tự Sửa `/retry` Theo Nhận Xét QA, Hội Thoại Nhiều Lượt (`/followup`) & Kiểm Thử E2E Với Khóa Gemini Thật (Hướng A)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 8 — Mốc `v0.8.0`)
- **Nghiệm thu Gate 8**: `PASSED` 100% và đã đồng bộ lên `main` kèm tag `v0.8.0` (Build `-warnaserror` 0/0; 0 `RegexOptions.Compiled`; migration thêm 2 cột `qa_*` bảo toàn 763/763 dòng; Cold Call #1 của `/ask` giảm từ 2.900 ms xuống `9.43 ms` nhờ `NexusRuntime.WarmUp()`, P95 đạt `2.19 ms`; `/ask --auto-qa` -> `PASSED`; `/qa` x3 -> `ESCALATED` r3 kèm đúng 1 dòng escalation; MCP `run_gemini_qa_review` -> Cockpit nhận trong `164 ms`; `Private Working Set` giảm sâu từ 58.6 MB về `44.2 MB`).
- **6 Quyết định Kiến trúc Đã Chốt (ADR-34 .. ADR-39)**:
  1. **ADR-34 (Empirical Correction to ADR-28 & `NexusRuntime.WarmUp()` Ratification)**:
     - Xác nhận thực nghiệm: cú khựng 2.9 s (Gate 7) và 409 ms (lần chạy 1 Gate 8) không do DPAPI hay `HttpClient` (vốn đã ở luồng nền từ TASK-07), mà do tranh chấp khi `HostService` đang khởi động song song và ~76 ms chi phí JIT lần đầu của luồng định tuyến/tra cứu registry/SQLite.
     - Phê chuẩn chính thức `NexusRuntime.WarmUp()` chạy nền khi `HostService` khởi động (ghi 1 dòng audit `host_warmup`) để trả trước chi phí JIT.
  2. **ADR-35 (Test Harness Refinements Ratification)**:
     - Phê chuẩn cách đo CPU trung bình nhiều mẫu ở `Verify-Task04.ps1` (E6) và việc khởi động sẵn `HostService` trước E4 ở `Verify-Task08.ps1`.
  3. **ADR-36 (Self-Correction Loop via `/retry` & Circuit-Breaker Enforcement)**:
     - Bổ sung lệnh `/retry [gợi ý bổ sung]` (hỗ trợ `/retry #<id>`, `/retry --auto-qa`, `/retry --force`) và MCP tool `retry_gemini_dispatch`.
     - Khi gọi `/retry`, Primary Agent nhận lại đề bài gốc + câu trả lời trước + nhận xét phê bình của QA (`qa_response` / `evidence_notes`) + gợi ý của người dùng để tự sinh bản sửa lỗi mới.
     - **Chốt chặn Cầu chì 3 Lần Thử**: Nếu bản ghi đang ở trạng thái `ESCALATED` (`retry_count >= 3`), lệnh `/retry` mặc định bị từ chối (trả về thông báo chặn của Circuit Breaker) trừ khi có cờ `--force` (`/retry --force`).
     - **Quy tắc đếm Strike**: Bản thân lệnh `/retry` không tự tăng `retry_count`; `retry_count` chỉ tăng khi bản sửa lỗi tiếp tục bị đánh giá `NEEDS_WORK` (bởi `/fail` hoặc `/qa`).
  4. **ADR-37 (Multi-Turn Conversation Continuity — `/followup` & `turn_history_json`)**:
     - Mở rộng bảng `agent_dispatch_logs` thêm cột `turn_history_json TEXT NOT NULL DEFAULT '[]'` và `turn_count INTEGER NOT NULL DEFAULT 1`.
     - Cho phép người dùng gõ `/followup <câu hỏi tiếp theo>` (bí danh `/f <câu hỏi>`) để hội thoại nhiều lượt nối tiếp trên cùng `dispatchId` hiện hành, giới hạn tối đa `maxConversationTurns` (mặc định `6` cặp lượt trong `nexus_config.json`) khi gửi mảng `contents` lên Gemini API.
  5. **ADR-38 (Two-Tier E2E Live Gemini Verification)**:
     - Kết hợp kiểm thử tất định 100% bằng Mock (E1–E4) và kiểm thử E2E thật với `gemini-2.5-flash` (E5 — tự động chạy ngay khi phát hiện khóa thật trong `gemini_key.dpapi` hoặc `GEMINI_API_KEY`, kèm tiện ích nạp khóa nhanh).
  6. **ADR-39 (Linear `main` & Milestone Tag `v0.8.0`)**: Khóa mốc `v0.8.0` trên `main`.

## 2. Mục Tiêu TASK-09 (Hướng A — Self-Correcting `/retry` + Multi-Turn Conversation + Live Gemini E2E)
Hoàn thiện chu trình **Tự Sửa Lỗi Khép Kín (Autonomous Self-Correction Loop)** và **Hội Thoại Đa Lượt** trên `CockpitUI` & `McpTools`:
1. **Lệnh `/retry` (Primary Agent Tự Sửa Theo Nhận Xét QA)**: Khi QA Agent chấm `NEEDS_WORK` (ví dụ chỉ ra thiếu xử lý ngoại lệ Socket), người dùng chỉ cần gõ `/retry --auto-qa` -> Primary Agent đọc nhận xét của QA để viết lại giải pháp -> QA Agent tự động chấm lại -> chuyển sang `PASSED`!
2. **Hội thoại nhiều lượt (`/followup` / `/f`)**: Tiếp tục hỏi sâu hoặc tinh chỉnh trên cùng phiên điều phối mà chuyên gia vẫn nhớ các lượt trao đổi trước đó.
3. **Sẵn sàng và kiểm chứng E2E với khóa Gemini thật**: Cung cấp cơ chế nạp khóa và kiểm thử E2E thực tế mượt mà.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Mở Rộng Schema SQLite WAL, `nexus_config.json` & `latest_dispatch.json`
1. **Migration SQLite WAL (`agent_dispatch_logs` — Bảo toàn 100% dữ liệu cũ)**:
   - Bổ sung 2 cột mới nếu chưa tồn tại:
     - `turn_count INTEGER NOT NULL DEFAULT 1`
     - `turn_history_json TEXT NOT NULL DEFAULT '[]'`
   - Định dạng phần tử trong `turn_history_json`: mảng JSON các lượt trao đổi `{ "turn": 1, "role": "user"|"model"|"qa", "text": "...", "timestampUtc": "..." }`.
2. **Mở rộng `nexus_config.json` (Tương thích ngược)**:
   - Thêm trường `"maxConversationTurns": 6` (số cặp lượt hội thoại tối đa gửi kèm trong mảng `contents` của request Gemini API để giữ độ trễ thấp và không tràn bộ nhớ).
3. **Mở rộng `latest_dispatch.json`**:
   - Bổ sung trường `"turnCount"` (int).

### 3.2. Bộ Máy Tự Sửa Lỗi `/retry` & MCP Tool `retry_gemini_dispatch` (ADR-36)
1. **Cú pháp hỗ trợ trên `CockpitUI`**:
   - `/retry [gợi ý bổ sung]`
   - `/retry #<id> [gợi ý bổ sung]`
   - Hỗ trợ kết hợp cờ `--auto-qa` (tự động gọi QA chấm lại ngay sau khi Primary Agent sửa xong) và cờ `--force` (vượt chốt chặn khi bản ghi đang ở trạng thái `ESCALATED`).
2. **Quy tắc Kiểm tra Chốt chặn Cầu chì (`ESCALATED` Guard)**:
   - Nếu bản ghi mục tiêu đang có `status == 'ESCALATED'` (hoặc `retry_count >= 3`) và lệnh gọi **không có** cờ `--force` (`allowEscalatedForce == false`):
     - Không gọi Gemini API.
     - Giữ nguyên trạng thái `ESCALATED` và hiển thị/trả về thông báo rõ ràng: `"[3-STRIKE CIRCUIT BREAKER] Dispatch #<id> is ESCALATED. Use /retry --force to override."`
3. **Luồng Thực thi `/retry` Khi Hợp Lệ**:
   - **Phần đồng bộ (`< 10 ms`)**:
     - Kiểm tra chặn lộ khóa API trong chuỗi gợi ý (`guard`).
     - Đặt trạng thái hiển thị tạm thời trên UI (`[RETRYING... Primary Agent refining]`) và trả Focus bàn phím ngay về cửa sổ trước đó.
   - **Phần bất đồng bộ (`Task.Run`)**:
     - Xây dựng prompt tự sửa gửi lên `GeminiAgentClient` (vai `primary_agent` của bản ghi đó), bao gồm:
       1. Nhiệm vụ gốc (`task_summary`).
       2. Phản hồi trước đó của Primary Agent (`ai_response`).
       3. Nhận xét/Lý do trượt từ QA Agent hoặc người dùng (`qa_response` và `evidence_notes`).
       4. Gợi ý bổ sung của người dùng từ lệnh `/retry` (nếu có).
     - Khi nhận được câu trả lời mới từ `GeminiAgentClient`:
       - Cập nhật `ai_response`, `ai_model`, `ai_latency_ms`, tăng `turn_count = turn_count + 1`, nối lượt mới vào `turn_history_json` và đặt `status = 'DISPATCHED'` (nếu không chạy `--auto-qa` ngay sau đó) hoặc chuyển thẳng sang bước QA nếu bật `--auto-qa`.
       - Nếu có `--auto-qa` (hoặc `autoQaAfterAsk == true`), tự động gọi tiếp `RunQaReviewAsync(dispatchId)` để QA Agent thẩm định bản sửa lỗi mới và chốt `PASSED` / `NEEDS_WORK` / `ESCALATED`.
4. **MCP Tool Thứ 7 (`retry_gemini_dispatch`) trong `NEXUSHome.McpTools`**:
   - Tham số: `dispatchId` (int, tùy chọn — mặc định bản ghi mới nhất), `userHint` (string, tùy chọn), `autoQa` (bool, mặc định `false`), `forceEscalated` (bool, mặc định `false`).
   - Trả về JSON kết quả sau khi chạy xong (gồm `dispatchId`, `status`, `retryCount`, `turnCount`, `aiResponse`, `qaResponse`).

### 3.3. Hội Thoại Nhiều Lượt `/followup` & MCP Tool `followup_gemini_dispatch` (ADR-37)
1. **Cú pháp trên `CockpitUI`**:
   - `/followup <câu hỏi/yêu cầu tiếp theo>` (bí danh: `/f <nội dung>`, hỗ trợ kèm `--auto-qa` và `#<id>`).
2. **Luồng Thực thi**:
   - **Phần đồng bộ (`< 10 ms`)**: Kiểm tra an toàn khóa, ghi nhận trạng thái đang xử lý lượt tiếp theo và trả Focus về Game/IDE.
   - **Phần bất đồng bộ (`Task.Run`)**:
     - Đọc `turn_history_json` của `dispatchId` mục tiêu, trích xuất tối đa `maxConversationTurns` cặp lượt gần nhất để đóng gói thành mảng `contents` nhiều lượt chuẩn Gemini API (`[ {"role": "user", "parts": [...]}, {"role": "model", "parts": [...]}, {"role": "user", "parts": [{"text": "<followupText>"}]} ]`).
     - Gọi `GeminiAgentClient` với `SystemInstruction` của Primary Agent.
     - Cập nhật `ai_response` mới nhất, tăng `turn_count = turn_count + 1`, lưu lịch sử vào `turn_history_json` (giữ tối đa `maxConversationTurns * 2` mục trong JSON để tránh phình DB) và cập nhật `latest_dispatch.json`.
     - Nếu có `--auto-qa`, tự động gọi tiếp `RunQaReviewAsync`.
3. **MCP Tool Thứ 8 (`followup_gemini_dispatch`) trong `NEXUSHome.McpTools`**:
   - Tham số: `followUpText` (string, bắt buộc), `dispatchId` (int, tùy chọn), `autoQa` (bool, mặc định `false`).

### 3.4. Chế Độ Mock Cho `/retry` & `/followup` + Kịch Bản Kiểm Thử Live Gemini API (ADR-38)
- Trong chế độ Mock (`NEXUS_GEMINI_MOCK_RESPONSE`), khi thực hiện `/retry` hoặc `/followup`, chuỗi phản hồi giả lập cần phản ánh rõ ngữ cảnh (ví dụ có chứa dấu hiệu `[MOCK-RETRY-TURN-<turnCount>]` hoặc `[MOCK-FOLLOWUP-TURN-<turnCount>]` kèm xác nhận đã đọc `qa_response` trước đó) để `Verify-Task09.ps1` kiểm chứng chính xác rằng lịch sử nhiều lượt và nhận xét QA thực sự được truyền vào `GeminiAgentClient`.
- Tạo thêm tiện ích nhỏ hoặc hỗ trợ trong `Verify-Task09.ps1` (mục E5): nếu phát hiện khóa thật trong `gemini_key.dpapi` (hoặc `GEMINI_API_KEY`), chạy thử 1 luồng E2E thật (`/ask` -> `/f` -> `/qa`) với ` gemini-2.5-flash` và in báo cáo độ trễ mạng thực tế; nếu chưa có khóa thì ghi `SKIPPED (No live key configured)` và hướng dẫn lệnh `/apikey set <KEY>`.

## 4. Verification Steps (Nghiệm Thu Gate 9 — `Verify-Task09.ps1`)
1. **E1 (Build `-warnaserror`, Zero-BOM & Schema Migration Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo.
   - Bảng `agent_dispatch_logs` có đủ `turn_count`, `turn_history_json` và bảo toàn 100% các dòng lịch sử hiện có.
2. **E2 (Self-Correction Loop: `/ask` -> `/qa` (`NEEDS_WORK`) -> `/retry --auto-qa` (`PASSED`) — ADR-36)**:
   - Tạo 1 phiên bằng `/ask` (Mock) -> cho QA chấm `NEEDS_WORK: Thiếu xử lý đóng kết nối Socket` (`status = 'NEEDS_WORK'`, `retry_count = 1`).
   - Đặt `NEXUS_GEMINI_QA_MOCK_VERDICT="PASSED: Bản sửa lỗi đã đóng Socket an toàn"` rồi gọi `/retry --auto-qa Đã bổ sung khối using cho Socket`.
   - Xác nhận: Primary Agent nhận được nhận xét QA cũ, cập nhật `ai_response` mới (`turn_count = 2`), QA tự động chấm lại chuyển `status` sang `'PASSED'` và giữ nguyên `retry_count = 1`, phần đồng bộ của `/retry` đạt `< 10 ms` (Desktop) / `< 25 ms` (LiveCombat).
3. **E3 (3-Strike Circuit Breaker Block on `/retry` & `--force` Override)**:
   - Đưa 1 bản ghi lên trạng thái `ESCALATED` (`retry_count = 3`).
   - Gọi `/retry` không có `--force` -> xác nhận bị chặn bởi Circuit Breaker, không gọi Gemini và giữ nguyên `status = 'ESCALATED'`, `turn_count` không đổi.
   - Gọi `/retry --force --auto-qa` (với QA mock `PASSED`) -> xác nhận vượt chốt chặn thành công, cập nhật giải pháp mới và chuyển sang `'PASSED'` (`retry_count = 3`).
4. **E4 (Multi-Turn `/followup` Context Continuity & MCP Tools Sync — ADR-37)**:
   - Gọi `/ask` (Turn 1) -> gọi `/f Thêm cơ chế chống trượt gói tin` (Turn 2) -> gọi MCP tool `followup_gemini_dispatch` hoặc `retry_gemini_dispatch` (Turn 3).
   - Xác nhận `turn_count = 3`, `turn_history_json` lưu đúng chuỗi lượt trao đổi (giới hạn theo `maxConversationTurns`), và `CockpitUI` đang chạy nhận đồng bộ trong `< 300 ms`.
5. **E5 (Live Gemini E2E Check — Conditional on Key — & Full Regression Suite)**:
   - Nếu đã nạp khóa thật trong DPAPI/ENV: chạy 1 vòng E2E thật tới `gemini-2.5-flash` và kiểm tra phản hồi thực tế; nếu chưa có khóa: báo `SKIPPED (No live key)` rõ ràng.
   - Đo `Private Working Set` của `HostService` duy trì `< 60 MB` (mục tiêu vùng ~44–52 MB), CPU nền `< 1%`, và chạy lại toàn bộ `Verify-Task03.ps1` .. `Verify-Task08.ps1` đều PASS 100%.