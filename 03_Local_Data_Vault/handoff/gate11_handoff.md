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

## 2. Trạng thái Gate 11 — ❌ CHƯA ĐẠT (chưa đủ bằng chứng)

| # | Bằng chứng | Tiêu chí PASS | Lần 1 (02:59, `-SkipBuild`) | Lần 2 (03:18, build lại) |
|---|-----------|---------------|---------------|---------------|
| E1 | Build + BOM + `schema11` | 0/0; 0 file BOM; bảng `gemini_response_cache`; ≥ 1 296 dòng giữ nguyên | ✅ 1 427 dòng | ✅ 1 664 dòng |
| E2 | `cache-hit-bypass` (mock) | Trúng cache < 2 ms, 0 token, quota không đổi, cộng dồn `saved_total_tokens`; `/qa` trúng cache < 2 ms; `/ask!` `--fresh` `/f` gọi API | ❌ `/ask` 1,26 / 0,66 ms ✅; **`/qa` 4,96 ms** | ❌ `/ask` 1,54 / 0,83 ms ✅; **`/qa` 2,46 ms** (`WriteFast` 1,23) |
| E3 | `vault-grounding` (mock) | ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; `/g` lặp lại < 2 ms | ✅ `/g` 1,63 ms | ❌ **`/g` 2,12 ms** |
| E4 | `/cache` qua pipe + MCP | Lệnh đúng; MCP tìm < 5 ms | ✅ 0,13 ms | ✅ 0,25 ms |
| E5 | Gọi thật + RAM/CPU + hồi quy 10 → 03 | Thật: lần 1 qua mạng → lần 2 trúng cache < 2 ms, 0 token → `/qa` PASSED; WS < 60 MB; CPU < 1 %; hồi quy ALL PASS | ✅ 3,36 s / 4 906 token → 1,68 ms, 0 token → PASSED; 48,7 MB; 0 %; hồi quy ALL PASS | ❌ thật PASS; 49,5 MB; 0 %; **hồi quy 10/09/08/07/06 FAIL** do hạn mức token ngày (~250k/250k) bị token mock ăn hết; Task06 thêm `git status` bẩn |

Selftest riêng sau khi sửa (Claude Code, bản sao vault, không có HostService): `/qa` trúng cache 3,12 / 1,29 / 1,73 / 1,75 / 1,13 ms (4/5 lần < 2 ms).

**Nhận định**: chức năng ADR-44/45 đúng đặc tả. Chưa đạt vì hai lý do không nằm ở chức năng:
1. Đường trúng cache đo được 0,7–2,5 ms, nên ngưỡng 2 ms chấm trên **một** mẫu lúc đạt lúc trượt.
2. Hạn mức token ngày bị chính bộ kiểm thử ăn hết. Hạn mức reset lúc 07:00 giờ Việt Nam.

### Lệnh chạy lại (máy Nhật Hòa, sau 07:00)
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
4. **Model `gemini-2.5-flash` trả 404** với khóa hiện tại; code TASK-10 đổi ngầm sang `gemini-3.5-flash-lite` (tên viết cứng). Đề nghị: sửa `geminiModel` trong `nexus_config.json`, đưa model dự phòng vào cấu hình và ghi log khi đổi.
5. **Warm-up qua `CockpitCommandProcessor`** để bỏ ~0,3–0,5 ms của lần trúng cache đầu tiên: thay đổi lớn hơn phạm vi Minimal Change. Có làm không?
6. **`Stop-Process -Force` trong `Verify-Task11.ps1`** (Cấp Đỏ): đổi thành cảnh báo rồi dừng script?
7. **E5 tự chạy `/retry` khi QA chấm `NEEDS_WORK`**: giữ, nhưng ghi `retriedToPass` vào báo cáo?
8. **Khóa cache của `/ask` chỉ gồm `model | prompt | grounding`**, không có `nexus_mode`. Cùng câu hỏi ở mode khác sẽ trúng cache cũ. Có cần thêm mode vào khóa?
9. **Đặc tả TASK-11 không có trên `main`**: chỉ nằm trong commit WIP của nhánh này.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng Gate 11 (TASK-11b, nhỏ)** *(khuyến nghị)* | Chốt §3.1–3.4. Không tính token mock vào hạn mức; chấm trúng cache theo P95 của 10 lần; thêm selftest cho dòng escalation khi `/qa` trúng cache; bỏ `Stop-Process -Force`; ghi `retriedToPass`; sửa model trong cấu hình. Chạy lại Verify-Task11. | Chức năng đã đúng; chỉ còn cách đo và hạ tầng kiểm thử. Nếu không sửa hạn mức token thì mọi Gate sau cũng sẽ trượt hồi quy sau vài lượt chạy. |
| B. Sang TASK-12 ngay | Chấp nhận Gate 11 với số liệu lần 1 (E5 ALL PASS) và lần 2 (E2 gần đạt). | Nhanh hơn, nhưng để lại lỗi đo đạc chập chờn trong chuỗi hồi quy. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, đây là nghiệm thu Gate 11 (TASK-11: `GeminiResponseCache` ADR-44 + `VaultGroundingEngine` ADR-45) trên nhánh `claude/task-11-vault-grounding-cache`. Code của Antigravity (`d0c24a1`), bản sửa của Claude Code trên máy Nhật Hòa (`7ed52b9`), nghiệm thu `gate11_handoff.md` và `qa_report.md` §13. **Gate 11 CHƯA ĐẠT** sau 2 lần chạy `Verify-Task11.ps1`.
>
> Chức năng đúng đặc tả: `/ask` trúng cache 0,66–1,54 ms, 0 token, quota không đổi, cộng dồn token tiết kiệm đúng; `/ask!`, `--fresh`, `/f` gọi API; `/ground` lấy ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; MCP tìm 0,13–0,25 ms; gọi thật PASS cả 2 lần (3,4 s / 4 906 token → lần 2 trúng cache 1,68 ms, 0 token → QA PASSED); WS 48,7–49,5 MB; CPU 0 %.
>
> Còn trượt:
> - (1) `/qa` trúng cache: 4,96 ms → 2,46 ms sau khi bỏ cắt ngắn `latest_dispatch.json` trong `WriteFast`. Selftest riêng: 4/5 lần dưới 2 ms.
> - (2) `/g` trúng cache 2,12 ms ở lần 2. Lần 1 là 1,63 ms.
> - (3) Hồi quy Task10 → 06 trượt ở lần 2 vì **token mock được tính vào hạn mức ngày thật** (~250k/250k).
>
> Đã sửa thêm một lỗi đúng sai: `/qa` trúng cache với `NEEDS_WORK` chạm strike 3 trước đây không ghi dòng `[3-STRIKE ESCALATION]` và không audit.
>
> Cần bạn quyết định 9 điểm ở §3, quan trọng nhất:
> - chấm `< 2 ms` theo P95 của 10 lần thay vì một mẫu;
> - không tính token mock vào hạn mức;
> - chấp nhận hay không việc `WriteFast` ghi đè không nguyên tử (lệch ADR-14);
> - model `gemini-2.5-flash` trả 404 và bị đổi ngầm sang `gemini-3.5-flash-lite`.
>
> Đặc tả TASK-11 chưa có trên `main`. Claude khuyến nghị Hướng A: TASK-11b nhỏ để đóng Gate 11 trước khi sang TASK-12. Chưa fast-forward `main`.
