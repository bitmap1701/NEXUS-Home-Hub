# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12: Multimodal Vision `/img` & `/snap`, JPEG ≤ 250 KB, Vision cache, Combat/Privacy Shields, ADR-61)

- **Ca thực thi**: Claude, 2026-11-09 (sau khi Gemini đóng Gate 13 / `v0.13.0` @`e62ddb7`)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude Code trên máy Nhật Hòa
- **Nhánh**: **`claude/task-12-multimodal-vision`**, tách từ `main` @`149756e`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-12)
- **Chi tiết kỹ thuật**: `qa_report.md` §19
- **Trạng thái build**: C# đã được **CI GitHub build xanh**. Selftest `vision` và `vision-guard` đã **PASS trên runner Windows của CI**. `Verify-Task12.ps1`, clipboard thật, màn hình G272, giao diện và số đo trên máy Nhật Hòa **chưa chạy**.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Right MFD, thẻ code | Tiêu đề và dòng đầu phần xem trước trùng nhau | Phần xem trước bắt đầu từ dòng sau tiêu đề; thẻ một dòng không in xem trước |
| 2 | Lịch sử clipboard Win+V | Các khối `/copy` vào lịch sử Win+V | Đặt `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0, `CanUploadToCloudClipboard` = 0 mỗi lần ghi |
| 3 | `/img` | Không có | Clipboard (PNG / CF_DIB) hoặc `--file`, `--fresh`; thiếu ảnh → `[VISION INFO] No image found in Clipboard…` |
| 4 | Nén ảnh | Không có | GDI+ (`System.Drawing.Common`), cạnh ≤ 1280, JPEG 78 → 60 → 45 rồi thu 3/4, **luôn ≤ 256 000 B**; SHA-256 trên byte JPEG; giải phóng ảnh thô ngay |
| 5 | `/snap` | Không có | Combat-Mode Shield (chặn trước mọi API màn hình), Privacy Window Guard (foreground + mọi cửa sổ hiện trên vùng chụp); chụp vùng làm việc G272, che đen Cockpit |
| 6 | Vision cache | Không có | Khóa `SHA-256("VISION_V1\|sha\|prompt\|agent\|model")` trong bảng cache ADR-44; trúng cache trả ngay, 0 token |
| 7 | Gemini | Chỉ văn bản | `inline_data` JPEG Base64 + `text`; mock có dấu `[VISION-MOCK …]` |
| 8 | `latest_dispatch.json`, cấu hình, pipe, MCP | — | `hasVisionAttachment`, `visionImageBytes`, `visionImageSha256`; `maxVisionImageBytes`, `maxVisionDimensionPx`, `privacyRedactedWindowKeywords`; pipe `VISIONSTATE`; MCP `analyze_visual_input` |
| 9 | Kiểm thử | — | Selftest `vision`, `vision-guard`; `Verify-Task12.ps1` (E1–E5); CI kiểm cú pháp mọi `tools/*.ps1` |
| 10 | Lỗi CI bắt được | Lượt thử 1: `pixelMissOk` FAIL | Bản sửa `07353e3`: phép thử đổi pixel trên ảnh gửi nguyên kích thước (1280×720); cặp 2K kiểm "nhất quán". Lượt thử 2: PASS |

---

## 2. Trạng thái Gate 12 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (CI xanh, selftest PASS trên runner; chưa chạy trên máy)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-12-multimodal-vision
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task12.ps1
```
Không chạy lệnh git nào trong repo khi Verify đang chạy. Gemini chỉ chạy mock, không tốn token. Script đặt một ảnh thử lên clipboard rồi khôi phục chữ trong clipboard; `/snap` an toàn chụp G272 thật một lần, ảnh chỉ nằm trong RAM. Kết quả ghi vào `task12_verification.json`.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + tinh chỉnh Gate 13 | `-warnaserror` 0/0; 0 vi phạm BOM; thẻ code không lặp tiêu đề; clipboard có cờ `ExcludeClipboardContentFromMonitorProcessing` | Chưa chạy (CI: build 0/0, 0 BOM; selftest `previewOk`, `historyOptOutInSource` PASS trên runner) |
| E2 | Nén + `/img` | Ảnh 2560×1440 > 2 MB → JPEG ≤ 256 000 B, cạnh ≤ 1280; mock nhận đúng số byte + SHA-256; clipboard và `--file` trên Cockpit thật | Chưa chạy (runner: 8,36 MB → 65 900 B, 1280×720) |
| E3 | Vision cache | 25 lần trùng: trúng 100 %, 0 token, **P50 ≤ 2,0 / P95 ≤ 3,0 ms**; đổi 1 pixel hoặc đổi câu hỏi → trượt | Chưa chạy (runner: 25/25, P50 0,986 / P95 1,123 ms; pixel và câu hỏi trượt đúng) |
| E4 | Khiên + MCP | `[COMBAT SHIELD]` < 2 ms, không chụp; `[PRIVACY GUARD]` với "Vietcombank - Password Manager"; `/snap` + MCP `analyze_visual_input` lên Cockpit < 300 ms, foreground không đổi | Chưa chạy (runner: combat P95 0,183 ms, 0 lần chụp; privacy chặn ở `Password`) |
| E5 | RAM + CPU + hồi quy | Sau ảnh 2K: Private WS (2 lần đọc ổn định) **≤ 52,0 MB**; CPU rảnh < 0,5 %; `-FastRegression` PASS có `[SLA-WARN SUMMARY]`; Git không đổi; CI xanh | Chưa chạy |

---

## 3. Điểm mở cần Architect quyết định

1. **GDI+ (`System.Drawing.Common` 8.0.8, gói của Microsoft) thay cho WIC qua WPF**: `NEXUSHome.Core` và McpTools không có WPF, mà cả hai đều phải nén ảnh.
2. **Clipboard vẫn dùng Win32**; các cờ chống lịch sử đặt bằng `RegisterClipboardFormat` trong cùng lần mở clipboard, thay vì `DataObject.SetData`.
3. **Phép thử "đổi 1 pixel" dùng ảnh 1280×720.** Trên ảnh 2560×1440, một pixel có thể biến mất sau thu 2:1 và JPEG; JPEG giống hệt từng byte thì Gemini nhận đúng ảnh cũ, nên trúng cache là đúng. Khóa vẫn băm JPEG đúng §3.2/§3.4.
4. **Khiên quyền riêng tư kiểm mọi cửa sổ đang hiện trên vùng chụp**, không chỉ cửa sổ foreground.
5. **Khiên chỉ dựa vào tiêu đề cửa sổ**: trang nhạy cảm có tiêu đề vô hại vẫn chụp được. `/snap` nên là thao tác người dùng chủ động gọi.
6. **Nhánh thử `claude/task-12-ci-probe`** (chạy selftest trên runner, miễn phí) không merge. Nhật Hòa có thể xóa nhánh này trên GitHub sau Gate 12 (Claude không tự xóa).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 12** *(khuyến nghị)* | Claude Code chạy `Verify-Task12.ps1`; sửa tối thiểu nếu lỗi; ghi §19.4 | Selftest đã PASS trên runner; còn thiếu clipboard thật, G272, giao diện, RAM trên máy |
| B. TASK-15 | `/ctx` | Bước tiếp theo theo lộ trình sau ADR-61 |
| C. Đưa selftest không giao diện vào CI chính | Chạy `vision`, `vision-guard`, `widgets`, `mfd-pins` trên mỗi push | Lượt thử đã bắt được lỗi thật trước khi lên máy Nhật Hòa; mỗi lượt khoảng 50 s, vẫn trong phần miễn phí |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-12 (ADR-61)** trên nhánh `claude/task-12-multimodal-vision` (tách từ `main` @`149756e`). C# đã được **CI GitHub build xanh**, và hai selftest không giao diện `vision` và `vision-guard` đã **PASS trên runner Windows của CI**. Clipboard thật, G272, giao diện và RAM cần Claude Code chạy `Verify-Task12.ps1` trên máy Nhật Hòa. Chi tiết ở `gate12_handoff.md` và `qa_report.md` §19.
>
> Đã làm:
> - 2 tinh chỉnh Gate 13: bỏ dòng xem trước trùng tiêu đề; cờ `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory` = 0 khi ghi clipboard.
> - `/img` (clipboard hoặc `--file`, `--fresh`), `/snap` (G272, che đen Cockpit), nén GDI+ ≤ 256 000 B, cạnh ≤ 1280.
> - Combat-Mode Shield chặn trước mọi API màn hình; Privacy Window Guard kiểm foreground và mọi cửa sổ trên vùng chụp.
> - Vision cache `SHA-256("VISION_V1|sha|prompt|agent|model")` trong bảng cache ADR-44; Gemini `inline_data`; `latest_dispatch.json` thêm 3 trường vision; MCP `analyze_visual_input`.
>
> Số tham khảo trên runner CI (không phải số nghiệm thu): ảnh 2K 8,36 MB → JPEG 65 900 B; 25 lần trúng cache P50 0,986 / P95 1,123 ms, 0 token; `[COMBAT SHIELD]` P95 0,183 ms, 0 lần chụp. Lượt thử đầu bắt được lỗi phép thử đổi pixel (pixel biến mất khi thu 2K → 1280 rồi nén JPEG); đã sửa ở `07353e3`.
>
> Cần bạn quyết:
> - (1) GDI+ `System.Drawing.Common` thay cho WIC/WPF;
> - (2) giữ clipboard Win32, đặt cờ bằng `RegisterClipboardFormat`;
> - (3) phép thử đổi 1 pixel dùng ảnh 1280×720 (ảnh gửi nguyên kích thước);
> - (4) khiên quyền riêng tư kiểm mọi cửa sổ trên vùng chụp;
> - (5) chấp nhận giới hạn "chỉ dựa vào tiêu đề cửa sổ".
