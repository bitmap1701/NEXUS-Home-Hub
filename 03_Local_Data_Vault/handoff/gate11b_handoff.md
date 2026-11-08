# 📦 HỒ SƠ BÀN GIAO — Gate 11b (TASK-11b, v0.11.1)

- **Ca thực thi**: Claude, 2026-11-08 (ca chiều; lần chạy 4 lúc 15:49, lần chạy 5 lúc 16:00–16:03)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: **`claude/task-11b-test-infra-hardening-r2`** (4 commit TASK-11b chép lại trên `main` @`603a974`; code giống hệt `1e650c7`). Lần chạy 5 trên @`690961a`; sau `269e434` chỉ có thay đổi tài liệu
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (chỉ thị TASK-11b)
- **Chi tiết kỹ thuật**: `qa_report.md` §14 (lần 4: §14.5, lần 5: §14.6)

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Điều kiện chạy | Lần 3 (04:40): Gemini timeout 25 s; hạn mức ngày đã dùng khoảng 211k/250k | Hạn mức đã reset sau 07:00. Thử một `/ask!` thật: **1 453 ms**, `gemini-3.5-flash-lite`, không fallback |
| 2 | Lần chạy 4 (15:49) | — | E1, E4, E5 PASS (lần đầu `/ground` thật đạt P50 2,363 ms). E2, E3 trượt độ trễ mock vì máy tải **76 % CPU** (Roblox, Overwolf…) |
| 3 | Lần chạy 5 (16:00) | — | Nhật Hòa tắt ứng dụng nặng; CPU toàn máy trung bình **8,2 %**. **Verify-Task11 ALL CHECKS PASS (E1–E5)**, `allPassed = true` |
| 4 | Code | — | **Không đổi code.** Chỉ cập nhật `qa_report.md` §14.5–14.6 và file này |
| 5 | Tag / `main` | `v0.11.0` chờ Nhật Hòa đẩy | `v0.11.0` đã có trên GitHub (`ece865f`). **Không gắn `v0.11.1`, không đụng `main`** (theo lệnh Nhật Hòa) |

---

## 2. Trạng thái Gate 11b — ✅ ALL CHECKS PASS (E1–E5)

Lần chạy 5: 16:00–16:03, nhánh `-r2` @`690961a`, có build. Cấu hình chuẩn `maxDailyTokens = 250000`, `geminiModel = gemini-3.5-flash-lite`. CPU toàn máy trung bình 8,2 % (10 × 1 s). Claude Code chạy trên máy Nhật Hòa. Report: `task11_verification.json` (`allPassed = true`, `2026-11-08T09:03:45Z`).

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout claude/task-11b-test-infra-hardening-r2
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task11.ps1
```
Không dùng `-SkipBuild`. Trước khi chạy: tắt game và ứng dụng nặng; thử một `/ask!` (trả lời 1–3 s).

| # | Bằng chứng | Tiêu chí PASS | Kết quả lần 5 |
|---|-----------|---------------|---------------|
| E1 | Build + BOM + `schema11` | 0/0; 0 file BOM; cột cache + `live_*`/`mock_tokens_used`; số dòng giữ nguyên; cấu hình chuẩn | ✅ 0/0, 0 BOM, 3 001 dòng; token hôm nay live 32 659, mock 7 788 |
| E2 | `cache-hit-bypass` (mock) | `/ask`, `/qa` 10 lần trúng cache sau warm-up: lookup P95 ≤ 2 ms, tổng P50 ≤ 2,5, P95 ≤ 4 ms; 0 token; bypass đúng; ADR-14 0 lần đọc hỏng | ✅ `/ask` lookup P95 0,553, tổng P50 **1,283** / P95 **1,692**; `/qa` lookup P95 0,531, tổng P50 **1,468** / P95 **1,829**; ADR-14 **0 / 24 216** |
| E3 | `vault-grounding` (mock) | ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; `/g` 10 lần trúng cache, cùng ngưỡng | ✅ 3 đoạn, 1 500 ký tự; `/g` lookup P95 0,714, tổng P50 **1,826** / P95 **2,334** |
| E4 | `/cache` qua pipe + MCP | Như Gate 11 | ✅ MCP `search_local_vault` 0,099 ms |
| E5 | Gọi thật + RAM/CPU + hồi quy | Model trả lời ngay, 0 fallback; 10 lần trúng cache, cùng ngưỡng; `/qa` PASSED; WS < 60 MB; CPU 0 %; hồi quy Task10 → 03 ALL PASS ở `250000` | ✅ 0 fallback, `retriedToPass = false`; `/ground` thật lookup P95 1,01, tổng P50 **1,715** / P95 **2,294**; `/qa` PASSED; WS 49 MB; CPU 0 %; hồi quy **ALL PASS** |

### Mảng 10 mẫu tổng (ms, sau warm-up)
- `/ask` (mock): 1,289 1,171 1,15 1,109 1,201 1,277 1,757 1,389 1,469 1,612
- `/qa` (mock): 1,47 1,485 1,14 1,152 1,271 1,465 1,843 1,811 1,561 1,351
- `/g` (mock): 1,578 1,499 1,262 1,366 1,832 1,868 2,514 1,838 2,113 1,819
- `/ground` (thật): 1,755 1,489 1,645 1,866 1,783 1,628 1,835 2,645 1,675 1,481

Mảng lookup ở `qa_report.md` §14.6.

**So với lần 4 (cùng code)**: lần 4 máy tải 76 %, E2/E3 có các mẫu đột biến 7–12 ms. Lần 5 máy rảnh, mẫu lớn nhất chỉ 2,645 ms. Kết quả này xác nhận lần 4 trượt do máy bận, không do code.

---

## 3. Điểm mở cần Architect quyết định

0. **Merge và gắn `v0.11.1`.** Gate đã ALL PASS. Theo ADR-39 (`main` thẳng hàng), đề xuất fast-forward `main` từ `603a974` lên đầu nhánh `-r2`, rồi gắn `v0.11.1`. Claude chưa làm theo lệnh Nhật Hòa; tag phải do Nhật Hòa đẩy.
1. **Quy định chạy Gate có ngưỡng ms**: máy phải rảnh (tắt game và ứng dụng nặng). Lần 4 cho thấy máy tải 76 % là đủ làm trượt. Có nên thêm chốt tải CPU (ví dụ > 30 % thì cảnh báo và dừng) vào Verify-Task11 không?
2. **Giữ ngưỡng tổng P50 2,5 ms của E5** (điểm §3.0 của ca trước): lần 5 đạt 1,715 ms. Claude đề nghị giữ, không nới.
3. Các điểm của ca trước vẫn chờ xác nhận:
   - `total_dispatch_ms` trong file không gồm lần ghi chính nó;
   - `SessionTotalTokens` chỉ đếm token thật;
   - `token-accum` và Verify-Task10 E4 chỉnh theo quy tắc mới;
   - điền ngược dữ liệu cũ theo `ai_model`;
   - bỏ `Stop-Process -Force`;
   - bỏ ưu tiên riêng `current_task.md` khi hòa điểm.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng Gate 11b** *(khuyến nghị)* | Gemini duyệt; fast-forward `main` lên `-r2`; Nhật Hòa gắn và đẩy `v0.11.1` | Đã ALL PASS ở cấu hình chuẩn; code không cần sửa |
| B. Chốt tải CPU trong Verify-Task11 (điểm 3.1) | Đo tải toàn máy trước E2/E3/E5; quá ngưỡng thì dừng và báo | Tránh tốn một lượt chạy (và token thật) khi máy bận. Thay đổi nhỏ, chỉ trong script |
| C. TASK-12 | Theo lộ trình Gemini (`ROADMAP_FUTURE.md`) | Hạ tầng kiểm thử đã ổn định |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 11b (TASK-11b) đã ALL CHECKS PASS (E1–E5)** trên máy Nhật Hòa: lần chạy 5, 16:00–16:03, nhánh `claude/task-11b-test-infra-hardening-r2` @`690961a`, có build, cấu hình chuẩn `maxDailyTokens = 250000` / `gemini-3.5-flash-lite`, máy rảnh (CPU trung bình 8,2 %). Chi tiết ở `gate11b_handoff.md` và `qa_report.md` §14.6.
>
> - E1 PASS: build 0/0, 0 BOM, 3 001 dòng; token hôm nay live 32 659, mock 7 788 (tách riêng).
> - E2 PASS: `/ask` tổng P50 1,283 / P95 1,692 ms; `/qa` P50 1,468 / P95 1,829 ms; lookup P95 ≤ 0,553; ADR-14 0 / 24 216 lần đọc hỏng.
> - E3 PASS: `/g` P50 1,826 / P95 2,334 ms; 3 đoạn, 1 500 ký tự.
> - E4 PASS: MCP `search_local_vault` 0,099 ms.
> - E5 PASS: `gemini-3.5-flash-lite` trả lời ngay, 0 fallback; `/ground` thật trúng cache P50 1,715 / P95 2,294 ms; `/qa` PASSED không retry; WS 49 MB; CPU 0 %; hồi quy Task10 → 03 ALL PASS ở 250 000.
>
> Lần chạy 4 trước đó (cùng code) trượt E2/E3 vì máy tải 76 % CPU (Roblox, Overwolf); lần 5 xác nhận nguyên nhân là máy bận, không phải code.
>
> Cần bạn quyết:
> - (1) duyệt fast-forward `main` lên `-r2` và gắn `v0.11.1` (Claude chưa đụng `main`/tag);
> - (2) có thêm chốt tải CPU vào Verify-Task11 không;
> - (3) giữ ngưỡng P50 2,5 ms của E5 (Claude đề nghị giữ).
>
> Tag `v0.11.0` đã có trên GitHub.
