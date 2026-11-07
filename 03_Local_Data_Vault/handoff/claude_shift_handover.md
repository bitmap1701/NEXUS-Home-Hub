# 🔁 BÀN GIAO CA — Claude → Antigravity & Gemini Web

- **Thời điểm**: 2026-11-07 23:25 (giờ Việt Nam), Claude kết thúc ca theo yêu cầu của Nhật Hòa.
- **Nhận ca**: **Antigravity** (Local IDE Executor) viết code và chạy kiểm thử; **Gemini Web** (Architect & Reality Checker) nghiệm thu và ra đặc tả.
- **Luật vẫn giữ nguyên**: `GEMINI.md` (phần cứng, Minimal Change, Xanh/Vàng/Đỏ, cầu chì 3 lần thử).

---

## 1. Trạng thái repo lúc bàn giao

| Mục | Trạng thái |
|-----|-----------|
| `main` | `ea62086`, tag `v0.8.0` (Gate 8 đã nghiệm thu) |
| Nhánh đang chờ | `claude/task-09-retry-followup-e2e`, hơn `main` 4 commit: `bbd6b80` (tài liệu Gate 8 lần chạy 3), `31ee4a8` (code TASK-09), `d7b4950` (tài liệu Gate 9 + sửa thông báo của `Set-NexusGeminiKey.ps1`), và commit chứa file bàn giao này |
| Gate 9 | ✅ Verify-Task09 ALL PASS (E1–E5) trên máy Nhật Hòa lúc 23:23. **Chờ Gemini nghiệm thu** |
| Gọi Gemini thật | ❌ **Chưa kiểm ở Gate nào** (chưa nạp được khóa). Mọi bằng chứng Gemini đến nay đều từ mock |
| Cây làm việc | Sạch, không có thay đổi chưa commit |

Tài liệu nghiệm thu: `gate9_handoff.md` (khung 5 mục), `qa_report.md` §11.

---

## 2. Việc của Gemini Web

1. Nghiệm thu Gate 9 theo `gate9_handoff.md` §2.
2. Quyết định 9 điểm mở ở `gate9_handoff.md` §3. Bốn điểm quan trọng nhất: `/f` không đổi `status`; `/retry` không có câu trả lời thì không ghi gì; cầu chì chặn cả `retry_count ≥ 3` khi đã `PASSED`; giới hạn lưu `maxConversationTurns × 2` mục tính cả mục `qa`.
3. Nếu PASS: fast-forward `main` tới **đầu nhánh** (`origin/claude/task-09-retry-followup-e2e`, gồm cả file bàn giao này), không dừng ở commit code: đã 2 lần commit tài liệu bị bỏ lại ngoài `main`. Gắn tag `v0.9.0`.
4. Viết đặc tả TASK-10 vào `03_Local_Data_Vault/handoff/current_task.md` trên `main` (UTF-8 không BOM). Claude khuyến nghị **Hướng A: độ bền khi gọi thật** (429/5xx có backoff theo `Retry-After`, giới hạn gọi/phút và token/ngày trong `nexus_config.json`, đếm `usageMetadata` theo dispatch trên HUD). Nên có số liệu E5 thật trước khi chốt ngưỡng.

---

## 3. Việc của Antigravity

### 3.1 Ngay khi nhận ca (không cần đặc tả mới)
Chạy E5 với khóa thật để có số liệu đầu tiên:
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-09-retry-followup-e2e
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Set-NexusGeminiKey.ps1   # dán khóa bằng Ctrl+V; ô nhập không hiện gì
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task09.ps1 -SkipRegression
```
Ghi kết quả E5 (độ trễ `/ask`, `/f`, `/qa`, model thật, lỗi nếu có) vào `qa_report.md` §11 thành mục §11.4. Nếu gặp 429/hết quota, ghi nguyên văn chẩn đoán: đó chính là đầu vào cho TASK-10.

### 3.2 Khi làm TASK-10
- Tạo nhánh mới từ `main` sau khi Gemini đã fast-forward; không commit thẳng vào `main`.
- **Thêm `task1*` vào `.gitignore`**: mẫu hiện tại `task0*_selftest_*.json`, `task0*_verification.json` không bao phủ `task10_…`, và Verify-Task06 E5 sẽ trượt vì `git status` bẩn.
- Mỗi `Verify-TaskNN.ps1` gọi lại bài trước (`09 → 08 → … → 03`), mất khoảng 8 phút. Verify-Task10 nên gọi `Verify-Task09.ps1 -SkipBuild`.

---

## 4. Những điều Claude rút ra (để khỏi trượt lại)

| # | Bài học | Nguồn |
|---|---------|-------|
| 1 | **Không khởi động HostService chạy lâu dài trước các bước đo độ trễ.** Đo trong ~2 s sau khi nó khởi động sẽ đo nhầm tranh chấp giữa hai tiến trình (2,9 s ở Gate 7, 409 ms ở Gate 8). Verify-Task08/09 chỉ khởi động nó trước E4, chờ thêm 3 s | ADR-34 |
| 2 | **`NexusRuntime.WarmUp()`** trả trước chi phí JIT/SQLite/persona lần đầu (không warm-up: Cold Call #1 = 113 ms; có: 9,4 ms). Đường đồng bộ mới phải được warm-up hoặc được đo sau một lệnh cùng loại | Gate 8 |
| 3 | **Đo CPU bằng 10 mẫu × 1 s, chấm trung bình 5 mẫu cuối**, không dùng một cửa sổ 3 s | ADR-35 |
| 4 | **Biến mock** (`NEXUS_GEMINI_MOCK_RESPONSE`, `NEXUS_GEMINI_QA_MOCK_VERDICT`) chỉ đặt cho tiến trình con của script, không bao giờ cho HostService chạy lâu dài | Gate 8–9 |
| 5 | **Mã hóa**: `.ps1` là UTF-8 **có** BOM (PowerShell 5.1); mọi file khác (`.cs`, `.xaml`, `.csproj`, `.md`, `.json`) **không** BOM. Verify-Task09 E1 quét 39 file. Ghi chú XAML không được chứa `--` | Gate 6, 8 |
| 6 | **Khóa API**: không bao giờ ghi khóa (kể cả khóa kiểm thử DPAPI) vào báo cáo, log hay tham số dòng lệnh. Nhập khóa chỉ qua `/apikey set` hoặc `Set-NexusGeminiKey.ps1` | ADR-24 |
| 7 | **Dữ liệu cũ**: mọi cột mới thêm bằng `ALTER TABLE … ADD COLUMN` idempotent có `DEFAULT`, kiểm số dòng trước = sau (hiện 871 dòng) | TASK-05..09 |
| 8 | **Không `git push --force`**. Nếu `main` cục bộ bị từ chối push thì `git pull` trước | Gate 7 |

---

## 5. Bản đồ code TASK-09 (để đọc nhanh)

| Chức năng | Vị trí |
|-----------|--------|
| `/retry`, `/f` (phân tích cờ, đo đồng bộ) | `NEXUSHome.Core/CockpitCommandProcessor.cs` → `ExecuteRefine`, `ParseRefineArgs` |
| Luồng retry/follow-up, cầu chì, prompt tự sửa, QA nối tiếp | `NEXUSHome.Core/GeminiAgentClient.cs` → `AgentConsultService.StartRetry/StartFollowUp/CompleteRetryAsync/CompleteFollowUpAsync/RefineAsync` |
| Gọi Gemini nhiều lượt + mock `[MOCK-…-TURN-n]` | `GeminiAgentClient.GenerateAsync(…, IReadOnlyList<GeminiTurn>, …, mockTag)`, `MockEcho` |
| Lịch sử lượt, cửa sổ `maxConversationTurns` | `NEXUSHome.Core/TurnHistory.cs` |
| Ghi lượt nguyên tử | `NEXUSHomeHub.Core.cs` → `SaveRefinedTurn`, `AppendTurnEntry` |
| Dựng lại SystemInstruction từ dòng SQLite | `SquadDispatcher.BuildInstructionFor` |
| MCP `retry_gemini_dispatch`, `followup_gemini_dispatch` | `NEXUSHome.McpTools/Program.cs` → `ExecuteRefine` |
| Kiểm thử | `NEXUSHome.HostService/SelfTest.cs` (`schema09`, `retry-loop`, `retry-breaker`, `followup-chain`, `live-e2e`, `apikey-status`), `tools/Verify-Task09.ps1` |

---

## 6. Lời nhắn dán cho Antigravity

> Chào Antigravity, bạn nhận ca từ Claude. Đọc `03_Local_Data_Vault/handoff/claude_shift_handover.md` trước (trạng thái repo, việc cần làm, bài học kiểm thử), rồi `gate9_handoff.md` và `qa_report.md` §11. Gate 9 (TASK-09: `/retry`, `/followup`, cầu chì trên `/retry`) đã ALL PASS trên nhánh `claude/task-09-retry-followup-e2e`, đang chờ Gemini nghiệm thu; `main` vẫn ở `v0.8.0`. Việc đầu tiên: nạp khóa Gemini bằng `tools\Set-NexusGeminiKey.ps1` rồi chạy `Verify-Task09.ps1 -SkipRegression` để lần đầu có số liệu gọi Gemini thật (E5), ghi vào `qa_report.md` §11.4. Khi làm TASK-10: tạo nhánh mới từ `main`, thêm `task1*` vào `.gitignore`, không khởi động HostService trước các bước đo độ trễ, `.ps1` có BOM còn mọi file khác không BOM, không bao giờ ghi khóa vào log/báo cáo.
