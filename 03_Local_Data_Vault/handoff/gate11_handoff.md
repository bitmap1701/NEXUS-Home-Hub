# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 11 (TASK-11)

- **Ca thực thi**: Claude, 2026-11-08 (nhận ca từ Antigravity lúc 02:50). Code TASK-11 do Antigravity viết. Bản sửa `/qa` do Claude Code chạy trên máy Nhật Hòa. Claude (claude.ai) đối chiếu code, đọc kết quả từ máy và viết nghiệm thu.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-11-vault-grounding-cache`, tách từ `main` (`bf018d8`, sau tag `v0.10.0`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-11). **Chỉ có trên nhánh này** (nằm trong commit WIP `d0c24a1`), không có trên `main`.
- **Chi tiết kỹ thuật**: `qa_report.md` §13

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Kiểm chứng | Build 0/0 nhưng chưa ai chạy trọn `Verify-Task11.ps1` (`cache-hit-bypass` chưa chạy được thật; `/g` hit 2,15 ms) | 2 lần chạy trọn Verify trên máy Nhật Hòa, số liệu ở §2 |
| 2 | `/qa` trúng cache | 4,96 ms; không rõ chậm ở đâu | Đo từng bước (`SyncTiming.QaCache`): nguyên nhân là `WriteFast` cắt ngắn `latest_dispatch.json` (5–7 ms). Bỏ cắt ngắn, đệm khoảng trắng → 1,1–2,5 ms |
| 3 | Cầu chì 3 lần thử | `/qa` trúng cache với `NEEDS_WORK` chạm strike 3 **không** ghi dòng `[3-STRIKE ESCALATION]`, không audit (lỗi đúng sai) | Ghi đúng 1 dòng + 1 audit `dispatch_verdict`, chạy nền, không ghi trùng (thử trên bản sao vault) |
| 4 | Đối chiếu code | — | 7 phát hiện (qa_report §13.1): ghi file không nguyên tử, model 404 bị đổi ngầm, token mock ăn hạn mức thật, `Stop-Process -Force`, `/retry` ẩn trong E5, `Clear()` cache trên DB thật |
| 5 | Tài liệu | — | `qa_report.md` §13, file này |

---

## 2. Trạng thái Gate 11 — ✅ E1–E5 ALL PASS ở lần chạy 3 (có điều kiện, xem dưới)

**Lần 3** (03:46, `7ed52b9` + `59b49e2`, có build, `maxDailyTokens` nâng tạm lên 1 000 000 trong `nexus_config.json` cục bộ): **ALL CHECKS PASS (E1–E5)**.
- `/ask` trúng cache 1,81 / 1,11 ms; **`/qa` trúng cache 1,14 ms** (`WriteFast` 0,31); `/g` trúng cache 1,80 ms.
- Gọi thật: lần 1 qua mạng 2,68 s / 4 897 token → lần 2 trúng cache **1,92 ms**, 0 token → `/qa` PASSED.
- HostService 49,1 MB, CPU khi rảnh 0 %; hồi quy Task10 → 03 ALL PASS. Chi tiết: qa_report §13.3.

**Điều kiện**:
1. Biên chỉ còn 0,08–0,2 ms; lần 2 cùng các đường này ra 2,12–2,46 ms.
2. Hồi quy chỉ qua được vì đã nâng hạn mức token ngày: token mock vẫn bị tính vào hạn mức (256 912 > 250 000 mặc định).

Hai điểm này cần Architect quyết định ở §3 trước khi gắn tag.

Lần 1–2 (trượt) giữ lại để đối chiếu:

| # | Bằng chứng | Tiêu chí PASS | Lần 1 (02:59, `-SkipBuild`) | Lần 2 (03:18, build lại) |
|---|-----------|---------------|---------------|---------------|
| E1 | Build + BOM + `schema11` | 0/0; 0 file BOM; bảng `gemini_response_cache`; ≥ 1 296 dòng giữ nguyên | ✅ 1 427 dòng | ✅ 1 664 dòng |
| E2 | `cache-hit-bypass` (mock) | Trúng cache < 2 ms, 0 token, quota không đổi, cộng dồn `saved_total_tokens`; `/qa` trúng cache < 2 ms; `/ask!` `--fresh` `/f` gọi API | ❌ `/ask` 1,26 / 0,66 ms ✅; **`/qa` 4,96 ms** | ❌ `/ask` 1,54 / 0,83 ms ✅; **`/qa` 2,46 ms** (`WriteFast` 1,23) |
| E3 | `vault-grounding` (mock) | ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; `/g` lặp lại < 2 ms | ✅ `/g` 1,63 ms | ❌ **`/g` 2,12 ms** |
| E4 | `/cache` qua pipe + MCP | Lệnh đúng; MCP tìm < 5 ms | ✅ 0,13 ms | ✅ 0,25 ms |
| E5 | Gọi thật + RAM/CPU + hồi quy 10 → 03 | Thật: lần 1 qua mạng → lần 2 trúng cache < 2 ms, 0 token → `/qa` PASSED; WS < 60 MB; CPU < 1 %; hồi quy ALL PASS | ✅ 3,36 s / 4 906 token → 1,68 ms, 0 token → PASSED; 48,7 MB; 0 %; hồi quy ALL PASS | ❌ thật PASS; 49,5 MB; 0 %; **hồi quy 10/09/08/07/06 FAIL** do hạn mức token ngày (~250k/250k) bị token mock ăn hết; Task06 thêm `git status` bẩn |

Lần 3: E1 ✅ 1 770 dòng · E2 ✅ `/ask` 1,81 / 1,11 ms, `/qa` 1,14 ms · E3 ✅ `/g` 1,80 ms · E4 ✅ 0,14 ms · E5 ✅ thật 2,68 s → 1,92 ms → PASSED, 49,1 MB, 0 %, hồi quy ALL PASS.

Selftest riêng sau khi sửa (Claude Code, bản sao vault, không có HostService): `/qa` trúng cache 3,12 / 1,29 / 1,73 / 1,75 / 1,13 ms (4/5 lần < 2 ms).

**Nhận định (sau lần 2, vẫn đúng sau lần 3)**: chức năng ADR-44/45 đúng đặc tả. Hai rủi ro còn lại không nằm ở chức năng:
1. Đường trúng cache đo được 0,7–2,5 ms, nên ngưỡng 2 ms chấm trên **một** mẫu lúc đạt lúc trượt.
2. Hạn mức token ngày bị chính bộ kiểm thử ăn hết. Hạn mức reset lúc 07:00 giờ Việt Nam.

### Lệnh chạy lại (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-11-vault-grounding-cache
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task11.ps1
```
Không dùng `-SkipBuild` (bản sửa `7ed52b9` cần build). Nếu HostService đang chạy, nên tắt qua pipe trước để script không phải dùng `Stop-Process -Force`.

---

## 3. Điểm mở cần Architect quyết định

1. **Cách chấm `< 2 ms`**: giữ một mẫu duy nhất, hay chấm **P95 của 10 lần trúng cache** (như Gate 8 E2 chấm `/ask`)? Với nhiễu khoảng 1 ms trên máy, một mẫu sẽ chập chờn. Claude khuyến nghị P95 của 10 lần, ngưỡng 2 ms.
2. **Token mock tính vào hạn mức ngày thật** (`RecordTokens` + `SeedFromStore` cộng cả dòng mock). Claude khuyến nghị: không tính khi `MockEnabled`, và `SeedFromStore` bỏ qua các dòng `ai_model` là model mock. Nếu không, mỗi ngày chỉ chạy được vài lượt Verify.
3. **`WriteFast` ghi đè tại chỗ, không nguyên tử** (lệch ADR-14), để kịp ngân sách 2 ms. Đọc trúng lúc đang ghi thì `TryRead` trả `null`, sự kiện bị bỏ qua, không sập. Chấp nhận, hay giữ ghi nguyên tử và nới ngân sách cho đường trúng cache?
4. **Model `gemini-2.5-flash` trả 404** với khóa hiện tại (khóa ở **gói Free** của Google AI Studio, chưa gắn thanh toán; gọi thật không tốn tiền); code TASK-10 đổi ngầm sang `gemini-3.5-flash-lite` (tên viết cứng). Đề nghị: sửa `geminiModel` trong `nexus_config.json`, đưa model dự phòng vào cấu hình và ghi log khi đổi.
5. **Warm-up qua `CockpitCommandProcessor`** để bỏ ~0,3–0,5 ms của lần trúng cache đầu tiên: thay đổi lớn hơn phạm vi Minimal Change. Có làm không?
6. **`Stop-Process -Force` trong `Verify-Task11.ps1`** (Cấp Đỏ): đổi thành cảnh báo rồi dừng script?
7. **E5 tự chạy `/retry` khi QA chấm `NEEDS_WORK`**: giữ, nhưng ghi `retriedToPass` vào báo cáo?
8. **Khóa cache của `/ask` chỉ gồm `model | prompt | grounding`**, không có `nexus_mode`. Cùng câu hỏi ở mode khác sẽ trúng cache cũ. Có cần thêm mode vào khóa?
9. **Đặc tả TASK-11 không có trên `main`**: chỉ nằm trong commit WIP của nhánh này.
10. **`maxDailyTokens`**: trên máy Nhật Hòa đang là 1 000 000 (nâng tạm để chạy Verify). Giữ, hay về 250 000 sau khi sửa lỗi token mock?
11. **`Verify-Task03.ps1:200`** báo `Cannot index into a null array` khi đọc dòng `Target monitor` (không ảnh hưởng kết quả): sửa trong TASK-11b?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Nghiệm thu Gate 11 + TASK-11b nhỏ** *(khuyến nghị)* | Gate 11 đã có lần ALL PASS: nghiệm thu, gắn `v0.11.0`. Sau đó làm TASK-11b: không tính token mock vào hạn mức (rồi trả `maxDailyTokens` về 250 000), chấm trúng cache theo P95 của 10 lần, selftest dòng escalation khi `/qa` trúng cache, bỏ `Stop-Process -Force`, ghi `retriedToPass`, sửa model trong cấu hình, sửa `Verify-Task03.ps1:200`. | Chức năng đã đúng và đã có bằng chứng PASS. Phần còn lại là hạ tầng kiểm thử; nếu để nguyên thì mọi Gate sau sẽ trượt hồi quy chập chờn. |
| B. Sang TASK-12 ngay, bỏ TASK-11b | Nghiệm thu Gate 11 với lần 3. | Nhanh hơn, nhưng mỗi ngày chỉ chạy được vài lượt Verify trước khi chạm hạn mức token, và các ngưỡng 2 ms sẽ trượt ngẫu nhiên. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Gate 11 (TASK-11: `GeminiResponseCache` ADR-44 + `VaultGroundingEngine` ADR-45) đã có lần chạy **Verify-Task11 ALL CHECKS PASS (E1–E5)** trên máy Nhật Hòa. Đó là lần 3, lúc 03:46, nhánh `claude/task-11-vault-grounding-cache`, code `7ed52b9`, có build 0/0. Chi tiết ở `gate11_handoff.md` và `qa_report.md` §13.
>
> Số liệu lần 3:
> - `/ask` trúng cache 1,81 / 1,11 ms, 0 token, quota không đổi.
> - `/qa` trúng cache 1,14 ms (trước sửa 4,96 ms; nguyên nhân là `WriteFast` cắt ngắn `latest_dispatch.json`).
> - `/g` trúng cache 1,80 ms; tối đa 3 đoạn, 1 500 ký tự, nguồn `[file#section]`; MCP tìm 0,14 ms.
> - Gọi thật: lần 1 qua mạng 2,68 s / 4 897 token → lần 2 trúng cache 1,92 ms, token ngày đứng yên → `/qa` PASSED.
> - WS 49,1 MB, CPU 0 %, hồi quy Task10 → 03 ALL PASS.
> - Đã sửa thêm lỗi `/qa` trúng cache với `NEEDS_WORK` chạm strike 3 không ghi dòng `[3-STRIKE ESCALATION]`.
>
> **Hai điều kiện cần bạn biết trước khi gắn tag**:
> - (1) Biên rất mỏng: các lần trúng cache ra 1,80–1,92 ms, trong khi lần 2 cùng các đường này ra 2,12–2,46 ms. Ngưỡng 2 ms chấm trên một mẫu sẽ chập chờn.
> - (2) Hồi quy chỉ qua được vì Nhật Hòa nâng tạm `maxDailyTokens` lên 1 000 000. Token mock của bộ kiểm thử vẫn bị cộng vào hạn mức ngày (256 912 > 250 000). Khóa API ở gói Free, nên gọi thật không tốn tiền.
>
> Cần bạn quyết định 11 điểm ở §3, quan trọng nhất:
> - chấm theo P95 của 10 lần;
> - không tính token mock vào hạn mức;
> - `WriteFast` ghi đè không nguyên tử (lệch ADR-14);
> - model `gemini-2.5-flash` trả 404 và bị đổi ngầm sang `gemini-3.5-flash-lite`.
>
> Đặc tả TASK-11 chưa có trên `main`. Claude khuyến nghị Hướng A: nghiệm thu Gate 11 (`v0.11.0`, fast-forward `main` tới đầu nhánh, gồm cả tài liệu), rồi làm TASK-11b nhỏ để sửa hạ tầng kiểm thử trước TASK-12.
