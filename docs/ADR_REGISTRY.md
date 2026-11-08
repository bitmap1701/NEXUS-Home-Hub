# Sổ Đăng Ký Quyết Định Kiến Trúc (ADR Registry) — NEXUS Home Hub

> **Luật dùng số (ADR-54)**
> 1. Mọi đặc tả mới (`current_task.md`), roadmap hoặc handoff **tra sổ này trước khi gán số ADR**. Số mới = số lớn nhất trong sổ + 1.
> 2. Không bao giờ dùng lại một số đã có, kể cả khi quyết định bị thay thế. Quyết định thay thế mang số mới và ghi "Thay thế ADR-xx".
> 3. Ai chốt số mới thì thêm dòng vào sổ trong cùng commit với đặc tả.
> 4. Hai lần trùng số trong lịch sử (ADR-44, ADR-45) được giữ cả hai nghĩa, phân biệt bằng hậu tố `a` / `b`. Chú thích trong code TASK-11 ghi "ADR-44" / "ADR-45" là **ADR-44b** / **ADR-45b**.
>
> **Nguồn**: lịch sử `current_task.md` và các commit "approve Gate … lock ADR-…" trên `main`, đối chiếu ngày 2026-11-08. Cột Commit là commit đầu tiên ghi nhận quyết định.
>
> **Trạng thái**: `Chốt` = đã phê chuẩn và có trong code/quy trình · `Chốt (chờ Gate)` = đã phê chuẩn, Gate chưa đóng · `Dự kiến` = giữ số cho task trong roadmap, nội dung chi tiết chốt khi viết đặc tả · `Bỏ` = không dùng.

| ADR | Tên | Task / Gate | Commit | Trạng thái |
|-----|-----|-------------|--------|------------|
| ADR-01 … ADR-04 | Không có bản ghi trong repo (quyết định trước TASK-03, chưa từng được ghi vào `current_task.md`) | TASK-01/02 | — | Không xác minh được |
| ADR-05 | In-Process Architecture: CockpitUI chạy chung tiến trình HostService, gọi trực tiếp SquadDispatcher và GameBoost | TASK-04 | `e11813e` | Chốt |
| ADR-06 | ACE-Guard Safe Game Boost: bảo vệ cửa sổ foreground + whitelist game/anti-cheat; Access Denied → Background Yield | TASK-04 | `e11813e` | Chốt |
| ADR-07 | Root Path Standardization: gốc `C:\Agent\NEXUS_Home_Hub`, tự dò gốc (`NexusPaths`, `NEXUS_ROOT`) | TASK-04 | `e11813e` | Chốt |
| ADR-08 | SQLite WAL Shared Access: `journal_mode=WAL`, `busy_timeout=5000` cho mọi kết nối | TASK-04 | `e11813e` | Chốt |
| ADR-09 | Memory Metric: `Private Working Set` < 60 MB là chỉ số bộ nhớ chính thức. Gate 14: thêm ngưỡng cảnh báo mềm 54 MB (báo, không trượt), trần cứng giữ 60 MB. TASK-13a: HUD và `/status` hiển thị đúng chỉ số này (`GetProcessMemoryInfo` EX2, dự phòng `QueryWorkingSet`), không còn `PrivateMemorySize64`; vùng Xanh mục tiêu ≤ 52 MB | TASK-05, Gate 14, TASK-13a | `59f266d`, `089abcb`, nhánh `claude/task-13a-memory-slimming` | Chốt (TASK-13a chờ Gate 13a) |
| ADR-10 | Command Mode Latency < 35 ms: đổi cờ cửa sổ + focus trên luồng chính, đọc feed SQLite ở `Background` | TASK-05 | `59f266d` | Chốt |
| ADR-11 | Deterministic ACE-Guard Test: giả lập Access Denied (Win32 5) cho `/boost` | TASK-05 | `59f266d` | Chốt |
| ADR-12 | Cockpit Dev-QA Slash Commands: `/pass`, `/fail` (`/needswork`), `/verdict` | TASK-05 | `59f266d` | Chốt |
| ADR-13 | 3-Strike Circuit Breaker: cột `retry_count`, `evidence_notes`; strike 3 → `ESCALATED` + dòng `[3-STRIKE ESCALATION]` | TASK-05 | `59f266d` | Chốt |
| ADR-14 | Two-Way Live Sync McpTools ↔ CockpitUI qua `latest_dispatch.json` (ghi nguyên tử, FileSystemWatcher) | TASK-05 | `59f266d` | Chốt (khôi phục ghi nguyên tử ở ADR-51) |
| ADR-15 | `current_task.md` = đặc tả task; `latest_dispatch.json` = trạng thái thời gian thực | TASK-05 | `59f266d` | Chốt |
| ADR-16 | Git & Encoding Discipline: `GEMINI.md` hợp nhất, UTF-8 không BOM (file `.ps1` có BOM) | TASK-05 | `59f266d` | Chốt |
| ADR-17 | Dual-Profile P95 Dispatch SLA: Desktop < 10 ms, LiveCombat < 25 ms | TASK-06 | `cece7f4` | Chốt |
| ADR-18 | Transactional Table-Rebuild Migration (BEGIN IMMEDIATE, sao lưu `VACUUM INTO`) | TASK-06 | `cece7f4` | Chốt |
| ADR-19 | Post-ESCALATED & Post-PASSED State Machine (không ghi trùng dòng escalation) | TASK-06 | `cece7f4` | Chốt |
| ADR-20 | Externalized Configuration `nexus_config.json` + `/reload` (`/config`) | TASK-06 | `cece7f4` | Chốt |
| ADR-21 | Tech-Debt Cleanup & Shared Logic (`NexusRuntime` dùng chung HostService/McpTools) | TASK-06 | `cece7f4` | Chốt |
| ADR-22 | Branch Consolidation: gộp các nhánh TASK-02..06 về `main` | TASK-06 | `cece7f4` | Chốt |
| ADR-23 | `combatProcessPrefixes` tách khỏi `protectedProcessPrefixes` (phát hiện LiveCombat) | TASK-07 | `5a2f06e` | Chốt |
| ADR-24 | Untracked Runtime Config & Seeded Files: `config/`, `*.dpapi`, 6 agent tự sinh trong `.gitignore` | TASK-07 | `5a2f06e` | Chốt |
| ADR-25 | `memoryTrimFloorMb` = ngưỡng Working Set tối thiểu để GameBoost xét `EmptyWorkingSet` | TASK-07 | `5a2f06e` | Chốt |
| ADR-26 | Idempotent Escalation & Reopen Rule: mở lại `PASSED` có r ≥ 3 → `ESCALATED`, tối đa 1 dòng escalation | TASK-07 | `5a2f06e` | Chốt |
| ADR-27 | Milestone Tag `v0.6.0` | Gate 6 | `5a2f06e` | Chốt |
| ADR-28 | Cold-Start `/ask` & `AskSyncBreakdown` (giả thuyết DPAPI/HttpClient) | TASK-08 | `86520e9` | Chốt, được đính chính bởi ADR-34 |
| ADR-29 | Memory Slimming: bỏ `RegexOptions.Compiled`, `Lazy<HttpClient>` | TASK-08 | `86520e9` | Chốt |
| ADR-30 | Automated Gemini Dev-QA Loop: `/qa`, `/ask --auto-qa`, MCP `run_gemini_qa_review` | TASK-08 | `86520e9` | Chốt |
| ADR-31 | Deterministic QA Mock Mode `NEXUS_GEMINI_QA_MOCK_VERDICT` | TASK-08 | `86520e9` | Chốt |
| ADR-32 | Optional Live API Test Policy: chưa có khóa → gọi thật `SKIPPED`, không chặn Gate | TASK-08 | `86520e9` | Chốt |
| ADR-33 | Milestone Tag `v0.7.0` | Gate 7 | `86520e9` | Chốt |
| ADR-34 | Empirical Correction to ADR-28 & `NexusRuntime.WarmUp()` | Gate 8 / TASK-09 | `ea62086` | Chốt |
| ADR-35 | Test Harness Refinements: CPU nhiều mẫu, khởi động HostService trước E4 | Gate 8 / TASK-09 | `ea62086` | Chốt |
| ADR-36 | Self-Correction Loop `/retry` + MCP `retry_gemini_dispatch`, cầu chì trên `/retry` | TASK-09 | `ea62086` | Chốt |
| ADR-37 | Multi-Turn Conversation `/followup` (`/f`), `turn_count`, `turn_history_json` | TASK-09 | `ea62086` | Chốt |
| ADR-38 | Two-Tier E2E Verification: mock tất định + gọi Gemini thật khi có khóa | TASK-09 | `ea62086` | Chốt |
| ADR-39 | Linear `main` & Milestone Tag `v0.8.0` | Gate 8 | `ea62086` | Chốt |
| ADR-40 | `/followup` Status Preservation (không đổi `status`) | Gate 9 / TASK-10 | `7b97945` | Chốt |
| ADR-41 | Transactional No-Op on Failed/Skipped `/retry` & `/f` | Gate 9 / TASK-10 | `7b97945` | Chốt |
| ADR-42 | Circuit Breaker Guard on `retry_count >= 3` (kể cả đã `PASSED`) | Gate 9 / TASK-10 | `7b97945` | Chốt |
| ADR-43 | `turn_history_json` Cap Separation: lưu × 3, gửi × 2 | Gate 9 / TASK-10 | `7b97945` | Chốt |
| **ADR-44a** | Dual Execution Semantics: Cockpit chạy nền sau bước chuẩn bị < 5 ms, McpTools chờ kết quả | Gate 9 / TASK-10 | `7b97945` | Chốt |
| **ADR-44b** | Deterministic Response Cache `GeminiResponseCache` trên SQLite WAL (trùng số với ADR-44a) | TASK-11 | `d0c24a1` | Chốt (`v0.11.0`) |
| **ADR-45a** | Prompt Construction Separation: prompt 4 phần cho `/retry`, `contents` đa lượt cho `/f` | Gate 9 / TASK-10 | `7b97945` | Chốt |
| **ADR-45b** | Selective Local Vault Grounding `VaultGroundingEngine` (trùng số với ADR-45a) | TASK-11 | `d0c24a1` | Chốt (`v0.11.0`) |
| ADR-46 | `Set-NexusGeminiKey.ps1` Validation (chống chuỗi rỗng) | Gate 9 / TASK-10 | `7b97945` | Chốt |
| ADR-47 | Live Gemini Resilience & Telemetry: backoff 429/5xx, RPM/TPM, `usageMetadata` | TASK-10 | `7b97945` | Chốt |
| ADR-48 | Shift Handover Protocol (`claude_shift_handover.md`) | Gate 9 | `7b97945` | Chốt |
| ADR-49 | — (bản nháp roadmap từng gán cho `/ctx`; không dùng để tránh nhầm) | — | `9e6fb6b` | Bỏ |
| ADR-50 | Live vs Mock Token Accounting: `IsLiveApi`, `live_*_tokens`, `mock_tokens_used`; hạn mức ngày chỉ đếm token thật | TASK-11b | `233ce7f` | Chốt (chờ Gate 11b) |
| ADR-51 | Atomic `latest_dispatch.json` Restored: file tạm + `File.Move(overwrite)` dưới khóa; bỏ `WriteFast`; `cache_lookup_ms`, `total_dispatch_ms` | TASK-11b | `233ce7f` | Chốt (chờ Gate 11b) |
| ADR-52 | Multi-Sample Latency Gates: 10 mẫu sau 1 warm-up; lookup P95 ≤ 2 ms; tổng P50 ≤ 2,5 ms, P95 ≤ 4 ms | TASK-11b | `233ce7f` | Chốt (chờ Gate 11b) |
| ADR-53 | Default Model `gemini-3.5-flash-lite` & Explicit Fallback (`[MODEL FALLBACK]`, audit, `model_used`); grounding loại trừ `*.db*`/`*.tmp`, hòa điểm `(Score, RelativePath, SectionIndex)` | TASK-11b | `233ce7f` | Chốt (chờ Gate 11b) |
| ADR-54 | ADR Registry Rule (sổ này) | Roadmap | (commit này) | Chốt |
| ADR-55 | Two-Tier Regression: `-FastRegression` (Gate hiện tại + Gate liền trước không gọi mạng + Task06, mục tiêu < 90 s) / `-FullRegression` (toàn chuỗi Task11 → 03, mặc định khi đóng tag). Lối vào: `tools/Invoke-NexusRegression.ps1`; chốt Git (nhánh + HEAD không đổi, cây sạch) trong lúc chạy. Quyết định (0b): ở tầng nhanh, ngưỡng ms vượt nhưng ≤ 2,5 × SLA thì `[SLA-WARN]` và PASS. TASK-13a: in `[SLA-WARN SUMMARY] Count=… \| Details=…`, `[DRIFT-ALERT]` khi > 2 cảnh báo trong một lượt hoặc cùng một metric ở 3 Gate liên tiếp (lịch sử `handoff/regression_sla_history.json`) | TASK-14b | nhánh `claude/task-14b-ci-fast-regression` | Đã triển khai, chờ Gate 14b |
| ADR-56 | GitHub Actions CI `.github/workflows/nexus-ci.yml`: push `main`, `claude/**`, `docs/**` và PR vào `main`; `windows-latest`, .NET 8 SDK; cổng 0 BOM trên `git ls-files` (`*.ps1` bắt buộc **có** BOM cho Windows PowerShell 5.1, mọi file khác không BOM); build HostService + McpTools `-warnaserror`; smoke McpTools JSON-RPC không màn hình | TASK-14b | nhánh `claude/task-14b-ci-fast-regression` | Đã triển khai, chờ Gate 14b |
| ADR-57 | Invariant Resource & Safety Budget cho mọi Phase (RAM, CPU, độ trễ, GPU, NuGet, bí mật, cấp thao tác) | Roadmap | (commit này) | Chốt |
| ADR-58 | Phase 3 Order Lock: Gate 11b → TASK-14 → TASK-13 → TASK-12 → TASK-15 | Roadmap | (commit này) | Chốt |
| ADR-59 | Zero-Token Local Utilities `/calc`, `/timer`, `/note`, MCP `run_local_utility`; ghi chú ở `zettelkasten/quick_notes.md`. Quyết định Gate 14: (1) mỗi bộ đếm một timer chạy một lần, 0 đối tượng khi rảnh, hết giờ báo bằng Passive HUD (`TIMERALERT`); (2) ô thứ 3 của Top-3 `/ground` dành cho ghi chú khớp ≥ 50 % từ khóa (bổ sung ADR-45b); (3) `/calc`: `^` = lũy thừa, `log` = ln, số âm in `-0x…` / `-0b…`; (4) Overwolf không phải tiến trình chiến đấu | TASK-14 | `ec01576`, `024b63a` | Chốt (`v0.12.0`) |
| ADR-60 | Interactive HUD Micro-Widgets: `WidgetCardExtractor` (code / checklist / utility, không Regex), `/copy` + `Alt+C` (clipboard Win32 một lần thử, không vòng thử lại OLE), `/pin` `/pins` `/unpin` + `Alt+P`, Right MFD 5 ô cố định từ `config/mfd_pins.json` (FIFO, Secret Guard, ghi nguyên tử + mutex), MCP `manage_hud_widgets`; `Alt+C`/`Alt+P` chỉ trong cửa sổ Cockpit ở Command mode, không `RegisterHotKey` | TASK-13 | `v0.13.0` @`e62ddb7` | ✅ Gate 13 PASS (lượt 1) |
| ADR-61 | Multimodal Vision: `/img` (clipboard PNG/DIB hoặc `--file`) và `/snap` (vùng làm việc MSI G272, khung Cockpit bị che đen) → JPEG ≤ `maxVisionImageBytes` (256 000 B), cạnh ≤ `maxVisionDimensionPx` (1280) bằng GDI+ (`System.Drawing.Common`, chất lượng 78 → 60 → 45 rồi thu 3/4); Combat-Mode Shield chặn trước mọi API màn hình; Privacy Window Guard (`privacyRedactedWindowKeywords`, cửa sổ foreground + mọi cửa sổ hiện trên vùng chụp); Vision cache `SHA-256("VISION_V1\|" + sha256(JPEG) + "\|" + prompt + "\|" + agent + "\|" + model)` trong bảng cache ADR-44; Gemini `inline_data`; MCP `analyze_visual_input`; kèm 2 tinh chỉnh Gate 13 (bỏ dòng xem trước trùng tiêu đề, cờ không lưu lịch sử Win+V). TASK-12b (Mô hình Bộ nhớ Hai Tầng với ADR-09: rảnh sau vision ≤ 45 MB, đỉnh ≤ 5 s chỉ đo, mềm 120 / 200 MB): clipboard giải mã tại chỗ (`GlobalLock` + `UnmanagedMemoryStream`), JPEG không `ToArray`, ảnh nguồn ≤ `maxSourceDimensionPx` (4096), nén LOH `CompactOnce` sau mỗi `/img` / `/snap`, hàm đo RAM dùng chung trong `NexusVerify.Common.ps1`. TASK-12c: `visionWorker` = `process` (mặc định: giải mã/nén/chụp trong `NEXUSHome.McpTools --vision-worker` ngắn hạn, khiên và cache vẫn ở HostService, HostService không nạp GDI+ cho vision) \| `inproc`; cột `is_test` + trả lại `latest_dispatch.json` sau selftest/Verify (B1) | TASK-12, 12b, 12c | nhánh `claude/task-12-multimodal-vision` | TASK-12c đã triển khai (CI xanh, 3 selftest PASS trên runner), chờ Gate 12 trên máy |
| ADR-62 | Active Window Context `/ctx` + Sensitive Window Denylist + Anti-Cheat Safe Guard | TASK-15 | — | Dự kiến |
| ADR-63 | Retrieval Ladder: SQLite FTS5 (BM25) ở v1.1 trước Vector RAG ở v1.2+ | Phase 6 | (commit này) | Chốt hướng |
| ADR-64 | Edge SLM Hardware Gate cho v2.0: i5-12400 không có NPU; GTX 1660 chỉ chạy SLM ngoài LiveCombat hoặc sau nâng cấp phần cứng | Phase 6 | (commit này) | Chốt hướng |

**Số tiếp theo còn trống: ADR-65.**
