# TASK-07: Nối Gemini API Vào CockpitUI & McpTools, Bảo Mật Khóa Bằng Windows DPAPI (Hướng A)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 6 — Mốc `v0.6.0`)
- **Nghiệm thu Gate 6**: `PASSED` 100% trên nhánh `main` (Build `-warnaserror` 0/0; 4 file không BOM; `nexus_config.json` tự phục hồi khi JSON hỏng và hỗ trợ `/reload` tức thì; P95 dispatch 2.97 ms; Private Working Set giảm còn 44.1 MB; CPU nền 0.13%).
- **5 Quyết định Kiến trúc Đã Chốt (ADR-23 .. ADR-27)**:
  1. **ADR-23 (`combatProcessPrefixes` vs `protectedProcessPrefixes`)**: Tách riêng `combatProcessPrefixes` (chỉ gồm các tiến trình Game & Kernel Anti-Cheat) để phát hiện SLA `LiveCombat` (25 ms) chính xác, tách biệt với `protectedProcessPrefixes` (bao gồm cả `dwm`, `explorer`, `audiodg`).
  2. **ADR-24 (Untracked Runtime Config & Seeded Files)**: Giữ `03_Local_Data_Vault/config/` (gồm `nexus_config.json`, các bản sao lưu và file khóa `.dpapi`) cùng 6 file agent tự sinh trong `.gitignore` để `git status` luôn sạch.
  3. **ADR-25 (`memoryTrimFloorMb` Semantics)**: Là ngưỡng Working Set tối thiểu (`WorkingSet64 > memoryTrimFloorMb * 1MB`) để `GameBoost` xét gọi `EmptyWorkingSet`, bỏ qua các tiến trình nhỏ để tiết kiệm syscall.
  4. **ADR-26 (Idempotent Escalation & Reopen Rule)**: Mở lại một bản ghi `PASSED` đã có `retry_count >= 3` bằng `/fail` sẽ chuyển thẳng về `ESCALATED`, và mỗi `dispatchId` chỉ xuất hiện tối đa đúng 1 dòng `[3-STRIKE ESCALATION]` trong `qa_report.md` suốt vòng đời.
  5. **ADR-27 (Milestone Tag `v0.6.0`)**: Khóa mốc `v0.6.0` trên `main` cho toàn bộ phần Nền móng Cục bộ (Phase 1–6).

## 2. Mục Tiêu TASK-07 (Hướng A — Wire Gemini API into CockpitUI with Windows DPAPI Vault)
Thổi "trí tuệ thời gian thực" của Gemini API vào bộ khung 283 Agents ngay trên màn hình phụ **MSI Optix G272** và `NEXUSHome.McpTools`:
1. **Bảo mật Khóa API Cấp Hệ Điều Hành (`DpapiSecretVault`)**: Mã hóa và lưu trữ Gemini API Key bằng **Windows DPAPI (`ProtectedData.Protect` với `DataProtectionScope.CurrentUser`)** tại `03_Local_Data_Vault/config/gemini_key.dpapi`. Chỉ tài khoản Windows của Nhật Hòa trên đúng chiếc PC này mới giải mã được.
2. **Quản lý Khóa Ngay Trên `CockpitUI`**: Hỗ trợ lệnh `/apikey set <key>`, `/apikey status`, `/apikey clear` với kỷ luật **Zero-Secret-Leak** (tuyệt đối không bao giờ ghi khóa API ra SQLite, `latest_dispatch.json`, log hay màn hình).
3. **Truy vấn Chuyên Gia Gemini Bất Đồng Bộ (`/ask <nhiệm vụ/câu hỏi>`)**:
   - Bước 1 (Tức thì `< 5 ms`): Gọi `SquadDispatcher` chọn đúng Squad + nạp `SystemInstruction` từ hồ sơ `.md` của Primary Agent + lưu bản ghi `DISPATCHED` vào SQLite WAL + **trả Focus bàn phím về ngay cho Game/IDE**.
   - Bước 2 (Chạy ngầm Async không khóa UI): Gửi `SystemInstruction` (đã ghép ngữ cảnh phần cứng i5-12400 + GTX 1660) và câu hỏi lên Gemini API (`generateContent`), nhận câu trả lời và hiển thị trực tiếp lên khung **Agent Response View** trên `CockpitUI` (màn hình MSI G272), đồng thời lưu vào SQLite WAL và `latest_dispatch.json`.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Kho Khóa Bảo Mật Windows DPAPI (`DpapiSecretVault.cs` trong Shared Core)
- **Đường dẫn lưu trữ**: `03_Local_Data_Vault/config/gemini_key.dpapi` (đảm bảo `*.dpapi` nằm trong `.gitignore`).
- **API Mã hóa / Giải mã**:
  - Sử dụng `System.Security.Cryptography.ProtectedData.Protect(plainBytes, optionalEntropy, DataProtectionScope.CurrentUser)` (gói NuGet `System.Security.Cryptography.ProtectedData` nếu cần hoặc có sẵn trong `net8.0-windows`).
  - Dùng một mảng `optionalEntropy` cố định nội bộ (ví dụ chuỗi định danh `"NEXUS_HOME_HUB_DPAPI_V1"`) để tăng cường cô lập.
- **Thứ tự ưu tiên lấy khóa (`ResolveApiKey`)**:
  1. Đọc và giải mã từ `03_Local_Data_Vault/config/gemini_key.dpapi`.
  2. Nếu chưa có file `.dpapi`, kiểm tra biến môi trường `GEMINI_API_KEY`.
  3. Nếu cả hai đều không có, trả về trạng thái `Missing` (không ném lỗi).
- **Hàm che khóa an toàn (`MaskApiKey`)**:
  - Chỉ hiển thị 4 ký tự đầu và 3 ký tự cuối kèm độ dài (ví dụ: `AIza...9xQ (39 chars, Source: DPAPI)`).
- **Lệnh Slash Commands trên `CockpitUI`**:
  - `/apikey set <raw_key>`: Mã hóa lưu vào `gemini_key.dpapi`, xóa ngay chuỗi `<raw_key>` khỏi ô nhập lệnh, KHÔNG ghi lệnh này vào `agent_dispatch_logs` hay `latest_dispatch.json`, và hiển thị thông báo đã mã hóa DPAPI thành công kèm chuỗi `MaskApiKey`.
  - `/apikey status`: Hiển thị trạng thái khóa hiện tại (`Configured via DPAPI`, `Configured via ENV`, hoặc `Not Configured`) và tên model Gemini đang cấu hình.
  - `/apikey clear`: Xóa file `gemini_key.dpapi` và cập nhật trạng thái.

### 3.2. Mở Rộng `nexus_config.json` & Schema SQLite WAL
1. **Bổ sung trường cấu hình Gemini vào `NexusConfig` (`nexus_config.json`)**:
   - `"geminiModel": "gemini-2.5-flash"`
   - `"geminiTimeoutSeconds": 25`
   - `"geminiMaxOutputTokens": 1024`
   - `"geminiTemperature": 0.3`
   *(Đảm bảo tương thích ngược: các file `nexus_config.json` hiện có từ TASK-06 khi nạp lên sẽ tự nhận các giá trị mặc định này).*
2. **Mở rộng bảng `agent_dispatch_logs` (Migration an toàn bảo toàn dữ liệu)**:
   - Bổ sung 3 cột mới (qua `ALTER TABLE ... ADD COLUMN` nếu chưa tồn tại hoặc qua cơ chế migration idempotent):
     - `ai_response TEXT NOT NULL DEFAULT ''`
     - `ai_model TEXT NOT NULL DEFAULT ''`
     - `ai_latency_ms INTEGER NOT NULL DEFAULT 0`
   - Cập nhật `latest_dispatch.json` có thêm 3 trường: `aiResponse`, `aiModel`, `aiLatencyMs`.

### 3.3. Bộ Máy Gọi Gemini API (`GeminiAgentClient.cs` — Shared Core)
- **Endpoint chuẩn**: `https://generativelanguage.googleapis.com/v1beta/models/{geminiModel}:generateContent`
  - Truyền khóa API qua header `x-goog-api-key: <apiKey>` (an toàn hơn truyền trên URL query string vì tránh lộ khóa trong log URL nếu có).
- **Cấu trúc Request JSON gửi lên Gemini**:
  - `system_instruction`: `{ "parts": [ { "text": "<Nội dung Markdown của Primary Agent + Ngữ cảnh phần cứng Nhật Hòa + Yêu cầu trả lời ngắn gọn, súc tích, thực chiến cho HUD>" } ] }`
  - `contents`: `[ { "role": "user", "parts": [ { "text": "<Câu hỏi / Nhiệm vụ của người dùng>" } ] } ]`
  - `generationConfig`: `{ "temperature": <geminiTemperature>, "maxOutputTokens": <geminiMaxOutputTokens> }`
- **Hỗ trợ Chế độ Giả lập Kiểm thử Tất định (Deterministic Mock Mode cho `Verify-Task07.ps1`)**:
  - Nếu biến môi trường `NEXUS_GEMINI_MOCK_RESPONSE` được đặt (hoặc trong chế độ test nội bộ), `GeminiAgentClient` không gọi mạng ra ngoài mà trả về ngay chuỗi phản hồi giả lập (kèm tên `PrimaryAgent` và `SquadId`) với `aiModel = "gemini-mock-verifier"`. Nhờ đó script kiểm thử tự động `Verify-Task07.ps1` luôn kiểm tra được trọn vẹn 100% luồng DPAPI -> Dispatch -> Gemini Client -> SQLite WAL -> `latest_dispatch.json` -> CockpitUI mà không phụ thuộc mạng Internet hay tốn quota thật.
- **Xử lý Lỗi Mạng / Chưa Có Khóa (Graceful Degradation)**:
  - Nếu người dùng gọi `/ask` khi chưa cài khóa API, hoặc khi mất mạng / hết quota (HTTP 400/401/403/429/5xx hoặc Timeout):
    - Bản ghi điều phối Squad cục bộ vẫn thành công (`DISPATCHED`).
    - Trường `ai_response` ghi thông báo chẩn đoán ngắn gọn, rõ ràng (ví dụ: `"[LOCAL DISPATCH ONLY] Chưa cấu hình Gemini API Key. Hãy gõ /apikey set <KEY> để kích hoạt."` hoặc `"[GEMINI API ERROR HTTP 429] Quota exceeded / Rate limited."`) và tuyệt đối không làm văng ứng dụng.

### 3.4. Giao Diện `CockpitUI` & Lệnh `/ask` + MCP Tool `consult_gemini_agent`
1. **Trên `CockpitUI` (`Ctrl+Shift+Space`)**:
   - **Lệnh `/ask <câu hỏi / nhiệm vụ>`**:
     - Thực hiện ngay (Synchronous `< 5 ms`): Gọi `SquadDispatcher` chọn Squad & Primary Agent -> ghi bản ghi `DISPATCHED` -> hiển thị lên thẻ Active Card trạng thái `[THINKING... Calling Gemini]` -> **trả Focus bàn phím ngay lập tức về cửa sổ trước đó (Game / IDE)**.
     - Chạy ngầm (Async Task): Gọi `GeminiAgentClient` -> khi có kết quả, cập nhật SQLite WAL (`ai_response`, `ai_model`, `ai_latency_ms`), ghi `latest_dispatch.json` và hiển thị nội dung câu trả lời trong khung **AI Response Box** (có thanh cuộn nhẹ hoặc giới hạn dòng gọn gàng trên màn MSI G272, giữ nguyên `RenderMode.SoftwareOnly`).
   - **Gõ câu tự nhiên (không có `/ask`)**: Giữ nguyên hành vi điều phối cục bộ siêu tốc (`< 3 ms`) như TASK-04..06 để không phá vỡ bất kỳ bài kiểm thử hồi quy nào của `Verify-Task04/05/06`.
2. **Trên `NEXUSHome.McpTools` (JSON-RPC stdio)**:
   - Bổ sung tool thứ 5: `consult_gemini_agent` (Cấp Xanh):
     - Tham số: `taskDescription` (string, bắt buộc), `nexusMode` (string, tùy chọn), `preferredAgentId` (string, tùy chọn).
     - Thực thi điều phối Squad + gọi `GeminiAgentClient` + lưu SQLite WAL + cập nhật `latest_dispatch.json` (để `CockpitUI` tự động hiển thị câu trả lời qua `FileSystemWatcher`) và trả về cả thông tin Squad lẫn `aiResponse` cho MCP client.

## 4. Verification Steps (Nghiệm Thu Gate 7 — `Verify-Task07.ps1`)
1. **E1 (Build `-warnaserror` & Schema Migration Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo.
   - Bảng `agent_dispatch_logs` có đủ 3 cột mới (`ai_response`, `ai_model`, `ai_latency_ms`) và giữ nguyên toàn bộ các dòng lịch sử hiện có.
2. **E2 (DPAPI Secret Vault Round-Trip & Zero-Plaintext Security Test)**:
   - Lưu thử một khóa giả lập (ví dụ `AIzaSyTestKey_NEXUS_DPAPI_Verify_123456`) qua `DpapiSecretVault` (hoặc lệnh `/apikey set`).
   - Kiểm tra file `03_Local_Data_Vault/config/gemini_key.dpapi` tồn tại trên đĩa, đọc mảng byte thô trên đĩa xác nhận **KHÔNG chứa chuỗi văn bản thuần** `AIzaSyTestKey`.
   - Giải mã lại qua `DpapiSecretVault` xác nhận khớp 100% chuỗi gốc, và hàm `MaskApiKey` che đúng định dạng.
   - Kiểm tra `agent_dispatch_logs` và `latest_dispatch.json` xác nhận **không hề bị rò rỉ** chuỗi khóa thử nghiệm.
   - (Khôi phục lại khóa thật nếu trước đó người dùng đã cài, hoặc dọn sạch khóa test).
3. **E3 (End-to-End `/ask` Flow & `consult_gemini_agent` MCP Tool with Deterministic Mock)**:
   - Đặt chế độ mock kiểm thử (`NEXUS_GEMINI_MOCK_RESPONSE`), gọi lệnh `/ask Tối ưu C# SocketAsyncEventArgs chống phân mảnh bộ nhớ` và gọi MCP tool `consult_gemini_agent`.
   - Xác nhận hệ thống chọn đúng `Squad1`, lưu `ai_response`, `ai_model`, `ai_latency_ms` vào SQLite WAL và cập nhật đầy đủ vào `latest_dispatch.json`.
4. **E4 (Graceful Missing-Key / Error Handling & Non-Blocking Focus)**:
   - Thử gọi `/ask` khi tạm thời không có khóa API -> xác nhận không quăng ngoại lệ, vẫn tạo bản ghi `DISPATCHED` và trả về thông báo hướng dẫn `[LOCAL DISPATCH ONLY]` rõ ràng.
5. **E5 (Live Gemini API Test — Nếu Nhật Hòa đã nạp khóa thật — & Full Regression Suite)**:
   - Nếu đã có khóa thật trong DPAPI/ENV, thực hiện 1 lệnh `/ask` thật tới model `gemini-2.5-flash` và ghi nhận `ai_latency_ms`.
   - Chạy lại toàn bộ `Verify-Task03.ps1`, `Verify-Task04.ps1`, `Verify-Task05.ps1`, `Verify-Task06.ps1` và `Verify-Task07.ps1` đều PASS 100%; `Private Working Set` dưới 60 MB, CPU nền dưới 1%.