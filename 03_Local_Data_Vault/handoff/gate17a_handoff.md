# 📦 HỒ SƠ BÀN GIAO — Gate 17a (TASK-17a, ADR-69 → `v0.17.1`): `networkTargets` tùy chọn + dọn giao diện lúc rảnh (RAM ≤ 45,0 MB)

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa / Claude Code (đo và Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-17a-net-targets-idle-trim` (đặc tả `8f62675`, nền `v0.17.0` + hồ sơ đóng Gate 17 `aaa5ec7`)
- **Chi tiết kỹ thuật**: `qa_report.md` §26
- **Trạng thái build**: Claude **chưa build** trên máy mình; CI Windows runner build `-warnaserror` + selftest. Số đo dưới đây là của PC Nhật Hòa.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Đích LAN của `/net` | Hằng số trong code | `networkTargets` tùy chọn trong `nexus_config.json` (tối đa 8), áp dụng ngay khi `/reload`; thiếu / rỗng / hỏng → 4 đích mặc định |
| 2 | Tên đích mặc định | `GW`, `NITRO5`, `NITRO5-WIFI`, `TV` | `Gateway`, `NITRO5-LAN`, `NITRO5-WIFI`, `LAN-TV` (theo đặc tả) |
| 3 | Dòng `⚡ NITRO5` | Theo nhãn cố định | Mục đầu tiên nhãn `NITRO5*` hoặc `.22` / `.192`; không có → "không có trong networkTargets" |
| 4 | Dọn `[MEM]` lúc rảnh | Chỉ GC + bỏ câu trả lời lịch sử vẽ sẵn | Thêm: chữ trang `MẠNG // /NET`, chuỗi thẻ 8 → 4 khi PASSIVE, khung trả lời khi ẩn; trả lại sau đánh thức (Background, ngoài WAKE). Giữ theo 17a.2 |
| 5 | Đo trước | — | `ui-footprint` thêm pha `net`, `hiddenTrimmed`; `Measure-CockpitFootprint.ps1 -Trim` (trimoff / trimall / trimhide) |
| 6 | Kiểm thử | — | Selftest `net-targets` (CI), `Verify-Task17a.ps1`; Verify-Task16b giữ ngân sách 45,0 MB (17a.2) |
| 7 | Sổ cái | ADR-69 trống | ADR-69 đang triển khai (`v0.17.1`), số trống kế tiếp ADR-70; ROADMAP thêm TASK-17a |

---

## 2. Trạng thái Gate 17a — ⏳ lần 2/3 FAIL (một selftest chập chờn trong chuỗi); chờ Architect: đóng dấu theo bằng chứng hay chạy lần 3/3

```powershell
cd C:\Agent\NEXUS_Home_Hub
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task17a.ps1
```
`git status` phải là `working tree clean` trước khi chạy. Kết quả: `03_Local_Data_Vault/handoff/task17a_verification.json`. Architect đã đồng ý dải `[SLA-WARN]` (`qa_report.md` §26.7). Chạy sau `Measure-HostFootprint.ps1` của TASK-17b.

**Lần 2/3** (`0e18679`, 09:2x–09:43): E1–E4 ✅, RAM 17a 38,4 MB; chuỗi chỉ trượt vì `cache-hit-bypass` (Task11) FAIL một lần trong fastRegression của Verify-Task13 (PASS ở mọi lượt khác cùng lần chạy, `-FullRegression` PASS); Task16b 45,4 MB GC 9,53 → `[SLA-WARN]` PASS. Chi tiết §26.9.

**Lần 1/3** (`0f23975`, 08:5x–09:13): E1–E4 ✅. E5 ❌ chỉ vì chuỗi: Verify-Task16b RAM 46 MB với GC heap **10,4 MB** (> 8,0) và Task04 E6 CPU rảnh 1,21 % (mẫu 1–2,5 % ở giây 7–11; cùng mã đã PASS lúc 08:04) kéo theo Task05…14. RAM Task17a 40,3 MB. Chi tiết `qa_report.md` §26.8.

**Lượt không tính** (`27b7ad2`, 08:04–08:20): E1–E4 ✅ (WAKE P50 8,27 / P95 11,71 ms). E5 ❌: `current_task.md` trên PC sửa tay chưa commit (đã commit `b9d50c5`); RAM Task16b 49,7 / Task15 45,2 MB. Chi tiết `qa_report.md` §26.5.

**Đo `-Trim` lần 2** (cây sạch `b9d50c5`, không phải lượt Verify): `hiddenTrimmed` trimoff 37,4 / 38,0 · trimall 36,8 / 37,7 · trimhide 37,0 / 37,2 MB → không cách dọn nào giảm được RAM đo được. Selftest (chỉ cửa sổ Cockpit) ổn định 37–38 MB; Verify-Task16b (HostService đầy đủ) các lần trước 44,9 / 48,7 / 49,7 MB.

| # | Bằng chứng | Lần 1/3 |
|---|------------|-----|
| E1 | build `-warnaserror` 0/0, Zero-BOM, ADR-67 chốt `v0.17.0`, ADR-69 có, selftest `net-targets` | ✅ |
| E2 | `LOOPBACK-TEST` + `NITRO5-LAN` → `/reload` → `/net` và MCP dùng ngay, cùng HostService | ✅ |
| E3 | Thiếu / `[]` / `{"label":"","host":null}` → 4 đích mặc định | ✅ |
| E4 | `ui-footprint` pha `net` + `hiddenTrimmed`; dọn khi hiện / ẩn, trả lại khi đánh thức; WAKE P50 ≤ 10 / P95 ≤ 16 ms | ✅ |
| E5 | RAM rảnh ≤ 45,0 MB mọi Gate (17a.2), CPU 0 %, chuỗi Verify-Task17 → … → 03 PASS, 0 lượt gọi Gemini thật, git sạch | ❌ |

---

## 3. Điểm mở cần Architect quyết định

**Đã quyết (17a.2):** giữ ngưỡng 45,0 MB, bỏ mục tiêu 43,5 MB, giữ các cách dọn vô hại.

- **(17a.4) RAM Task16b dao động 44,9–49,7 MB quanh ngưỡng 45,0**: phần chênh 7–12 MB so với selftest nằm ngoài cửa sổ Cockpit (HUD, watcher, luồng nền của HostService), không thuộc phạm vi 17a. Chạy Verify lần 2/3 lúc này gần như tung đồng xu. Chọn:
  - (a) dải [SLA-WARN] cho RAM rảnh Task16b giống WAKE (ví dụ PASS ≤ 45,0; PASS kèm cảnh báo ≤ 50,0 nếu GC heap phẳng; FAIL trên đó);
  - (b) tách task riêng đo RAM rảnh của HostService đầy đủ, Gate 17a chỉ xét RAM của Gate 17a;
  - (c) cứ chạy Verify lần 2/3 với ngưỡng hiện tại.
- **(17a.5) Lần 1/3 có tính vào cầu chì không**: nguyên nhân chính là cây git PC chưa sạch (không phải lỗi mã).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. 17a.4 chọn (a) + (b)** *(khuyến nghị)* | Thêm dải [SLA-WARN] RAM vào Verify-Task16b rồi chạy Verify-Task17a lần 2/3; mở task nhỏ đo RAM HostService sau `v0.17.1` | Đóng được Gate với số đo trung thực, không giấu nhiễu; phần RAM thật được xử lý đúng chỗ |
| B. Sau `v0.17.1` | STT cục bộ sau `ISpeechToTextProvider`, hoặc dịch vụ LAN trên Nitro 5 | Đã có chỗ gắn sẵn; STT tốn RAM nên làm sau phần đo RAM |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. Báo cáo Gate 17a (nhánh `claude/task-17a-net-targets-idle-trim` @`HEAD`, chi tiết `qa_report.md` §26.5):
> - **Verify-Task17a lần 1/3** (`27b7ad2`): E1–E4 ✅ (WAKE P50 8,27 / P95 11,71 ms). E5 ❌ vì cây git PC có `current_task.md` sửa tay chưa commit (đã commit `b9d50c5`) và RAM Task16b 49,7 MB.
> - **Đo `-Trim` lần 2** (cây sạch): `hiddenTrimmed` trimoff 37,4 / 38,0 · trimall 36,8 / 37,7 · trimhide 37,0 / 37,2 MB → không cách dọn nào giảm RAM đo được; giả thuyết bề mặt cửa sổ ẩn bị bác. Theo 17a.2 tôi giữ các cách dọn vô hại, ngưỡng 45,0 MB.
> - **Vấn đề**: selftest (chỉ cửa sổ Cockpit) ổn định 37–38 MB, nhưng Verify-Task16b (HostService đầy đủ) đo 44,9 / 48,7 / 49,7 MB. Phần chênh 7–12 MB nằm ngoài cửa sổ Cockpit, không thuộc phạm vi 17a. Chạy lần 2/3 lúc này gần như tung đồng xu ở E5.
>
> Mời bạn quyết:
> - **(17a.4)** (a) thêm dải [SLA-WARN] cho RAM rảnh Task16b như WAKE (PASS ≤ 45,0; PASS kèm cảnh báo ≤ 50,0 nếu GC heap phẳng; FAIL trên đó); (b) tách task riêng đo RAM HostService đầy đủ; (c) cứ chạy lần 2/3 với ngưỡng hiện tại. Tôi khuyến nghị (a) + (b).
> - **(17a.5)** Lần 1/3 (lỗi do cây git, không do mã) có tính vào cầu chì 3 lần không?
