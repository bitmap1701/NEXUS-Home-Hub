# 📦 HỒ SƠ BÀN GIAO — Gate 11b (TASK-11b, v0.11.1)

- **Ca thực thi**: Claude, 2026-11-08 (ca chiều, lần chạy 4 lúc 15:49–15:52)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: **`claude/task-11b-test-infra-hardening-r2`** @`269e434` (4 commit TASK-11b chép lại trên `main` @`603a974`, code giống hệt `1e650c7`). Nhánh gốc `claude/task-11b-test-infra-hardening` giữ để đối chiếu
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (chỉ thị TASK-11b)
- **Chi tiết kỹ thuật**: `qa_report.md` §14 (lần chạy 4: §14.5)

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Điều kiện chạy | Lần chạy 3 (04:40): Gemini timeout 25 s; hạn mức ngày đã dùng khoảng 211k/250k | Sau 07:00 hạn mức reset (0 / 250 000). Thử một `/ask!` thật: **1 453 ms**, `gemini-3.5-flash-lite`, không fallback → đạt điều kiện 1–3 s |
| 2 | `main` | Lần 3 trượt dây chuyền vì `main` tiến lên `603a974` | `origin/main` vẫn `603a974`, là tổ tiên của `-r2`; Verify-Task06 báo "fast-forward ready" |
| 3 | Verify-Task11 đầy đủ (có build) | E1–E4 PASS, E5 FAIL (lần 2: P50 `/ground` thật 2,754 ms; lần 3: timeout) | **E1, E4, E5 PASS**; **E2, E3 FAIL** ngưỡng độ trễ mock khi máy tải 76 % CPU |
| 4 | Code | — | Không đổi code. Chỉ cập nhật `qa_report.md` §14.5 và file này |

---

## 2. Trạng thái Gate 11b — ❌ CHƯA ĐẠT (E2, E3 trượt độ trễ do máy bận)

Lần chạy 4: nhánh `-r2` @`269e434`, có build, cấu hình chuẩn `maxDailyTokens = 250000` / `gemini-3.5-flash-lite`. Claude Code chạy trên máy Nhật Hòa.

**Hoàn cảnh**: tải CPU toàn máy **76 %**. Roblox đang chạy (`RobloxPlayerBeta`, 1,4 GB), cùng Discord, Chrome, Overwolf, Steam, iCUE. Lần chạy 2–3 (khoảng 04:00) máy rảnh. Claude không tắt các chương trình này (Cấp Đỏ).

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout claude/task-11b-test-infra-hardening-r2
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task11.ps1
```
Không dùng `-SkipBuild`. Trước khi chạy: tắt game và ứng dụng nặng; thử một `/ask!` (trả lời 1–3 s).

| # | Bằng chứng | Tiêu chí PASS | Kết quả lần 4 |
|---|-----------|---------------|---------------|
| E1 | Build + BOM + `schema11` | 0/0; 0 file BOM; cột cache + `live_*`/`mock_tokens_used`; số dòng giữ nguyên; cấu hình chuẩn | ✅ 0/0, 0 BOM, 2 790 dòng; token hôm nay live 4 138, mock 0 |
| E2 | `cache-hit-bypass` (mock) | `/ask`, `/qa` 10 lần trúng cache sau warm-up: lookup P95 ≤ 2 ms, tổng P50 ≤ 2,5, P95 ≤ 4 ms; 0 token; bypass đúng; ADR-14 0 lần đọc hỏng | ❌ `/ask` tổng P50 2,923 / P95 8,922 (một mẫu 12,4 ms); `/qa` lookup P95 2,059, tổng P50 2,8 / P95 4,336. Phần chức năng ✅; ADR-14 ✅ **0 / 17 090** |
| E3 | `vault-grounding` (mock) | ≤ 3 đoạn, ≤ 1 500 ký tự, nguồn `[file#section]`; `/g` 10 lần trúng cache, cùng ngưỡng | ❌ chức năng ✅ (3 đoạn, 1 500 ký tự); `/g` lookup P95 3,411, tổng P50 3,392 / P95 6,791 (một mẫu 7,6 ms) |
| E4 | `/cache` qua pipe + MCP | Như Gate 11 | ✅ MCP `search_local_vault` 0,168 ms |
| E5 | Gọi thật + RAM/CPU + hồi quy | Model trả lời ngay, 0 fallback; 10 lần trúng cache, cùng ngưỡng; `/qa` PASSED; WS < 60 MB; CPU 0 %; hồi quy Task10 → 03 ALL PASS ở `250000` | ✅ **Lần đầu đạt**: `/ground` thật lookup P95 1,102, tổng P50 **2,363** / P95 2,932; 0 fallback; `/qa` PASSED, không retry; WS 49 MB; CPU 0 %; hồi quy **ALL PASS** |

**Nhận định của Claude (chưa kiểm chứng bằng lần chạy khi máy rảnh)**: E2/E3 trượt do máy bận, không do code.
- Code không đổi so với lần 2–3, khi E2/E3 đều đạt.
- Các mẫu chậm là đột biến đơn lẻ (7–12 ms) giữa các mẫu 2–3 ms. Kiểu này là luồng bị hệ điều hành tạm dừng, không phải chậm đều.
- Lookup (chỉ SQLite, không ghi file) cũng nhảy lên 2,4–4,6 ms.

Gộp các lần chạy: mỗi E đã PASS ít nhất một lần trên code này (E2, E3 ở lần 2–3; E5 ở lần 4), nhưng **chưa có lần nào ALL PASS trọn vẹn**. Chưa gắn `v0.11.1`.

---

## 3. Điểm mở cần Architect quyết định

0. **Cách xử lý ngưỡng ms khi máy đang bận.** Ngưỡng P50/P95 trên 10 mẫu chịu được một mẫu chậm, nhưng không chịu được máy tải 76 % CPU. Ba hướng:
   - **(a)** giữ ngưỡng, quy định chạy Gate khi máy rảnh (tắt game và ứng dụng nặng), rồi chạy lại một lần đầy đủ *(Claude khuyến nghị)*;
   - **(b)** Verify-Task11 đo tải CPU toàn máy trước E2; trên 30 % thì cảnh báo và dừng, không chấm;
   - **(c)** chấp nhận gộp bằng chứng từ nhiều lần chạy (mỗi E đã PASS ít nhất một lần). Claude không khuyến nghị: Gate nên có một lần ALL PASS trọn vẹn.
1. Các điểm §3.1–§3.6 của ca trước vẫn chờ xác nhận: `total_dispatch_ms` trong file không gồm lần ghi chính nó; `SessionTotalTokens` chỉ đếm token thật; `token-accum` và Verify-Task10 E4 chỉnh theo quy tắc mới; điền ngược dữ liệu cũ theo `ai_model`; bỏ `Stop-Process -Force`; bỏ ưu tiên riêng `current_task.md` khi hòa điểm.
2. **Ngưỡng tổng P50 của E5** (điểm §3.0 ca trước): lần này đạt 2,363 ms ở ngưỡng 2,5. Claude đề nghị **giữ ngưỡng 2,5**, không nới.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy lại Gate 11b khi máy rảnh, gắn `v0.11.1`** *(khuyến nghị)* | Nhật Hòa tắt Roblox và ứng dụng nặng, thử `/ask!`, chạy lệnh ở §2. ALL PASS thì merge `-r2` vào `main` (fast-forward) và gắn `v0.11.1` | Code không cần sửa. Mỗi E đã đạt ở một lần chạy; chỉ còn thiếu một lần trọn vẹn |
| B. Thêm chốt tải CPU vào Verify-Task11 (hướng 3.0b) | Đo tải toàn máy trước E2/E3/E5; quá ngưỡng thì dừng và báo | Tránh tốn một lượt chạy (và token thật) khi máy bận |
| C. Sau Gate 11b: TASK-12 | Theo lộ trình Gemini | Hạ tầng kiểm thử ổn định rồi mới thêm tính năng |

**Việc còn treo từ ca trước**: tag `v0.11.0` cần Nhật Hòa tự đẩy (máy chủ chặn Claude đẩy tag, HTTP 403). Lệnh ở `qa_report.md` §14 hoặc bản handoff cũ trong git log.

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, kết quả Gate 11b (TASK-11b) lần chạy 4 trên máy Nhật Hòa (15:49, nhánh `claude/task-11b-test-infra-hardening-r2` @`269e434`, có build, cấu hình chuẩn `250000` / `gemini-3.5-flash-lite`): **CHƯA ALL PASS**. Chi tiết ở `gate11b_handoff.md` và `qa_report.md` §14.5.
>
> Trước khi chạy, Claude thử một `/ask!` thật: 1 453 ms, không fallback; hạn mức ngày đã reset.
>
> - E1 PASS: 0/0, 0 BOM, 2 790 dòng.
> - E4 PASS.
> - **E5 PASS (lần đầu)**: `/ground` thật trúng cache lookup P95 1,102, tổng P50 **2,363** / P95 2,932 ms; 0 fallback; `/qa` PASSED; WS 49 MB; CPU 0 %; hồi quy Task10 → 03 ALL PASS ở 250 000.
> - **E2, E3 FAIL ngưỡng độ trễ mock**: `/ask` P50 2,923 / P95 8,922; `/qa` P50 2,8 / P95 4,336; `/g` P50 3,392 / P95 6,791 ms. Phần chức năng đều đúng; ADR-14 0 / 17 090 lần đọc hỏng.
>
> Claude cho rằng nguyên nhân là máy bận chứ không phải code: CPU toàn máy 76 % (Roblox đang chạy cùng Discord, Chrome, Overwolf); code không đổi so với lần 2–3, khi E2/E3 đạt; các mẫu chậm là đột biến đơn lẻ 7–12 ms.
>
> Cần bạn quyết §3.0:
> - (a) giữ ngưỡng, chạy lại khi máy rảnh (Claude khuyến nghị);
> - (b) thêm chốt tải CPU vào Verify-Task11;
> - (c) gộp bằng chứng nhiều lần chạy (Claude không khuyến nghị).
>
> Claude đề nghị giữ ngưỡng P50 2,5 ms của E5 vì lần này đã đạt. Chưa gắn `v0.11.1`; tag `v0.11.0` vẫn chờ Nhật Hòa đẩy.
