# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12 + 12b + 12c: Multimodal Vision, tiến trình con "thợ nén", ADR-61)

- **Ca thực thi**: Claude (ca viết code), 2026-11-09, 03:20– (TASK-12c theo chỉ thị của Gemini). Các lượt trước: Gate 12 lượt 1–3 ở `qa_report.md` §19.4; lượt TASK-12b ở §19.6; đề xuất TASK-12c / UI v2 ở §19.7.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa, Antigravity
- **Nhánh**: **`claude/task-12-multimodal-vision`**, tách từ `main` @`149756e`
- **Đặc tả**: chỉ thị TASK-12c của Gemini (gửi qua chat Nhật Hòa, 03:20)
- **Chi tiết kỹ thuật**: `qa_report.md` §19.8
- **Trạng thái build**: **CI GitHub xanh trên `24ed095`**, gồm bước mới chạy `vision`, `vision-guard`, `vision-mem` trên runner Windows (đều PASS). `Verify-Task12/13a/13.ps1` trên máy Nhật Hòa **chưa chạy** với code mới.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Đường ảnh | GDI+ chạy trong HostService; khoảng 15–16 MB native còn lại khi rảnh (§19.6) | `visionWorker` = `process` (mặc định): McpTools `--vision-worker` giải mã / chụp / nén rồi thoát; HostService không có kiểu `System.Drawing` nào trong đường này. `inproc` vẫn còn để dự phòng / so sánh |
| 2 | Khiên + cache | Trong HostService | Vẫn trong HostService, chạy **trước** khi khởi động thợ |
| 3 | B1 (dữ liệu kiểm thử trên HUD thật) | Bản ghi selftest hiện ở ACTIVE SQUAD và Recent Dispatches | Cột `is_test`; Recent Dispatches của Cockpit thật bỏ qua bản ghi test; đánh dấu một lần các bản ghi cũ nhận ra được; selftest và 3 script Verify trả lại `latest_dispatch.json` của Nhật Hòa khi xong |
| 4 | Tiêu chí | E3 P95 ≤ 3,0; E5 ≤ 45 (3 ảnh) | Selftest P95 ≤ **2,5**; đỉnh clipboard ≤ **70 MB**; E5: HostService mới, **6 ảnh nối tiếp** trong thợ, `gdiplus.dll` không do vision nạp, trung vị ≤ 45,0, **HUD lệch ≤ 0,5 MB** |
| 5 | CI | Build + smoke | Thêm 3 selftest không giao diện (khoảng 35 s, miễn phí) |
| 6 | Nhánh thử | Có trên GitHub | Đã xóa ở máy Claude; trên GitHub bị chặn 403 → Nhật Hòa xóa (lệnh ở mục 2) |

---

## 2. Trạng thái Gate 12 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (CI xanh, 3 selftest PASS trên runner; chưa chạy trên máy)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-12-multimodal-vision
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task12.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13a.ps1 -SkipBuild
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1 -SkipBuild
```
Verify-Task12 build cả HostService lẫn McpTools (thợ nén cần bản McpTools mới). Không chạy lệnh git nào khi Verify đang chạy; không copy/paste; không mở cửa sổ ngân hàng hay trình quản lý mật khẩu trên G272. Gemini chỉ chạy mock, 0 token.

Dọn nhánh thử trên GitHub (Nhật Hòa chạy một lần, sau khi Verify xong):
```powershell
git push origin --delete claude/task-12-ci-probe
```

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + tinh chỉnh Gate 13 | `-warnaserror` 0/0; 0 BOM; CI xanh | Chưa chạy (CI `24ed095`: 0/0, 0 BOM, `tools/*.ps1` parse sạch) |
| E2 | Nén + đỉnh | JPEG ≤ 250 KB, cạnh ≤ 1280; mọi lần chạy trong thợ; đỉnh HostService khi `/img` clipboard **≤ 70 MB** | Chưa chạy (runner: thợ 296–343 ms/ảnh, JPEG 65 900 B, sha như trong tiến trình) |
| E3 | Vision cache | 25 lần trúng, 0 token; selftest P50 ≤ 2,0 / **P95 ≤ 2,5 ms**; Cockpit P95 ≤ 3,0 | Chưa chạy (runner P50 1,05 / P95 1,21 ms) |
| E4 | Khiên + MCP | `[COMBAT SHIELD]` < 2 ms, 0 lần chụp; `[PRIVACY GUARD]`; MCP → Cockpit < 300 ms | Chưa chạy (runner: combat P95 0,167 ms; `/snap` an toàn qua thợ 212 517 B) |
| E5 | RAM rảnh + chéo Gate | 6 ảnh 2K nối tiếp trong thợ; `gdiplus.dll` không do vision nạp; trung vị 8 lần đọc **≤ 45,0 MB**; HUD lệch ≤ 0,5 MB; CPU 0 %; `vision-mem` PASS; tầng nhanh PASS; Verify-Task13a / 13 ALL PASS | Chưa chạy (runner `vision-mem`: tăng 0,9 MB sau 6 ảnh, 3 lần cuối chênh 0,0) |

---

## 3. Điểm mở cần Architect quyết định

1. **Số phiên bản và số task bị trùng.** Tag `v0.12.0` đã có từ Gate 14 (`f354f97`), và TASK-14 là `/calc` `/timer` `/note`. Đặc tả TASK-12b từng định `v0.14.0` cho Gate 12. Đề xuất: Gate 12 → **`v0.14.0`**, Cockpit UI v2 chặng 1 (B1–B6 + U1) → **TASK-16**. Không ai đụng tag cũ (xóa tag là Cấp Đỏ).
2. **Mỗi ảnh chậm thêm khoảng 180 ms** vì khởi động thợ (runner: 296–343 ms so với khoảng 150 ms trong tiến trình). Chỉ áp cho lần không trúng cache; lúc đó vẫn chờ Gemini vài giây.
3. **`gdiplus.dll` trong HostService**: HostService dùng WinForms cho icon khay hệ thống, nên `System.Drawing` có thể đã nạp từ trước. Verify E5 so module lúc khởi động và sau tải, chỉ chấm trường hợp vision nạp thêm.
4. **Đánh dấu bản ghi test cũ** chỉ nhận ra model `mock` và thẻ `(Gate N EN)`. Vài bản ghi test cũ không có dấu (ví dụ task "Thiết kế C# Async TCP Server…") có thể còn hiện cho tới khi bị dispatch thật đẩy ra khỏi danh sách 5 dòng.
5. **`vision-mem` làm nóng 3 ảnh 2K trước mức nền**: runner cho thấy HostService tăng một lần khoảng 3,4 MB ở 4 lần gọi đầu, kể cả khi ảnh xử lý trong thợ; bậc này giống hệt ở `inproc`, nên không phải GDI+.
6. **Bước CI mới** (3 selftest, khoảng 35 s mỗi lần push, trong phần miễn phí) sẽ theo nhánh này vào `main`.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 12** *(khuyến nghị)* | Claude Code chạy 3 lệnh ở mục 2; sửa tối thiểu nếu lỗi script; ghi §19.9 | Mức rảnh trên Cockpit thật chỉ đo được trên máy |
| B. Merge + tag | Sau khi Gemini duyệt: fast-forward `main`, Nhật Hòa gắn tag (đề xuất `v0.14.0`) | Theo chỉ thị TASK-12c |
| C. Cockpit UI v2 chặng 1 | B1–B6 + U1 (`ui_v2_proposal.md`; đề xuất số TASK-16) | B1 tầng dữ liệu đã làm trong ca này; còn phần hiển thị |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-12c** trên nhánh `claude/task-12-multimodal-vision`. **CI xanh trên `24ed095`**, gồm bước mới chạy 3 selftest không giao diện (`vision`, `vision-guard`, `vision-mem`) trên runner Windows, đều PASS. Mức RAM trên Cockpit thật cần Claude Code chạy Verify-Task12, 13a, 13 trên máy Nhật Hòa. Chi tiết ở `gate12_handoff.md` và `qa_report.md` §19.8.
>
> **Đã làm:**
> - `visionWorker` = `process` (mặc định) | `inproc`. `NEXUSHome.McpTools --vision-worker` giải mã / chụp / nén bằng đúng code cũ (cùng byte, cùng SHA-256), trả header JSON + JPEG qua stdout rồi thoát. Khiên Combat / Privacy và Vision cache vẫn chạy trong HostService trước khi khởi động thợ. HostService không có kiểu `System.Drawing` nào trong đường này. McpTools thiếu hoặc cũ thì dùng đường trong tiến trình và ghi `inproc-fallback`.
> - **B1 tầng dữ liệu**: cột `is_test`, Recent Dispatches của Cockpit thật bỏ qua bản ghi test; selftest và 3 script Verify trả lại `latest_dispatch.json` của Nhật Hòa khi xong.
> - **Tiêu chí mới trong Verify-Task12**: selftest P95 ≤ 2,5 ms; đỉnh clipboard ≤ 70 MB; E5 trên HostService mới với 6 ảnh 2K nối tiếp trong thợ, kiểm `gdiplus.dll` không do vision nạp, trung vị ≤ 45,0 MB, HUD lệch ≤ 0,5 MB.
> - Nhánh thử đã xóa ở máy Claude; trên GitHub bị chặn 403, Nhật Hòa sẽ xóa.
>
> **Số tham khảo trên runner** (headless, không phải số nghiệm thu):
> - thợ 296–343 ms mỗi ảnh 2K (đỉnh của chính thợ 58,7 MB, trả hết khi thoát);
> - `vision-mem`: tăng 0,9 MB sau 6 ảnh, 3 lần cuối chênh 0,0;
> - cache P95 1,21 ms;
> - combat P95 0,167 ms.
>
> **Cần bạn quyết:**
> - (1) **tag `v0.12.0` và số TASK-14 đã dùng** (Gate 14 / tiện ích `/calc`): đề xuất Gate 12 → `v0.14.0`, UI v2 chặng 1 → TASK-16;
> - (2) chấp nhận mỗi lần cache miss chậm thêm khoảng 180 ms do khởi động thợ;
> - (3) `gdiplus.dll` có thể đã nạp từ trước do icon khay WinForms, nên Verify chỉ chấm phần vision nạp thêm;
> - (4) bước CI mới (khoảng 35 s mỗi lần push, miễn phí) theo nhánh vào `main`.
