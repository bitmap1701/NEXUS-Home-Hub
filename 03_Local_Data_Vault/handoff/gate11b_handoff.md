# 📦 HỒ SƠ BÀN GIAO — Gate 11b (TASK-11b, v0.11.1)

- **Ca thực thi**: Claude, 2026-11-08
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-11b-test-infra-hardening`, tách từ `main` (`6424d0f`)
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

## 2. Trạng thái Gate 11b — ⏳ CHƯA ĐỦ BẰNG CHỨNG (chưa build)

Môi trường Claude không có .NET SDK nên **chưa build, chưa chạy**. Chưa có số đo.

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
| E1 | Build + BOM + `schema11` | 0/0; 0 file BOM; bảng cache + 4 cột `live_*`/`mock_tokens_used`; số dòng giữ nguyên (≥ 1 296); **cấu hình chuẩn**: `maxDailyTokens = 250000`, `geminiModel = gemini-3.5-flash-lite` | ⏳ |
| E2 | `cache-hit-bypass` (mock) | `/ask` và `/qa`, mỗi loại 10 lần trúng cache sau warm-up: lookup P95 ≤ 2 ms, tổng P50 ≤ 2,5 ms, P95 ≤ 4 ms; 0 token, quota không đổi, cộng dồn đúng; bypass đúng; 40 lần ghi với luồng đọc song song: 0 lần đọc hỏng | ⏳ |
| E3 | `vault-grounding` (mock) | ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; `/g` 10 lần trúng cache sau warm-up, cùng ngưỡng | ⏳ |
| E4 | `/cache` qua pipe + MCP | Như Gate 11 | ⏳ |
| E5 | Gọi thật + RAM/CPU + hồi quy | `gemini-3.5-flash-lite` trả lời ngay lần đầu, 0 dòng audit `gemini_model_fallback`; 10 lần trúng cache sau warm-up, cùng ngưỡng; `/qa` PASSED (ghi `retriedToPass`); WS < 60 MB; CPU 0 %; **hồi quy Task10 → 03 ALL PASS với `maxDailyTokens = 250000`** | ⏳ |

---

## 3. Điểm mở cần Architect quyết định

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

> Chào Gemini, Claude đã triển khai TASK-11b trên nhánh `claude/task-11b-test-infra-hardening`. Chi tiết ở `gate11b_handoff.md` và `qa_report.md` §14.
>
> Đã fast-forward `main` tới `6424d0f` theo lệnh của bạn. **Tag `v0.11.0` chưa lên GitHub**: máy chủ chặn đẩy tag từ môi trường Claude, Nhật Hòa sẽ đẩy từ máy.
>
> - **(A)** `GeminiResult.IsLiveApi`; 4 cột `live_prompt_tokens`, `live_completion_tokens`, `live_total_tokens`, `mock_tokens_used` (ALTER idempotent, điền ngược theo `ai_model`). Hạn mức ngày chỉ đếm `live_*`; token mock chỉ vào `mock_tokens_used` (không vào TPM, tổng phiên, hạn mức).
> - **(B)** Bỏ `WriteFast`; mọi lần ghi `latest_dispatch.json` dùng file tạm `.<pid>.tmp` + `File.Move(overwrite)` dưới khóa; thêm `cache_lookup_ms`, `total_dispatch_ms`.
> - **(C)** E2/E3/E5 đo 10 lần trúng cache sau 1 warm-up: lookup P95 ≤ 2 ms, tổng P50 ≤ 2,5 / P95 ≤ 4 ms, in mảng mẫu, min/P50/P95/max; thêm kiểm 40 lần ghi với luồng đọc song song (0 lần đọc hỏng).
> - **(D)** Mặc định `gemini-3.5-flash-lite`; chuyển model ghi `[MODEL FALLBACK: a -> b]` + audit `gemini_model_fallback`; `model_used` trong `latest_dispatch.json`; grounding loại trừ `*.db*`, `*.tmp`, sắp xếp `(Score DESC, RelativePath ASC, SectionIndex ASC)`.
> - Ngoài chỉ thị: bỏ `Stop-Process -Force` khỏi Verify-Task11.
>
> Hai kiểm thử cũ được chỉnh theo quy tắc mới: `token-accum` (Gate 10 E2) và Verify-Task10 E4.
>
> **Trạng thái: chưa build** (môi trường Claude không có .NET SDK); Gate 11b chờ Nhật Hòa chạy Verify-Task11 sau khi sửa `nexus_config.json` về `maxDailyTokens = 250000` và `geminiModel = gemini-3.5-flash-lite`.
>
> Cần bạn xác nhận 6 điểm ở §3, quan trọng nhất: `total_dispatch_ms` trong file không thể gồm thời gian ghi chính nó (P50/P95 chấm trên `SyncTiming.TotalSyncMs`), và tổng phiên cũng chỉ đếm token thật.
