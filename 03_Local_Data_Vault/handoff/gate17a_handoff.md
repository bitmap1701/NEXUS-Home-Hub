# 📦 HỒ SƠ BÀN GIAO — Gate 17a (TASK-17a, ADR-69 → `v0.17.1`): `networkTargets` tùy chọn + dọn giao diện lúc rảnh (RAM ≤ 43,5 MB)

- **Ca thực thi**: Claude, 2026-11-12 (rạng sáng)
- **Gửi tới**: Nhật Hòa / Claude Code (đo và Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-17a-net-targets-idle-trim` (đặc tả `8f62675`, nền `v0.17.0` + hồ sơ đóng Gate 17 `aaa5ec7`)
- **Chi tiết kỹ thuật**: `qa_report.md` §26
- **Trạng thái build**: Claude **chưa build** trên máy mình; CI Windows runner build `-warnaserror` + selftest. Chưa có số đo nào trên máy Nhật Hòa.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Đích LAN của `/net` | Hằng số trong code | `networkTargets` tùy chọn trong `nexus_config.json` (tối đa 8), áp dụng ngay khi `/reload`; thiếu / rỗng / hỏng → 4 đích mặc định |
| 2 | Tên đích mặc định | `GW`, `NITRO5`, `NITRO5-WIFI`, `TV` | `Gateway`, `NITRO5-LAN`, `NITRO5-WIFI`, `LAN-TV` (theo đặc tả) |
| 3 | Dòng `⚡ NITRO5` | Theo nhãn cố định | Mục đầu tiên nhãn `NITRO5*` hoặc `.22` / `.192` trả lời; không có mục nào → "không có trong networkTargets" |
| 4 | Dọn `[MEM]` lúc rảnh | Chỉ GC + bỏ câu trả lời lịch sử vẽ sẵn | Thêm: chữ trang `MẠNG // /NET`, chuỗi thẻ 8 → 4 khi PASSIVE, khung trả lời khi ẩn; trả lại sau đánh thức (Background, ngoài WAKE) |
| 5 | Đo trước | — | `ui-footprint` thêm pha `net`, `hiddenTrimmed`, biến thể `trimoff/all/live/pool`; `Measure-CockpitFootprint.ps1 -Trim` |
| 6 | Kiểm thử | — | Selftest `net-targets` (CI), `Verify-Task17a.ps1`; Verify-Task16b ngân sách RAM 45,0 → 43,5 MB |
| 7 | Sổ cái | ADR-69 trống | ADR-69 đang triển khai (`v0.17.1`), số trống kế tiếp ADR-70; ROADMAP thêm TASK-17a |

---

## 2. Trạng thái Gate 17a — ⏳ chưa đủ bằng chứng (bước 1: đo trên PC; bước 2: Verify lần 1/3)

| # | Bằng chứng | Cần |
|---|------------|-----|
| E1 | build `-warnaserror` 0/0, Zero-BOM, ADR-67 chốt `v0.17.0`, ADR-69 có, selftest `net-targets` | ✅ |
| E2 | `LOOPBACK-TEST` + `NITRO5-LAN` → `/reload` → `/net` và MCP dùng ngay (`LOOPBACK-TEST` ONLINE), cùng HostService | ✅ |
| E3 | Thiếu / `[]` / `{"label":"","host":null}` → 4 đích mặc định, 0 lỗi | ✅ |
| E4 | `ui-footprint` có pha `net` + `hiddenTrimmed`; dọn khi hiện (≤ 4 dòng, chữ trang mạng) và khi ẩn (khung trả lời); đánh thức trả lại; WAKE P50 ≤ 10 / P95 ≤ 16 ms | ✅ |
| E5 | RAM rảnh Task17a và Task16b ≤ 43,5 MB (mục tiêu ~42), các Gate khác ≤ 45, CPU 0 %, chuỗi Verify-Task17 → … → 03 PASS, 0 lượt gọi Gemini thật | ✅ |

### Bước 1 — đo trước (không phải lượt Verify, khoảng 3–4 phút)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-17a-net-targets-idle-trim
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Measure-CockpitFootprint.ps1 -Trim
```
Kết quả: bảng trên màn hình + `03_Local_Data_Vault/handoff/task17a_footprint_trim.json` (git bỏ qua). Hàng cần xem: `hidden` và `hiddenTrimmed` của `trimoff` so với `trimall`.

### Bước 2 — Verify lần 1/3 (sau khi Claude đọc số đo; PC rảnh ~20 phút)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task17a.ps1
```
Kết quả: `03_Local_Data_Vault/handoff/task17a_verification.json`. Script ghi tạm `networkTargets` vào `nexus_config.json` rồi trả lại đúng file cũ và `/reload`.

---

## 3. Điểm mở cần Architect quyết định

- **(17a.1) Cách dọn nào giữ lại**: chờ số đo `-Trim`. Nếu một cách không giảm được RAM đo được (trong mức nhiễu ±1 MB), Claude đề xuất bỏ nó để giữ mã gọn.
- **(17a.2) Nếu cả ba cách vẫn không đưa Task16b về ≤ 43,5 MB**: phần còn lại nằm ngoài các đối tượng giao diện (bộ nhớ gốc của WPF, bề mặt cửa sổ layered ~1,4 MB ở 330 px). Khi đó cần Architect chọn: giữ 43,5 MB và tìm hướng khác, hay đặt lại ngưỡng.
- **(17a.3) Đổi tên đích mặc định** (`GW` → `Gateway`, `TV` → `LAN-TV`, `NITRO5` → `NITRO5-LAN`) theo đặc tả: thẻ Radar và MCP hiện tên mới.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đo `-Trim` trên PC rồi Verify-Task17a lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | Đúng thứ tự "đo trước" của đặc tả |
| B. Sau `v0.17.1` | STT cục bộ sau `ISpeechToTextProvider`, hoặc dịch vụ LAN trên Nitro 5 | Đã có chỗ gắn sẵn |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **TASK-17a đã viết xong** trên `claude/task-17a-net-targets-idle-trim` (chi tiết `qa_report.md` §26, `gate17a_handoff.md`):
> - N1a: `networkTargets` tùy chọn trong `nexus_config.json` (tối đa 8), `/net` đọc ở mỗi lần dò nên `/reload` áp dụng ngay; thiếu / rỗng / mục hỏng / cả file hỏng → 4 đích mặc định đặt tên đúng đặc tả (`Gateway`, `NITRO5-LAN`, `NITRO5-WIFI`, `LAN-TV`); dòng `⚡ NITRO5` lấy mục `NITRO5*` hoặc `.22` / `.192`; MCP dùng cùng cấu hình.
> - M1a: lượt `[MEM]` lúc rảnh thêm 3 cách dọn (chữ trang `MẠNG // /NET`; chuỗi thẻ 8 → 4 khi PASSIVE; khung trả lời khi ẩn), trả lại sau đánh thức ở mức Background ngoài bộ đếm WAKE. Theo đúng "đo trước", chúng nằm sau biến chẩn đoán để `Measure-CockpitFootprint.ps1 -Trim` đo A/B trên PC (`trimoff` = hành vi `v0.17.0`) trước lượt Verify.
> - Verify-Task17a E1–E5 theo đặc tả; Verify-Task16b E5 ngân sách 43,5 MB; ADR-69 vào sổ (trống kế tiếp ADR-70).
>
> Bước tiếp: Nhật Hòa chạy `Measure-CockpitFootprint.ps1 -Trim` trên PC, tôi đọc số rồi mới cho chạy Verify-Task17a lần 1/3. Nếu cả ba cách vẫn không đưa Task16b về ≤ 43,5 MB, tôi sẽ báo số đo để bạn quyết (17a.2).
