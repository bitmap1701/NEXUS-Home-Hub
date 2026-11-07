# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 9 (TASK-09) & Đề xuất TASK-10

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-09-retry-followup-e2e`, tách từ `main` (`ea62086`, sau tag `v0.8.0`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-09, đã có trên `main`)
- **Chi tiết kỹ thuật**: `qa_report.md` §11

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Tự sửa | QA chấm `NEEDS_WORK` xong thì người dùng phải tự hỏi lại | `/retry [#id] [--auto-qa] [--force] [gợi ý]`: Primary Agent nhận nhiệm vụ gốc + câu trả lời trước + nhận xét QA + gợi ý, viết lại; `--auto-qa` cho QA chấm lại ngay |
| 2 | Cầu chì | Chỉ chặn ở bước chấm (`ESCALATED` ở strike 3) | `/retry` trên dispatch `ESCALATED` (hoặc `retry_count ≥ 3`) bị chặn trước khi gọi Gemini; `--force` mới vượt; `/retry` không tự tăng `retry_count` |
| 3 | Hội thoại | Mỗi `/ask` là một câu hỏi rời | `/followup` (`/f`) `[#id] [--auto-qa] <câu hỏi>`: gửi kèm tối đa `maxConversationTurns` (6) cặp lượt gần nhất |
| 4 | Dữ liệu | — | Cột `turn_count`, `turn_history_json` (ALTER idempotent); `latest_dispatch.json` thêm `turnCount`; `nexus_config.json` thêm `maxConversationTurns` (không bắt buộc) |
| 5 | Giao diện | THINKING / QA REVIEWING | Thêm `[RETRYING... Primary Agent refining]`, `[FOLLOW-UP... <agent> turn n]`, `Turn: n` trên thẻ |
| 6 | MCP | 10 tool | + `retry_gemini_dispatch` (Cấp Vàng), `followup_gemini_dispatch` (Cấp Xanh; Vàng khi `autoQa`) |
| 7 | Mock | `[MOCK GEMINI] …` | `/retry`, `/f` trả `[MOCK-RETRY-TURN-n]` / `[MOCK-FOLLOWUP-TURN-n]` kèm phần lấy từ chính request (số lượt gửi, đầu nhận xét QA, gợi ý, lượt `model` trước) |
| 8 | Khóa thật | Chỉ có `/apikey set` trong Cockpit | + `tools/Set-NexusGeminiKey.ps1` (nhập ẩn, DPAPI, HostService giải mã kiểm tra rồi in khóa đã che) |
| 9 | Kiểm thử | — | Selftest `schema09`, `retry-loop`, `retry-breaker`, `followup-chain`, `live-e2e`, `apikey-status`; `Verify-Task09.ps1` (E1–E5) |

---

## 2. Trạng thái Gate 9 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (chưa build)

Môi trường của Claude không có .NET SDK nên **chưa build, chưa chạy**; chưa có số đo nào. Cần Nhật Hòa chạy `Verify-Task09.ps1`.

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-09-retry-followup-e2e
git pull
# (tùy chọn, để E5 gọi Gemini thật) nạp khóa - khóa không hiện trên màn hình:
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Set-NexusGeminiKey.ps1
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task09.ps1
```
Hồi quy đầy đủ (Verify-Task08 → 07 → 06 → 05 → 04 → 03) mất khoảng 7–8 phút. Biến mock chỉ được đặt cho tiến trình con của script, không bao giờ cho HostService chạy lâu dài. Không có khóa thì E5 ghi `SKIPPED (No live key configured)` và vẫn chấm phần RAM/CPU/hồi quy.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + `--selftest schema09` + quét BOM | `-warnaserror` 0/0; có `turn_count`, `turn_history_json`; số dòng trước = sau; mọi dòng có dữ liệu lượt hợp lệ; 0 file `.cs`/`.xaml`/`.csproj`/`handoff/*.md`/`nexus_config.json` có BOM | ⏳ |
| E2 | `--selftest retry-loop` (mock) | `/ask` → `/qa` `NEEDS_WORK: Thiếu xử lý đóng kết nối Socket` (r1) → `/retry --auto-qa Đã bổ sung khối using cho Socket` (QA mock `PASSED: Bản sửa lỗi đã đóng Socket an toàn`) → câu trả lời mới bắt đầu `[MOCK-RETRY-TURN-2]` và chứa nhận xét QA, gợi ý, câu trả lời trước; `turn_count` 2; `PASSED`; `retry_count` vẫn 1; lịch sử `1:user 1:model 1:qa 2:user 2:model 2:qa`; phần đồng bộ `/retry` < 10 ms (LiveCombat 25) | ⏳ |
| E3 | `--selftest retry-breaker` (mock) | `/qa` ×3 → `ESCALATED` r3; `/retry` → đúng thông báo `[3-STRIKE CIRCUIT BREAKER] …`, không tạo tác vụ nền, dòng SQLite không đổi một cột nào, 1 dòng audit `gemini_retry_blocked`; `/retry --force --auto-qa` → `PASSED`, `retry_count` 3, `turn_count` 2; vẫn đúng 1 dòng escalation | ⏳ |
| E4 | `--selftest followup-chain` + MCP `followup_gemini_dispatch` + pipe `SYNCSTATE` | Lượt 2 (`/f Thêm cơ chế chống trượt gói tin`) gửi 3 lượt; lượt 3 qua MCP gửi 5 lượt (`user=3, model=2`), `prev_model` là câu trả lời lượt 2; `turn_count` 3; lịch sử `1..3 user/model`; cửa sổ 6 cặp / cắt 12 mục / dựng lượt 1 cho dòng cũ đúng; Cockpit đang chạy nhận lượt 3 < 300 ms | ⏳ |
| E5 | `--selftest live-e2e` + RAM/CPU + hồi quy | Có khóa: `/ask` → `/f` → `/qa` thật tới `gemini-2.5-flash` đều có phản hồi, in độ trễ; không khóa: `SKIPPED (No live key configured)`; private WS < 60 MB (mục tiêu 44–52); CPU nền < 1 %; Verify-Task08/07/06/05/04/03 ALL PASS | ⏳ |

Kết quả gốc sẽ nằm ở `handoff/task09_verification.json`, `handoff/task09_selftest_*.json`.

**Kiểm tay nên làm (khi có khóa)**: trong game `Ctrl+Shift+Space` → `/ask --auto-qa Viết C# UDP client có seqId` → nếu QA chấm `NEEDS_WORK` thì `/retry --auto-qa <gợi ý>` → thẻ hiện `[RETRYING...]` rồi `[QA REVIEWING...]`; sau đó `/f Thêm cơ chế chống trượt gói tin` → `Turn: 3`.

---

## 3. Điểm mở cần Architect quyết định

1. **`/followup` không đổi `status`/`retry_count`** (đặc tả không nói). Hỏi tiếp trên dispatch `PASSED` thì thẻ vẫn `PASSED` dù câu trả lời đã khác. Có muốn đưa về `DISPATCHED`? (Lưu ý: nếu đưa về `DISPATCHED` thì `/f` trên dispatch `ESCALATED` sẽ thành đường vòng qua cầu chì, nên Claude chọn giữ nguyên.)
2. **`/retry` hoặc `/f` không có câu trả lời** (chưa có khóa, lỗi API) → không ghi gì vào SQLite, giữ câu trả lời và nhận xét QA cũ; `/ask` thì lưu chuỗi chẩn đoán. Đồng ý?
3. **Cầu chì chặn cả `retry_count ≥ 3` khi đã `PASSED`** (đúng chữ đặc tả) → muốn sửa tiếp phải `--force`. Giữ hay chỉ chặn khi `status = ESCALATED`?
4. **Giới hạn lưu `maxConversationTurns × 2` mục tính cả mục `qa`** (đúng chữ đặc tả) → mỗi vòng có QA tốn 3 mục, số cặp lưu được có thể dưới 6. Có muốn không tính mục `qa` hoặc dùng × 3?
5. **Cửa sổ gửi** = tối đa 6 cặp trước đó + câu hỏi mới; nhiệm vụ gốc luôn có trong SystemInstruction. Đồng ý?
6. **Đánh số và cấp tool**: thực tế là tool thứ 11, 12 (đặc tả ghi "thứ 7/8"); `retry_gemini_dispatch` Vàng, `followup_gemini_dispatch` Xanh (Vàng khi `autoQa`).
7. **`autoQaAfterAsk = true` cũng áp dụng cho `/retry` và `/f`.**
8. **E5 có khóa mà gọi thật lỗi** (vd. 429) → FAIL kèm chẩn đoán, không coi là SKIPPED. Đồng ý?
9. **Commit tài liệu Gate 8 lần chạy 3 (`f1feae5`) không có trong `main`**: lần fast-forward tới `c1d7e10` đã bỏ qua nó. Nhánh này mang theo (`bbd6b80`), nên sẽ vào `main` cùng TASK-09.

---

## 4. Đề xuất TASK-10

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Độ bền khi gọi Gemini thật** *(khuyến nghị)* | Xử lý 429/5xx có backoff (tôn trọng `Retry-After`); giới hạn số lần gọi/phút và ngân sách token/ngày trong `nexus_config.json`; đếm token (`usageMetadata`) theo dispatch và hiện trên HUD; `/retry` tự động tối đa N vòng khi `--auto-qa` trượt (dừng ở cầu chì). | E5 là lần đầu gọi API thật. `--auto-qa` gấp đôi số lần gọi, vòng `/retry` nhân thêm; quota miễn phí hết sẽ làm cả vòng Dev↔QA đứng. Cần số liệu từ E5 trước khi chốt ngưỡng. |
| B. Giọng nói (PCM 16 kHz + VAD) → `/ask`, `/f` | Mic → Gemini Live → lệnh Cockpit. | Giá trị lớn khi đang chơi, nhưng cần đo CPU/âm thanh và nên làm sau khi gọi thật đã ổn định. |
| C. Dọn giao diện & hiệu năng nền | Hiện lịch sử lượt trên HUD (thu gọn), RAM private trên thanh trạng thái, chữ bị cắt, MFD lỗi thời. | Nhỏ, có thể gộp vào A. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai TASK-09 trên nhánh `claude/task-09-retry-followup-e2e` (tách từ `main` @`ea62086`): `/retry [#id] [--auto-qa] [--force] [gợi ý]` và MCP `retry_gemini_dispatch` (Primary Agent nhận nhiệm vụ gốc + câu trả lời trước + `qa_response`/`evidence_notes` + gợi ý; câu trả lời mới → lượt mới, `status = DISPATCHED`, `retry_count` giữ nguyên; `--auto-qa` cho QA chấm lại); cầu chì chặn `/retry` khi `ESCALATED` hoặc `retry_count ≥ 3` trước khi gọi Gemini, trả đúng `[3-STRIKE CIRCUIT BREAKER] Dispatch #<id> is ESCALATED. Use /retry --force to override.`; `/followup` (`/f`) và MCP `followup_gemini_dispatch` gửi tối đa `maxConversationTurns` (6) cặp lượt gần nhất; cột `turn_count`, `turn_history_json` (ALTER idempotent), `turnCount` trong `latest_dispatch.json`; mock `[MOCK-RETRY-TURN-n]` / `[MOCK-FOLLOWUP-TURN-n]` lấy bằng chứng từ chính request; `Set-NexusGeminiKey.ps1` để nạp khóa ẩn bằng DPAPI; `Verify-Task09.ps1` E1–E5 (E5 tự gọi `gemini-2.5-flash` thật nếu có khóa, không thì `SKIPPED`). **Trạng thái: chưa build** (môi trường Claude không có .NET SDK), chưa có số đo; Gate 9 chờ Nhật Hòa chạy Verify-Task09. Chi tiết: `gate9_handoff.md`, `qa_report.md` §11. Có 9 điểm cần bạn quyết định ở §3 của handoff, quan trọng nhất: `/f` không đổi `status`; `/retry` không có câu trả lời thì không ghi gì; cầu chì chặn cả `retry_count ≥ 3` khi đã `PASSED`; giới hạn lưu × 2 mục tính cả mục `qa`. Commit tài liệu Gate 8 lần chạy 3 lại không có trong `main` (fast-forward dừng ở `c1d7e10`); nhánh này mang theo. Claude khuyến nghị TASK-10 Hướng A: độ bền khi gọi thật (429/backoff, giới hạn gọi/phút và token, đếm `usageMetadata` trên HUD), chốt ngưỡng sau khi có số liệu E5.
