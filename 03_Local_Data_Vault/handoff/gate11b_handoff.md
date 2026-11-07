# 📦 HỒ SƠ BÀN GIAO — Gate 11b (TASK-11b, v0.11.1)

- **Ca thực thi**: Claude, 2026-11-08
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: **`claude/task-11b-test-infra-hardening-r2`** (4 commit TASK-11b chép lại trên `main` @`603a974`, code giống hệt `1e650c7`). Nhánh gốc `claude/task-11b-test-infra-hardening` tách từ `6424d0f` giữ để đối chiếu
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (chỉ thị TASK-11b, chép nguyên văn từ chat)
- **Chi tiết kỹ thuật**: `qa_report.md` §14

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 0 | `main` | `bf018d8` (`v0.10.0`) | Fast-forward tới `6424d0f` theo lệnh Gemini (gồm code + tài liệu TASK-11). **Tag `v0.11.0` chưa đẩy được** (máy chủ chặn đẩy tag từ môi trường Claude, HTTP 403) |
| A | Token mock | Cộng vào hạn mức ngày thật; 256 912 / 250 000 sau vài lượt Verify | Chỉ vào `mock_tokens_used` (cột mới + bộ đếm riêng); hạn mức ngày chỉ đếm `live_*_tokens`; `/usage` hiện riêng dòng mock |
| B | Ghi `latest_dispatch.json` | Đường trúng cache ghi đè tại chỗ (`WriteFast`, không nguyên tử) | Một cách ghi duy nhất: file tạm `.<pid>.tmp` + `File.Move(overwrite)` dưới khóa; thêm `cache_lookup_ms`, `total_dispatch_ms` |
| C | Đo độ trễ trúng cache | 1 mẫu, ngưỡng 2 ms | 10 mẫu sau 1 lần warm-up: lookup P95 ≤ 2 ms; tổng P50 ≤ 2,5 ms, P95 ≤ 4 ms; in mảng mẫu, min/P50/P95/max; thêm kiểm đọc song song (0 lần đọc hỏng) |
| D | Model | Cấu hình `gemini-2.5-flash` → 404 → đổi ngầm sang `gemini-3.5-flash-lite` | Mặc định `gemini-3.5-flash-lite`; chuyển model thì ghi `[MODEL FALLBACK: a -> b]` + audit `gemini_model_fallback`; `model_used` trong `latest_dispatch.json` |
| D | Grounding | Ưu tiên riêng `current_task.md` khi hòa điểm | Thứ tự `(Score DESC, RelativePath ASC, SectionIndex ASC)`; loại trừ `*.db*`, `*.tmp`, `latest_dispatch.json*`, file kết quả kiểm thử |
| + | Script | `Stop-Process -Force` HostService | Chỉ tắt qua pipe; không thoát thì dừng và báo |

---

## 2. Trạng thái Gate 11b — ❌ CHƯA ĐẠT: E1–E4 PASS, E5 trượt một ngưỡng

Lần chạy 2 (04:20, commit `02dac22`, có build, cấu hình chuẩn `250000` / `gemini-3.5-flash-lite`; Claude Code chạy trên máy Nhật Hòa):
- **E1** PASS: 0/0, 0 BOM, 2 163 dòng.
- **E2** PASS:
  - `/ask`: tổng P50 2,19 / P95 2,88 ms;
  - `/qa`: tổng P50 1,82 / P95 2,26 ms;
  - **ADR-14: 17 738 lần đọc song song, 0 lần đọc hỏng**.
- **E3** PASS: `/g` tổng P50 1,65 / P95 2,02 ms.
- **E4** PASS.
- **E5**: gọi thật `gemini-3.5-flash-lite` trả lời ngay, không có fallback; `/qa` PASSED; WS 47,3 MB; CPU 0,03 %; **hồi quy Task10 → 03 ALL PASS ở `maxDailyTokens = 250000`**. Riêng `/ground` thật trúng cache: lookup P95 1,81 ✅, tổng P95 3,38 ✅, nhưng **tổng P50 = 2,754 ms > 2,5** ❌.

Bằng chứng ADR-50: token hôm nay **live 174 454 + mock 149 935**. Nếu chưa tách, tổng 324 389 đã vượt 250 000 và chặn hồi quy như Gate 11.

Lần 1 trượt vì hai lỗi trong chính bộ kiểm thử (Verify-Task03 đọc nhầm khối log; E3 chỉ nhận 3 tên file), Claude Code đã sửa ở `02dac22`. Chi tiết và mảng 10 mẫu: qa_report §14.3.

**Không gắn tag `v0.11.1`** khi chưa ALL PASS.

**Cập nhật 04:44 (qa_report §14.4)**:
- Đo từng bước không tìm thấy bước code nào chậm. `/ground` thật chạy riêng **đạt** (P50 1,754 ms, P95 2,641 ms).
- Lần chạy 3: E1–E4 PASS. E5 trượt vì (1) Gemini API timeout 25 s ở lần gọi đầu, nên không có mẫu trúng cache nào; (2) `main` tiến lên `603a974` làm Verify-Task06 báo "diverged". Lỗi (2) đã xử lý bằng nhánh `-r2`.
- Chạy lại khi Gemini API trả lời bình thường (thử một `/ask` trước, phản hồi 1–2 s mới chạy), tốt nhất sau 07:00 khi hạn mức ngày reset (hôm nay đã dùng khoảng 211k/250k token thật).

### Việc Nhật Hòa làm trước khi chạy
1. **Đẩy tag `v0.11.0`** (máy chủ chặn Claude đẩy tag):
   ```powershell
   cd C:\Agent\NEXUS_Home_Hub
   git fetch origin
   git checkout main
   git pull --ff-only
   git tag -a v0.11.0 -m "Release v0.11.0: GeminiResponseCache (ADR-44), VaultGroundingEngine (ADR-45) & 49.1MB Footprint"
   git push origin v0.11.0
   ```
2. **Sửa `03_Local_Data_Vault\config\nexus_config.json`** (Notepad):
   - xóa dòng `"maxDailyTokens": 1000000,`, hoặc đổi số thành `250000`;
   - đổi `"geminiModel": "gemini-2.5-flash"` thành `"geminiModel": "gemini-3.5-flash-lite"`. Nếu chưa có dòng này thì không cần thêm, vì mặc định mới đã là 3.5-flash-lite.

### Lệnh chạy
```powershell
git checkout claude/task-11b-test-infra-hardening
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task11.ps1
```
Không dùng `-SkipBuild`. Nếu HostService đang chạy, script tắt nó qua pipe; không tắt được thì script dừng và báo, không tự buộc dừng.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + `schema11` | 0/0; 0 file BOM; bảng cache + 4 cột `live_*`/`mock_tokens_used`; số dòng giữ nguyên (≥ 1 296); **cấu hình chuẩn**: `maxDailyTokens = 250000`, `geminiModel = gemini-3.5-flash-lite` | ✅ |
| E2 | `cache-hit-bypass` (mock) | `/ask` và `/qa`, mỗi loại 10 lần trúng cache sau warm-up: lookup P95 ≤ 2 ms, tổng P50 ≤ 2,5 ms, P95 ≤ 4 ms; 0 token, quota không đổi, cộng dồn đúng; bypass đúng; 40 lần ghi với luồng đọc song song: 0 lần đọc hỏng | ✅ (0 / 17 738) |
| E3 | `vault-grounding` (mock) | ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; `/g` 10 lần trúng cache sau warm-up, cùng ngưỡng | ✅ |
| E4 | `/cache` qua pipe + MCP | Như Gate 11 | ✅ |
| E5 | Gọi thật + RAM/CPU + hồi quy | `gemini-3.5-flash-lite` trả lời ngay lần đầu, 0 dòng audit `gemini_model_fallback`; 10 lần trúng cache sau warm-up, cùng ngưỡng; `/qa` PASSED (ghi `retriedToPass`); WS < 60 MB; CPU 0 %; **hồi quy Task10 → 03 ALL PASS với `maxDailyTokens = 250000`** | ❌ chỉ trượt tổng P50 của `/ground` thật (2,754 ms); mọi tiêu chí khác ✅ |

---

## 3. Điểm mở cần Architect quyết định

0. **Ngưỡng tổng P50 ≤ 2,5 ms cho lần trúng cache của lệnh gọi thật (E5).** Đường code giống hệt mock, nhưng dữ liệu thật lớn hơn: câu trả lời dài hơn nhiều lần và 3 nguồn grounding có tên dài. Lookup thật ~1,1 ms so với mock ~0,6 ms. Ba hướng:
   - **(a)** giữ ngưỡng, cho Claude Code đo từng bước bằng mock có độ dài câu trả lời bằng bản thật (không tốn token thật) để tìm phần chậm và tối ưu;
   - **(b)** nới riêng ngưỡng tổng P50 của E5 lên 3,0 ms (P95 4,0 ms giữ nguyên);
   - **(c)** chạy lại, nhưng lần 1 → lần 2 đã dao động mạnh (P50 4,83 → 2,75), nên chạy lại không đáng tin.

   Claude khuyến nghị **(a)** trước, rồi mới quyết (b) nếu không còn gì tối ưu được trong phạm vi Minimal Change.

1. **`total_dispatch_ms` trong file** không thể gồm thời gian ghi chính file đó. Trong file, đó là thời gian tới ngay trước lần ghi; con số chấm P50/P95 (gồm ghi nguyên tử) lấy từ `SyncTiming.TotalSyncMs` của lệnh. Chấp nhận?
2. **Tổng phiên (`SessionTotalTokens`) cũng chỉ đếm token thật.** RPM vẫn đếm mọi request, kể cả mock, vì Gate 10 E4 dùng RPM để kiểm chặn.
3. **Hai kiểm thử cũ được chỉnh theo quy tắc mới**: `token-accum` (Gate 10 E2) giờ kiểm token mock **không** vào hạn mức ngày; Verify-Task10 E4 chấp nhận `daily + mock > 0`.
4. **Điền ngược dữ liệu cũ dựa vào `ai_model`**: dòng trả lời thật nhưng QA mock (hiếm) bị tính cả vào live, và ngược lại.
5. **Bỏ `Stop-Process -Force`** khỏi Verify-Task11 (ngoài chỉ thị, theo luật Cấp Đỏ).
6. **Bỏ ưu tiên riêng cho `current_task.md`** khi hòa điểm, theo đúng thứ tự trong chỉ thị. Nguồn grounding có thể khác Gate 11 khi điểm bằng nhau.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 11b, gắn `v0.11.1`** *(khuyến nghị)* | Nhật Hòa đẩy tag `v0.11.0`, sửa cấu hình, chạy Verify-Task11. Claude Code trên máy sửa nếu build hoặc kiểm thử trượt. | Code chưa được build lần nào. |
| B. Sau Gate 11b: TASK-12 | Theo lộ trình của Gemini (ví dụ giọng nói → `/ask`, hoặc dọn HUD). | Hạ tầng kiểm thử đã ổn định thì mới nên thêm tính năng. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, kết quả Gate 11b (TASK-11b) trên máy Nhật Hòa, lần chạy 2 (04:20, nhánh `claude/task-11b-test-infra-hardening` @`02dac22`, có build, cấu hình chuẩn `maxDailyTokens = 250000` / `gemini-3.5-flash-lite`): **CHƯA ALL PASS**. Chi tiết ở `gate11b_handoff.md` và `qa_report.md` §14.3.
>
> Đã đạt:
> - E1 PASS: 0/0, 0 BOM, 2 163 dòng.
> - E2 PASS: `/ask` tổng P50 2,19 / P95 2,88 ms; `/qa` P50 1,82 / P95 2,26 ms; **ADR-14: 17 738 lần đọc song song, 0 lần đọc hỏng**.
> - E3 PASS: `/g` P50 1,65 / P95 2,02 ms.
> - E4 PASS.
> - E5: gọi thật `gemini-3.5-flash-lite` trả lời ngay, 0 fallback; `/qa` PASSED, không cần retry; WS 47,3 MB; CPU 0,03 %; **hồi quy Task10 → 03 ALL PASS ở 250 000** (token hôm nay live 174 454 + mock 149 935; nếu chưa tách đã vượt hạn mức).
>
> **Chỉ trượt một ngưỡng**: `/ground` thật trúng cache có tổng P50 = 2,754 ms (ngưỡng 2,5); lookup P95 1,81 và tổng P95 3,38 vẫn đạt. Đường code giống mock; khác biệt nằm ở dữ liệu thật lớn hơn (câu trả lời dài, tên nguồn dài).
>
> Lần 1 trượt vì hai lỗi của bộ kiểm thử, Claude Code đã sửa ở `02dac22`.
>
> Cần bạn quyết điểm §3.0:
> - (a) giữ ngưỡng, đo từng bước bằng mock có câu trả lời dài như thật để tối ưu (Claude khuyến nghị);
> - (b) nới riêng tổng P50 của E5 lên 3,0 ms.
>
> Đã fast-forward `main` tới `603a974` (`ROADMAP_FUTURE.md` + `docs/ADR_REGISTRY.md`). Nhánh TASK-11b sẽ lên `main` sau khi Gate 11b đạt. Chưa gắn `v0.11.1`.
