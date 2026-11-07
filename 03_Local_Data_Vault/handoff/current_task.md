# ĐẶC TẢ KỸ THUẬT TASK-11: BỘ ĐỆM PHẢN HỒI XÁC ĐỊNH (`GeminiResponseCache` — ADR-44) & ĐỊNH VỊ NGỮ CẢNH KHO DỮ LIỆU CỤC BỘ (`VaultGroundingEngine` — ADR-45)

- **Phiên bản cơ sở:** `v0.10.0` (`main` @ `bf018d8` — Gate 10 PASSED: HTTP 429/5xx Exponential Backoff + Jitter, `GeminiQuotaGuard` RPM/TPM/Daily, `usageMetadata` HUD & MCP)
- **Nhánh triển khai:** `claude/task-11-vault-grounding-cache`
- **Hồ sơ phần cứng mục tiêu:** CPU Intel Core i5-12400 (6P/12T), iGPU Intel UHD 730 (24 EUs) xuất màn hình phụ MSI G272 144 Hz, dGPU GTX 1660 6 GB dành riêng cho Game/IDE, RAM 16 GB DDR4, bàn phím AULA HERO 68 HE (8000 Hz Polling).
- **Ngân sách tài nguyên bất khả xâm phạm (ADR-40 / Ràng buộc TASK-11):**
  - `HostService Private Working Set` **< 60,0 MB** (hiện tại `54,7 MB / 60,0 MB`), CPU nền khi nghỉ **< 1 %** (`0,0 %`).
  - **Không thêm gói NuGet mới.**
  - **Không giữ cache hoặc chỉ mục lớn trên Managed Heap.** Mọi dữ liệu cache phải nằm trong SQLite WAL (`nexus_memory.db`) và tệp trong `03_Local_Data_Vault/` phải được đọc theo luồng nhẹ (streaming).

---

## 1. Quyết định Kiến trúc Áp dụng trong TASK-11

| Mã ADR | Quyết định Kiến trúc | Trạng thái ở TASK-11 |
| :--- | :--- | :--- |
| **ADR-44** | **Deterministic Response Cache trên SQLite WAL (`GeminiResponseCache`):** Lưu kết quả Gemini theo khóa `SHA-256` của `(model_id \| normalized_prompt \| grounding_fingerprint)` với TTL cấu hình được (mặc định `3600s`) để trả lời tức thì (`< 2 ms`) với `0` token tiêu tốn cho các truy vấn lặp lại | **Triển khai ở TASK-11** |
| **ADR-45** | **Selective Local Vault Grounding (`VaultGroundingEngine`):** Trích xuất tối đa `3` phân đoạn ngắn (`<= 1 500` ký tự tổng cộng) từ `03_Local_Data_Vault/` bằng bộ lọc từ khóa xác định trên luồng nhẹ trước khi gửi lên Gemini (không dùng Vector DB nặng trên RAM) | **Triển khai ở TASK-11** |

---

## 2. Yêu cầu Kỹ thuật Chi tiết

### 2.1. Hạng mục 1 — Bộ đệm Phản hồi Xác định trên SQLite WAL (`GeminiResponseCache` — ADR-44)

1. **Bảng `gemini_response_cache` trong SQLite WAL (`nexus_memory.db`):**
   - Tạo bảng `gemini_response_cache` tự động trong `SqliteMemoryStore.InitializeSchema()` (bảo toàn 100 % dữ liệu hiện có `>= 1 296` dòng):
     - `cache_key TEXT PRIMARY KEY`: Chuỗi hex SHA-256 chữ thường của `model_id | normalized_prompt | grounding_fingerprint`.
     - `command_type TEXT NOT NULL`: Loại lệnh (`ask`, `ground`, `qa`).
     - `normalized_prompt TEXT NOT NULL`: Chuỗi truy vấn đã chuẩn hóa khoảng trắng.
     - `response_text TEXT NOT NULL`: Nội dung phản hồi đã lưu.
     - `prompt_tokens_saved INTEGER NOT NULL DEFAULT 0`: Tổng số prompt token đã tiết kiệm được qua các lần trúng cache (`hit_count * base_prompt_tokens`).
     - `completion_tokens_saved INTEGER NOT NULL DEFAULT 0`: Tổng số completion token đã tiết kiệm được qua các lần trúng cache (`hit_count * base_completion_tokens`).
     - `hit_count INTEGER NOT NULL DEFAULT 0`: Số lần trúng cache.
     - `created_utc TEXT NOT NULL`, `last_hit_utc TEXT NOT NULL`, `expires_utc TEXT NOT NULL` (TTL mặc định `3600` giây).
     - Các cột bổ trợ: `model_id TEXT`, `grounding_fingerprint TEXT`, `grounding_sources_json TEXT`, `base_prompt_tokens INTEGER`, `base_completion_tokens INTEGER`.

2. **Cơ chế Cache Hit (`< 2 ms`, 0 Token bị trừ thêm):**
   - Khi thực thi `/ask`, `/qa`, hoặc `/ground` (`/g`) trùng `cache_key` còn hạn TTL (`expires_utc > UtcNow`):
     - Trả về kết quả ngay trong **`< 2 ms`**.
     - Đánh dấu `cache_hit = true` (`cacheHit = true`) trong `latest_dispatch.json`.
     - **KHÔNG trừ quota** trong `GeminiQuotaGuard` (`0` RPM phát sinh thêm, `0` token trừ thêm vào ngân sách).
     - Cộng dồn `prompt_tokens_saved` + `completion_tokens_saved` trong `gemini_response_cache` và phản ánh tổng `saved_total_tokens` (`savedTotalTokens`).

3. **Cơ chế Bypass Cache (`/ask!`, `/ask --fresh`, `/f`):**
   - Tiền tố `/ask! <prompt>` hoặc `/ask --fresh <prompt>` (cũng như `/ground! <query>` / `/ground --fresh <query>`) bỏ qua bước đọc cache, gọi thẳng Gemini API và cập nhật lại bản ghi cache mới (giữ nguyên số thống kê `hit_count` và token đã tiết kiệm trước đó).
   - Lệnh `/f` (`/followup`) luôn gọi thẳng API theo cửa sổ trượt ADR-43.

---

### 2.2. Hạng mục 2 — Định vị Ngữ cảnh Kho Dữ liệu Cục bộ (`VaultGroundingEngine` — ADR-45)

1. **Quét & Chia phân đoạn theo luồng nhẹ (Streaming Section Extractor):**
   - Quét các tệp `.md` và `.json` chính thức trong `03_Local_Data_Vault/` (bỏ qua các tệp kết quả tự kiểm thử tạm `task*_selftest_*.json`, `task*_verification.json`, và bản sao lưu hỏng `*.invalid-*`).
   - Đọc theo dòng (`File.ReadLines` / `StreamReader`), chia tệp `.md` theo các tiêu đề `#`, `##`, `###` và chấm điểm khớp từ khóa xác định (deterministic keyword scoring: tiêu đề nhân hệ số cao hơn nội dung, cộng điểm khớp cụm từ).
2. **Giới hạn Top-3 Phân đoạn (`<= 1 500` ký tự):**
   - Chọn tối đa **Top-3** phân đoạn có điểm liên quan cao nhất, tổng độ dài văn bản đính kèm vào prompt **không vượt quá `1 500` ký tự**.
   - Xuất danh sách nguồn định dạng `[file#section]` (ví dụ `[handoff/current_task.md#ADR-44]`) vào trường `grounding_sources` (`groundingSources`) trong `latest_dispatch.json`.
   - Tính `grounding_fingerprint` (SHA-256 hex) từ các nguồn và nội dung trích xuất để kết hợp vào `cache_key` của ADR-44.

---

### 2.3. Hạng mục 3 — Slash Commands & Local MCP Server Tools

1. **Cockpit Slash Commands (`CockpitCommandProcessor` & `CockpitShellWindow`):**
   - `/cache` (hoặc `/cache stats`): Hiển thị thống kê bộ đệm SQLite WAL (tổng số mục, số mục còn hạn, tổng lượt `hit_count`, `prompt_tokens_saved`, `completion_tokens_saved`, `saved_total_tokens`, TTL).
   - `/cache clear`: Xóa sạch bảng `gemini_response_cache` và ghi nhận vào `system_audit_logs`.
   - `/ground <query>` (bí danh `/g <query>`, hỗ trợ `--auto-qa` và `--fresh` / `/ground!`): Trích xuất Top-3 phân đoạn từ `03_Local_Data_Vault/` qua `VaultGroundingEngine`, đính kèm vào câu hỏi cho Primary Agent, kiểm tra `GeminiResponseCache` (< 2 ms nếu trúng cache) và ghi `grounding_sources` vào `latest_dispatch.json`.
   - `/ask! <query>` (hoặc `/ask --fresh <query>`): Bypass cache cho `/ask`.

2. **Local MCP Server (`NEXUSHome.McpTools`):**
   - Công cụ `get_gemini_cache_stats` (`[CẤP XANH]`): Trả về JSON thống kê bộ đệm `gemini_response_cache` (`totalEntries`, `activeEntries`, `totalHits`, `promptTokensSaved`, `completionTokensSaved`, `savedTotalTokens`, `ttlSeconds`).
   - Công cụ `search_local_vault` (`[CẤP XANH]`): Nhận `query` (bắt buộc) và `maxSections` (tối đa 3), chạy `VaultGroundingEngine` (< 5 ms) và trả về danh sách phân đoạn kèm `grounding_sources`, `grounding_fingerprint`, `totalChars` (`<= 1 500`).

---

## 3. Tiêu chí Nghiệm thu Gate 11 (`01_Foundation_Engine/tools/Verify-Task11.ps1`)

- **E1 (Build, Zero-BOM & Schema11):**
  - `dotnet build -c Release -warnaserror` đạt `0 Warning(s)`, `0 Error(s)`.
  - `0` tệp `.cs`, `.xaml`, `.csproj`, `.md`, `.json` có UTF-8 BOM; các tệp `.ps1` có UTF-8 BOM.
  - Bảng `gemini_response_cache` được khởi tạo an toàn, bảo toàn 100 % bản ghi hiện có (`>= 1 296` dòng).
- **E2 (Deterministic Response Cache Hit < 2 ms & Cache Bypass):**
  - Lần gọi 1 (Cache Miss) lưu vào `gemini_response_cache`.
  - Lần gọi 2 lặp lại cùng câu hỏi (Cache Hit) hoàn tất trong **`< 2 ms`**, đánh dấu `cache_hit = true` trong `latest_dispatch.json`, **`0` token bị trừ thêm** trong `GeminiQuotaGuard`, và cộng dồn chính xác `saved_total_tokens` (`prompt_tokens_saved + completion_tokens_saved`).
  - Lệnh `/ask!` (`/ask --fresh`) và `/f` bypass cache, gọi thẳng API.
- **E3 (`VaultGroundingEngine` Top-3 <= 1 500 chars & `grounding_sources`):**
  - Lệnh `/ground <query>` (và `/g <query>`) trích xuất đúng phân đoạn từ `03_Local_Data_Vault/`, giới hạn `<= 3` phân đoạn và tổng độ dài `<= 1 500` ký tự, ghi nhận `grounding_sources` dạng `[file#section]` vào `latest_dispatch.json`, đồng thời lần gọi `/g` lặp lại trúng cache trong `< 2 ms`.
- **E4 (Slash Commands `/cache`, `/cache clear` & MCP Tools < 5 ms):**
  - Kiểm chứng `/cache` (`/cache stats`), `/cache clear` qua Named Pipe và trực tiếp.
  - Kiểm chứng MCP `get_gemini_cache_stats` và MCP `search_local_vault` phản hồi chính xác (thời gian trích xuất của `VaultGroundingEngine` `< 5 ms`).
- **E5 (Live DPAPI E2E `live-e2e11`, Tài nguyên & Hồi quy toàn diện 10..03):**
  - Với khóa thật DPAPI (`live-e2e11`): Gọi lệnh lần 1 qua mạng Gemini thật -> gọi lặp lại lần 2 trúng Cache `< 2 ms` với `0` token phát sinh thêm -> `/qa` đạt `PASSED`.
  - Chạy toàn bộ chuỗi hồi quy `Verify-Task10.ps1` -> `Verify-Task03.ps1` đạt **100 % ALL PASS**.
  - Đảm bảo `HostService Private WS < 60,0 MB` và CPU nền khi nghỉ `< 1,0 %` (`0 %`).
  - Cập nhật báo cáo bàn giao tại `03_Local_Data_Vault/handoff/gate11_handoff.md` và `03_Local_Data_Vault/handoff/qa_report.md` (§13).
