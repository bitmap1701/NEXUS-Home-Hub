# 🔁 BÀN GIAO CA — Antigravity → Claude & Gemini

- **Thời điểm**: 2026-11-08 02:50 (giờ Việt Nam), Antigravity tạm bàn giao ca theo yêu cầu của Nhật Hòa.
- **Nhận ca**: **Claude** (Senior Implementation Specialist) tiếp tục hoàn thiện và nghiệm thu TASK-11; **Gemini** (Architect & Reality Checker) nghiệm thu và ra đặc tả tiếp theo.
- **Ràng buộc bất biến**: `GEMINI.md` (Minimal Change, UTF-8 no BOM cho source, .ps1 có BOM, HostService Private WS < 60 MB, 0% CPU khi nghỉ, không bao giờ lộ/ghi API key).

---

## 1. Trạng thái repo lúc bàn giao

| Mục | Trạng thái |
|-----|-----------|
| `main` | `bf018d8`, tag `v0.10.0` (Gate 10 đã nghiệm thu & đẩy lên remote) |
| Nhánh làm việc | `claude/task-11-vault-grounding-cache` (đã commit & push lên `origin`) |
| Commit mới nhất | `d0c24a1` (`feat(task-11): implement ADR-44 GeminiResponseCache and ADR-45 VaultGroundingEngine [WIP]`) |
| Build status | `dotnet build -c Release -warnaserror` **0 Warning(s), 0 Error(s)** |
| Trạng thái mã nguồn | Toàn bộ 13 file thay đổi đã được staged, committed và push lên GitHub |

---

## 2. Tiến độ triển khai TASK-11 (Hướng A: ADR-44 Response Cache & ADR-45 Local Vault Grounding)

### 2.1 Các module đã hoàn thành 100%:
1. **`01_Foundation_Engine/NEXUSHome.Core/VaultGroundingAndCache.cs`**:
   - `GeminiResponseCache`: SHA-256 key hashing (`model_id | normalized_prompt | grounding_fingerprint`), SQLite WAL CRUD, token saving, hit counting, TTL quản lý.
   - `VaultGroundingEngine`: Streaming scan `03_Local_Data_Vault/` (.md & .json), trích xuất section `#`/`##`/`###`, keyword scoring không cấp phát (zero-allocation `OrdinalIgnoreCase`), giới hạn Top-3 sections <= 1 500 chars, nguồn `[file#section]`.
2. **`NEXUSHomeHub.Core.cs`**:
   - Migration DDL tự động tạo bảng `gemini_response_cache` (15 cột).
   - Thiết lập `PRAGMA synchronous=NORMAL;` trên mọi pooled connection.
   - Thêm phương thức `TryCacheHitAndInsertDispatch` và `TryCacheHitAndApplyQaVerdict` (dùng `UPDATE ... RETURNING` tối ưu trong 1 giao dịch duy nhất).
3. **`SquadDispatcher.cs` & `GeminiAgentClient.cs`**:
   - `TryDispatchFromCache` trả về kết quả ngay (< 2 ms) khi cache hit.
   - `TryExecuteCachedConsult` và `TryExecuteCachedQa` tích hợp cache vào luồng gọi.
   - Bỏ qua cache khi có tiền tố `/ask!` hoặc `/ask --fresh`. Lệnh `/f` luôn gọi API theo ADR-43.
4. **`CockpitCommandProcessor.cs` & `CockpitShellWindow.xaml.cs`**:
   - Thêm lệnh `/cache` (`/cache stats`), `/cache clear`, `/ground` (`/g`), `/ask!` (`/ask --fresh`).
   - Cập nhật hiển thị HUD và Named Pipe snapshot với `cacheHit`, `groundingSources`, token tiết kiệm.
5. **`NEXUSHome.McpTools/Program.cs`**:
   - Thêm 2 tool MCP mới: `get_gemini_cache_stats` và `search_local_vault` (< 5 ms).
6. **Kiểm thử nghiệm thu**:
   - `SelfTest.Task11.cs`: kịch bản `schema11`, `cache-hit-bypass`, `vault-grounding`, `cache-commands`, `live-e2e11`.
   - `Verify-Task11.ps1`: bộ kịch bản E1–E5 (UTF-8 WITH BOM).

### 2.2 Điểm cần Claude xử lý tiếp theo:
1. **Chạy kiểm thử nghiệm thu E2 (`cache-hit-bypass`)**:
   - Chạy `Verify-Task11.ps1` (hoặc set biến mock: `$env:NEXUS_GEMINI_MOCK_RESPONSE` và `$env:NEXUS_GEMINI_QA_MOCK_VERDICT` trước khi gọi `--selftest cache-hit-bypass`).
   - Kiểm tra `qaCacheHitUnder2Ms` và `hit1`/`hit2` đều đạt tiêu chí < 2.0 ms.
2. **Chạy toàn bộ chuỗi nghiệm thu Gate 11**:
   ```powershell
   cd C:\Agent\NEXUS_Home_Hub
   powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task11.ps1 -SkipBuild
   ```
   (Script sẽ chạy E1..E5, gồm cả chuỗi hồi quy từ Gate 10 trở về Gate 3 và đo RAM/CPU của HostService).
3. **Cập nhật tài liệu nghiệm thu & commit**:
   - Cập nhật `03_Local_Data_Vault/handoff/qa_report.md` (thêm mục §13 cho Gate 11).
   - Tạo `03_Local_Data_Vault/handoff/gate11_handoff.md`.
   - Commit các tài liệu nghiệm thu và push lên nhánh `claude/task-11-vault-grounding-cache` để chuyển cho Gemini nghiệm thu.
