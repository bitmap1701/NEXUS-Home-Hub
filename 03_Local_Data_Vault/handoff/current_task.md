# TASK-08: Vòng Lặp Dev-QA Tự Động Có Gemini (`/qa` & `--auto-qa`), Ép Giảm RAM & Triệt Tiêu Độ Trễ Cold-Start Của `/ask` (Hướng A)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 7 — Mốc `v0.7.0`)
- **Nghiệm thu Gate 7**: `PASSED` 100% và đã fast-forward vào `main` kèm tag `v0.7.0` (Build `-warnaserror` 0/0; migration thêm 3 cột `ai_*` bảo toàn 465/465 dòng; DPAPI 262-byte blob không chứa bản rõ, quét rò rỉ khóa trên `.db/.wal/.shm` và JSON sạch tuyệt đối; `/ask` đã làm nóng đạt 1.5–2.4 ms; MCP `consult_gemini_agent` đồng bộ thẳng lên Cockpit; hồi quy Task06/05/04/03 PASS 100%).
- **6 Quyết định Kiến trúc Đã Chốt (ADR-28 .. ADR-33)**:
  1. **ADR-28 (Fix 2.9s Cold-Start `/ask` Spike & Step-by-Step Instrumentation)**:
     - Nguyên nhân lần `/ask` đầu tiên mất 2.9 s là do tác vụ khởi tạo lạnh (DPAPI RPC `CryptUnprotectData`, khởi tạo `HttpClient` / dò proxy WPAD của Windows, hoặc JIT regex) chạy trên luồng đồng bộ trước khi tách sang background task.
     - Giải pháp bắt buộc: Đẩy toàn bộ `DpapiSecretVault.ResolveApiKey()` và việc lấy `HttpClient` vào **bên trong luồng nền bất đồng bộ (`Task.Run`)**. Luồng đồng bộ của `/ask` chỉ thực hiện: (1) kiểm tra chặn lộ khóa (`guardMs`), (2) điều phối Squad + ghi bản ghi `DISPATCHED` vào SQLite WAL & `latest_dispatch.json` (`dispatchAndDbMs`), (3) khởi tạo background task (`taskSpawnMs`).
     - Bổ sung bộ đo từng bước `AskSyncBreakdown` (`guardMs`, `dispatchAndDbMs`, `taskSpawnMs`, `totalSyncMs`) để chứng minh **ngay cả lần `/ask` đầu tiên (Cold Call #1)** cũng đạt `< 25 ms` (Desktop) / `< 40 ms` (LiveCombat), và P95 các lần tiếp theo `< 10 ms`.
  2. **ADR-29 (Memory Slimming — Đưa `Private Working Set` Về Vùng `< 53 MB`)**:
     - Gỡ bỏ hoàn toàn `RegexOptions.Compiled` trên toàn bộ mã nguồn (tránh phát sinh Dynamic IL Assembly chiếm cứng 4–6 MB trên LoaderHeap).
     - Chuyển `HttpClient` sang khởi tạo lười (`Lazy<HttpClient>`) chỉ cấp phát khi thực sự gửi request mạng thật (không khởi tạo khi chạy ở chế độ Mock hoặc khi chưa gọi API thật), đồng thời cấu hình `SocketsHttpHandler` gọn nhẹ để tránh treo dò proxy WPAD.
  3. **ADR-30 (Automated Gemini Dev-QA Loop — `/qa`, `/ask --auto-qa` & MCP `run_gemini_qa_review`)**:
     - Nối `qa_agent` của từng Squad vào `GeminiAgentClient` để tự động kiểm định chất lượng câu trả lời của Primary Agent (hoặc bằng chứng thực thi do người dùng/MCP cung cấp) và tự động gọi `UpdateVerdict` (`PASSED`, `NEEDS_WORK`, hoặc kích hoạt cầu chì 3 lần thử `ESCALATED`).
  4. **ADR-31 (Deterministic QA Mock Mode — `NEXUS_GEMINI_QA_MOCK_VERDICT`)**:
     - Hỗ trợ biến môi trường `NEXUS_GEMINI_QA_MOCK_VERDICT` (`PASSED` hoặc `NEEDS_WORK`) để `Verify-Task08.ps1` kiểm thử tự động 100% chu trình `/ask` -> `/qa` -> `PASSED` / `NEEDS_WORK` / `ESCALATED` mà không cần gọi mạng thật.
  5. **ADR-32 (Optional Live API Test Policy)**: Khi chưa cài khóa thật vào `gemini_key.dpapi`, bước test gọi mạng thật được ghi nhận là `SKIPPED` mà không chặn Gate.
  6. **ADR-33 (Milestone Tag `v0.7.0`)**: Khóa mốc `v0.7.0` trên `main`.

## 2. Mục Tiêu TASK-08 (Hướng A — Automated Gemini QA Loop + Memory Slimming + `/ask` Instrumentation)
1. **Khép kín tự động vòng lặp Dev <-> QA bằng AI**:
   - Người dùng gõ `/ask <nhiệm vụ>` -> Primary Agent (ví dụ `Backend Architect`) sinh lời giải.
   - Người dùng gõ `/qa [ghi chú/bằng chứng bổ sung]` (hoặc dùng `/ask --auto-qa <nhiệm vụ>` để chạy nối tiếp tự động) -> Hệ thống nạp đúng hồ sơ Markdown của **`qa_agent`** đi kèm (ví dụ `API Tester` hoặc `Reality Checker`), chấm điểm câu trả lời/bằng chứng và tự động cập nhật phán quyết (`PASSED` / `NEEDS_WORK` / `ESCALATED`) vào SQLite WAL, `latest_dispatch.json`, `qa_report.md` và giao diện `CockpitUI`.
2. **Đo từng bước (`/ask` Step Timing) & Xóa bỏ cú khựng 2.9s ở lần gọi đầu tiên**.
3. **Dọn bộ nhớ (`Private Working Set`)**: Bỏ `RegexOptions.Compiled` và lười hóa `HttpClient` để giữ mức RAM ổn định cách xa trần 60 MB.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Đo Từng Bước Đường Găng Đồng Bộ Của `/ask` & Triệt Tiêu Cold-Start 2.9s (ADR-28)
- Tạo cấu trúc kết quả đo `AskSyncBreakdown` gồm:
  - `GuardMs` (double): Thời gian kiểm tra chặn lộ khóa API trong câu lệnh.
  - `DispatchAndDbMs` (double): Thời gian chọn Squad + ghi bản ghi `DISPATCHED` vào SQLite WAL + cập nhật `latest_dispatch.json`.
  - `TaskSpawnMs` (double): Thời gian đẩy tác vụ gọi Gemini vào `Task.Run`.
  - `TotalSyncMs` (double): Tổng thời gian phần đồng bộ trước khi trả điều khiển/Focus về cho người dùng.
- **Quy tắc bất biến trên đường găng đồng bộ của `/ask` và `/qa`**:
  - KHÔNG gọi `ProtectedData.Unprotect` (`DpapiSecretVault.ResolveApiKey()`) trên luồng đồng bộ; việc giải mã khóa DPAPI chỉ được gọi bên trong `Task.Run` bất đồng bộ.
  - KHÔNG khởi tạo `HttpClient` hoặc `SocketsHttpHandler` trên luồng đồng bộ.
- Ghi nhận `AskSyncBreakdown` vào kết quả trả về nội bộ (hoặc log/telemetry) để script `Verify-Task08.ps1` kiểm tra trực tiếp cả lần gọi đầu tiên (`Cold Call #1`) lẫn phân vị P95.

### 3.2. Tối Ưu Hóa Bộ Nhớ (`Private Working Set` Slimming — ADR-29)
- Tìm và xóa bỏ toàn bộ cờ `RegexOptions.Compiled` trong Solution (giữ `RegexOptions.CultureInvariant` hoặc kiểm tra chuỗi trực tiếp).
- Trong `GeminiAgentClient`:
  - Sử dụng `Lazy<HttpClient>` (Thread-safe) chỉ khởi tạo khi thực sự bước vào nhánh gửi HTTP request thật (tức là đã qua bước kiểm tra Mock và đã xác nhận có khóa API hợp lệ).
  - Khi tạo `SocketsHttpHandler`, đặt `UseProxy = false` (hoặc cấu hình tối giản không quét WPAD chậm) và `PooledConnectionLifetime = TimeSpan.FromMinutes(2)`.

### 3.3. Bộ Máy Nghiệm Thu Tự Động Với Gemini QA Agent (`/qa`, `--auto-qa` & MCP `run_gemini_qa_review` — ADR-30 & ADR-31)
1. **Mở rộng cấu hình `nexus_config.json`**:
   - Thêm trường `"autoQaAfterAsk": false` (mặc định `false` để tương thích ngược với các bài test `/ask` đơn lẻ; có thể bật trong config hoặc truyền cờ `--auto-qa` ngay trong câu lệnh `/ask --auto-qa <nhiệm vụ>`).
2. **Mở rộng Schema SQLite WAL (`agent_dispatch_logs`) & `latest_dispatch.json` (Tương thích ngược)**:
   - Bổ sung 2 cột mới vào `agent_dispatch_logs` (nếu chưa có):
     - `qa_response TEXT NOT NULL DEFAULT ''`
     - `qa_latency_ms INTEGER NOT NULL DEFAULT 0`
   - Bổ sung `qaResponse` và `qaLatencyMs` vào `latest_dispatch.json`.
3. **Logic Thực Thi QA Tự Động (`RunQaReviewAsync`)**:
   - Đầu vào: `int? dispatchId` (mặc định lấy bản ghi mới nhất), `string? additionalEvidence` (ghi chú hoặc kết quả thực thi bổ sung).
   - Nạp hồ sơ Markdown của `qa_agent` đã được gắn với bản ghi `dispatchId` đó từ `02_Agents_Registry` làm `SystemInstruction` cho QA.
   - Đóng gói nội dung gửi QA Agent gồm:
     - `Task Summary` (Đề bài gốc)
     - `Primary Agent` & `AI Response` (Giải pháp/Câu trả lời của Primary Agent)
     - `Additional Evidence` (Bằng chứng bổ sung nếu có)
     - Chỉ thị định dạng: Yêu cầu QA Agent mở đầu câu trả lời bằng dòng `VERDICT: PASSED` hoặc `VERDICT: NEEDS_WORK`, theo sau là nhận xét kiểm định ngắn gọn (1–3 câu).
   - **Phân tích Phán quyết (Verdict Parsing)**:
     - Nếu phản hồi của QA Agent chứa `VERDICT: PASSED` -> xác định `verdict = "PASSED"`.
     - Nếu chứa `VERDICT: NEEDS_WORK` (hoặc mặc định theo nguyên tắc Reality Checker khi không rõ ràng) -> xác định `verdict = "NEEDS_WORK"`.
     - Nếu không có khóa API (và không bật Mock): giữ nguyên trạng thái hiện tại, ghi vào `qa_response` thông báo `"[LOCAL QA SKIPPED] Chưa cấu hình Gemini API Key."` mà không tự ý tăng `retry_count`.
   - Khi có phán quyết hợp lệ từ QA Agent (Mock hoặc Live):
     - Lưu `qa_response` và `qa_latency_ms` vào SQLite WAL.
     - Gọi hàm `UpdateVerdict(dispatchId, verdict, evidenceNotes: qaSummary)` để cập nhật chính thức `status` (`PASSED`, `NEEDS_WORK`, hoặc `ESCALATED` khi chạm 3 lần trượt) cùng `latest_dispatch.json` và `qa_report.md` (nếu chạm mốc 3-Strike Escalation).
4. **Hỗ trợ Mock Tất định Cho QA (`NEXUS_GEMINI_QA_MOCK_VERDICT`)**:
   - Khi biến môi trường `NEXUS_GEMINI_QA_MOCK_VERDICT` có giá trị (ví dụ `"PASSED: Đạt tiêu chuẩn P95 < 10ms"` hoặc `"NEEDS_WORK: Thiếu kiểm tra tràn bộ đệm"`), nhánh QA trả về ngay phán quyết giả lập này để phục vụ kiểm thử tự động.
5. **Giao diện `CockpitUI` & MCP Tool `run_gemini_qa_review`**:
   - Trên `CockpitUI`:
     - Hỗ trợ lệnh `/qa [bằng chứng bổ sung]` và `/qa #<id> [bằng chứng]`: phần đồng bộ trả Focus về game ngay lập tức (`< 10 ms`), phần chạy ngầm gọi `RunQaReviewAsync` và cập nhật thẻ trạng thái + khung phản hồi trên màn **MSI Optix G272**.
     - Hỗ trợ lệnh `/ask --auto-qa <nhiệm vụ>`: sau khi Primary Agent trả lời xong trong luồng ngầm, tự động chạy nối tiếp `RunQaReviewAsync` và chốt phán quyết (`PASSED` / `NEEDS_WORK` / `ESCALATED`).
   - Trên `NEXUSHome.McpTools`:
     - Bổ sung tool thứ 6: `run_gemini_qa_review` (tham số: `dispatchId` tùy chọn, `additionalEvidence` tùy chọn) trả về JSON chứa `dispatchId`, `qaAgent`, `verdict`, `status`, `retryCount`, `qaResponse`, `qaLatencyMs`.

## 4. Verification Steps (Nghiệm Thu Gate 8 — `Verify-Task08.ps1`)
1. **E1 (Build `-warnaserror`, Zero-BOM & Schema Migration Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo.
   - Bảng `agent_dispatch_logs` có đủ `qa_response`, `qa_latency_ms` và bảo toàn 100% số dòng lịch sử hiện có.
   - Quét mã nguồn xác nhận **0 lần xuất hiện** `RegexOptions.Compiled`.
2. **E2 (Cold-Start `/ask` Breakdown & P95 Latency Check — ADR-28)**:
   - Đo trực tiếp lần gọi `/ask` đầu tiên (`Cold Call #1`) cùng 10 lần gọi tiếp theo:
     - Xác nhận `Cold Call #1` có `TotalSyncMs < 25 ms` (Desktop) / `< 40 ms` (LiveCombat) — chứng minh đã triệt tiêu hoàn toàn cú khựng 2.900 ms của Gate 7.
     - Xác nhận P95 `TotalSyncMs` của chuỗi gọi đạt `< 10 ms` (Desktop) / `< 25 ms` (LiveCombat) và in rõ 4 thành phần `GuardMs`, `DispatchAndDbMs`, `TaskSpawnMs`, `TotalSyncMs`.
3. **E3 (Automated QA Loop `/qa` & `/ask --auto-qa` -> `PASSED` & `ESCALATED` — ADR-30/31)**:
   - Kịch bản 3a (`--auto-qa` -> `PASSED`): Đặt `NEXUS_GEMINI_MOCK_RESPONSE` và `NEXUS_GEMINI_QA_MOCK_VERDICT="PASSED: Kịch bản UDP đạt chuẩn"`, gọi `/ask --auto-qa Thiết kế gói tin UDP có seqId` -> xác nhận sau khi tác vụ ngầm hoàn tất, bản ghi tự động chuyển sang `status = 'PASSED'`, có đủ cả `ai_response` lẫn `qa_response` trong SQLite WAL và `latest_dispatch.json`.
   - Kịch bản 3b (`/qa` 3 lần trượt -> `ESCALATED`): Đặt `NEXUS_GEMINI_QA_MOCK_VERDICT="NEEDS_WORK: Lỗi rò rỉ socket"`, tạo 1 dispatch mới rồi gọi `/qa` (hoặc MCP `run_gemini_qa_review`) 3 lần liên tiếp -> xác nhận lần 1 & 2 là `NEEDS_WORK` (`r1`, `r2`), lần 3 tự động chuyển sang `ESCALATED` (`r3`) và ghi đúng 1 dòng `[3-STRIKE ESCALATION]` vào `qa_report.md`.
4. **E4 (MCP `run_gemini_qa_review` -> Live Cockpit Sync)**:
   - Gọi tool `run_gemini_qa_review` qua `NEXUSHome.McpTools` (JSON-RPC stdio) -> xác nhận `CockpitUI` đang chạy tự động nhận được trạng thái và nội dung `qaResponse` mới trong `< 300 ms`.
5. **E5 (Memory Slimming Budget & Full Regression Suite — ADR-29)**:
   - Đo `Private Working Set` của `HostService` (đã mở `CockpitUI`, nạp 283 agents và chạy xong các bài test): xác nhận đạt **dưới 60 MB** (mục tiêu cải thiện về vùng **45–54 MB** nhờ bỏ `RegexOptions.Compiled` và lười hóa `HttpClient`), CPU nền `< 1%`.
   - Chạy lại toàn bộ `Verify-Task03.ps1` .. `Verify-Task07.ps1` đều PASS 100%.