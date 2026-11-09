# TASK-16: Cockpit UI v2 Chặng 1 ("Dễ Nhìn Hơn" B1–B6 + U1) & Tự Động Hóa Nhẹ Màn Phụ G272 / Loa K450 / Đồng Hồ GT 6 (U2–U3) (`ADR-62`)

## 1. Context Summary & Trạng Thái Xuất Phát (`v0.14.0` @ `1dba5b2`)
- **Gate 12 (`TASK-12 + 12b + 12c`) ĐÃ HOÀN TẤT (`ALL PASS` -> `v0.14.0`)**:
  - Nhờ kiến trúc Thợ nén tách biệt (Out-of-Process Worker), đỉnh bộ nhớ khi chạy `/img` giảm xuống `51.7–58.6 MB` (`<= 70 MB`) và `Private Working Set` lúc rảnh sau 6 ảnh 2K đạt **`35.4–36.4 MB`** (ngang mức không dùng Vision), HUD lệch `0 MB`, Vision Cache P95 đạt `2.188 ms`, `-FastRegression` đạt `55.1 s` với `[SLA-WARN SUMMARY] Count=0`.
  - Architect đã phê duyệt 2 bản vá kiểm thử (`832fa53`, `8477cd2`), chốt ngưỡng SLA Vision Cache (`<= 2.5 ms` WARN / `<= 3.0 ms` FAIL), đóng dấu `v0.14.0` trên `main` và mở **`TASK-16`** trên nhánh `claude/task-16-cockpit-ui-v2`.
- **Hồ sơ Hệ sinh thái Thiết bị Đồng bộ của Nhật Hòa (`Hardware Ecosystem Manifest`)**:
  - **Màn hình**: Chính `LG UltraGear 27G523B-B 200Hz` (GTX 1660) + Phụ `MSI Optix G272 144Hz` (iGPU UHD 730 — nơi đặt `CockpitUI` và mở trình duyệt xem YouTube).
  - **Âm thanh & Thu âm**: Loa thanh `[AV] Samsung Soundbar K450 K-Series` (kết nối qua ngõ `SAMSUNG - HD Audio Driver for Display Audio` / `Speakers Realtek`, đã cài `Equalizer APO 1.4.2` với 2 tệp cấu hình `nexus_k450_music.txt` và `nexus_k450_youtube.txt` trong `C:\Program Files\EqualizerAPO\config\`), Micro rời `FIFINE K669 Microphone`.
  - **Thiết bị đeo & Di động**: Điện thoại `iPhone 14 Pro Max` kết nối đồng hồ `HUAWEI WATCH GT 6` (đã bật đồng bộ thông báo).
  - **Dự án nhỏ chờ mua & Phase 4 (Ghi nhận vào Lộ trình)**: Tai nghe IEM `7Hz Salnotes Zero` (3.5mm không mic), Đèn `LED NEON FLEX 5V-USB ARGB` (điều khiển qua Remote IR / App), và `TV Samsung LAN 192.168.1.97`.

## 2. Mục Tiêu TASK-16 (Ưu Tiên Dễ & Sát Sườn Trải Nghiệm Hằng Ngày Trước)
1. **Triển khai trọn vẹn Cockpit UI v2 Chặng 1 (`B1–B6` + `U1` từ `ui_v2_proposal.md`)**: Giúp giao diện trên màn hình phụ `MSI G272` sạch sẽ, dễ đọc, kéo gọn 3 nấc (`40 / 330 / 640 px`) và hỗ trợ xuyên thấu chuột (`60%` opacity).
2. **Tích hợp 2 Tự động hóa Siêu nhẹ Không tốn RAM (`U2` & `U3`)**:
   - **`U2` (Secondary Monitor YouTube & K450 EQ Auto-Switch)**: Tự nhận diện khi Nhật Hòa xem YouTube trên màn phụ `MSI G272` để tự chuyển cấu hình Equalizer APO của loa `Samsung Soundbar K450` sang chế độ lọc giọng nói (`nexus_k450_youtube.txt`) và thu gọn HUD (nếu đang ở Passive), trả lại cấu hình nghe nhạc (`nexus_k450_music.txt`) khi đóng YouTube.
   - **`U3` (Wrist Haptic Webhook Hook — 15 dòng C#)**: Bắn thông báo đẩy nhẹ qua Webhook (khi được cấu hình URL) sang `iPhone 14 Pro Max` để làm rung đồng hồ `HUAWEI WATCH GT 6` khi `/timer` đếm về 0 hoặc khi `Reality Checker` chạm `[ESCALATED - 3 STRIKES!]`.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Sáu Tinh Chỉnh Hiển Thị "Dễ Nhìn Hơn" (`B1` – `B6`)
- **`B1` (Ẩn bản ghi kiểm thử khỏi `ACTIVE SQUAD` trên UI)**:
  - Phần hiển thị của ô `ACTIVE SQUAD` và danh sách `RECENT DISPATCHES` trên `CockpitUI` lọc bỏ hoàn toàn các bản ghi kiểm thử/selftest (tầng dữ liệu `NEXUS_SELFTEST_KEEP_LATEST` đã xử lý ở `TASK-12c`, nay khóa chặt thêm ở tầng hiển thị UI để người dùng chỉ thấy các phiên làm việc thật).
- **`B2` (Một con số `Private WS` duy nhất — Bỏ dòng `Working Set` gây nhiễu)**:
  - Xóa dòng `Working Set : 152.3 MB` ở cột `LEFT MFD`; hợp nhất hiển thị đúng **một chỉ số `Private WS` duy nhất** (ví dụ `35.8 MB / 60 MB`) đồng nhất tuyệt đối giữa thanh tiêu đề đỉnh và cột `LEFT MFD`.
- **`B3` (Ô giữa luôn ưu tiên Câu trả lời AI gần nhất — Log hệ thống rút xuống 1 dòng trạng thái)**:
  - Khung trung tâm (`CENTER HUD`) dành không gian chính để hiển thị rõ ràng câu trả lời AI gần nhất; khung cuộn log hệ thống (`[Wakeup]`, `[MEM] Dọn khi rảnh...`) được gom gọn xuống **một dòng trạng thái nhỏ (1-line status bar)** ngay phía dưới để không đẩy trôi câu trả lời.
- **`B4` (Chống cắt chữ — Bổ sung Tooltip & Bảng Phím Tắt `F1`)**:
  - Xử lý dứt điểm các nhãn bị cắt chữ (`TextTrimming` / bố cục hợp lý), gắn `ToolTip` đầy đủ khi di chuột vào các mục bị thu hẹp, và thêm phím **`F1`** (chỉ bắt phím khi cửa sổ Cockpit đang Focus, không đăng ký hotkey toàn cục) để bật/tắt nhanh bảng tra cứu phím tắt (`Cheat Sheet`).
- **`B5` (Nhãn trạng thái Thẻ ghim rõ nghĩa)**:
  - Thay chuỗi khó hiểu `[0 thẻ | Alt+P: Pin kết quả]` bằng nhãn rõ ràng: **`Câu trả lời hiện tại: X thẻ`** (hoặc `Câu trả lời hiện tại: 0 thẻ` khi chưa có thẻ tách ra).
- **`B6` (Khóa vị trí an toàn trên màn hình phụ `MSI G272`)**:
  - Đảm bảo cửa sổ `CockpitUI` luôn neo đúng trong vùng làm việc (`WorkingArea`) của màn hình phụ **`MSI G272`** (nếu có màn hình phụ), không tràn sang màn hình chính `LG UltraGear` và không đè lên các cửa sổ làm việc đang active khi khởi động.

### 3.2. Cơ Chế Kéo Lên / Kéo Xuống 3 Nấc & Chế Độ Xuyên Thấu (`U1`)
- **3 Mức Độ Cao Chuẩn (Snap Heights)**:
  - **Thu gọn (`Collapsed = 40 px`)**: Chỉ hiện thanh tiêu đề/trạng thái đỉnh siêu gọn và ô nhập lệnh nhanh khi cần.
  - **Nửa (`Half = 330 px`)**: Chế độ mặc định gọn gàng, vừa xem câu trả lời AI vừa chừa nửa dưới màn hình `MSI G272` cho cửa sổ khác.
  - **Đầy (`Full = 640 px`)**: Hiển thị đầy đủ chi tiết khi đọc câu trả lời dài hoặc bảng ghim.
- **Tương tác Kéo & Nhấp đúp**:
  - Cho phép kéo thanh viền dưới (`Bottom Resize Grip`) để thay đổi chiều cao; khi thả chuột sẽ tự động **bám (snap) vào mức gần nhất** trong 3 mức (`40 / 330 / 640 px`) mà không dùng animation chạy liên tục gây tốn CPU.
  - **Nhấp đúp (Double-Click)** vào thanh tiêu đề hoặc thanh dưới để chuyển nhanh **`Thu gọn (40 px) <-> Nửa (330 px)`**.
  - Lưu và khôi phục vị trí + độ cao cửa sổ giữa các lần chạy; hỗ trợ tùy chọn **tự động thu gọn về `40 px` sau `30 s` rảnh ở chế độ `PASSIVE`** (có thể bật/tắt).
- **Chế độ Xuyên Thấu Chuột (`Click-Through Mode`)**:
  - Khi kích hoạt chế độ xuyên thấu ở trạng thái `PASSIVE`, cửa sổ hạ độ mờ xuống **`60%` (`Opacity = 0.6`)** và bật cờ Win32 `WS_EX_TRANSPARENT | WS_EX_LAYERED` để mọi thao tác nhấp/cuộn chuột đi xuyên qua HUD xuống cửa sổ phía sau (như trình duyệt YouTube trên màn hình `G272`).
  - Nhấn **`Ctrl + Shift + Space`** lập tức tắt xuyên thấu, trả độ rõ về `100%` và đưa con trỏ vào ô lệnh `NEXUS>`.

### 3.3. Tự Động Hóa Nhẹ Khi Xem YouTube Trên Màn Phụ `G272` & Loa `K450` (`U2`) + Đồng Hồ `GT 6` (`U3`)
- **`U2` (YouTube on G272 Detection & Equalizer APO K450 Auto-Switch — 0% CPU Idle)**:
  - Tận dụng chu kỳ kiểm tra trạng thái rảnh sẵn có (không tạo vòng lặp timer tần số cao mới): kiểm tra xem trên màn hình phụ `MSI G272` có cửa sổ trình duyệt hiển thị (`chrome`, `msedge`, `brave`, `firefox`) mang tiêu đề chứa `"YouTube"` hay không.
  - **Khi chuyển sang trạng thái đang xem YouTube trên G272**:
    1. Nếu tệp `C:\Program Files\EqualizerAPO\config\nexus_k450_youtube.txt` tồn tại, chỉ ghi cập nhật `Include: nexus_k450_youtube.txt` vào `C:\Program Files\EqualizerAPO\config\config.txt` **đúng 1 lần khi đổi trạng thái** (bọc trong `try/catch` an toàn, không lỗi nếu thiếu quyền hoặc chưa cài).
    2. Nếu `CockpitUI` đang ở chế độ `PASSIVE` và bật tự thu gọn, ưu tiên thu gọn về `40 px` (hoặc xuyên thấu) để nhường trọn không gian màn hình `G272` cho video YouTube.
  - **Khi đóng hoặc rời YouTube trên G272**:
    1. Khôi phục `C:\Program Files\EqualizerAPO\config\config.txt` về `Include: nexus_k450_music.txt` (chế độ nghe nhạc Hi-Fi).
- **`U3` (15-Line Wrist Haptic Webhook Bridge cho `iPhone 14 Pro Max` -> `HUAWEI WATCH GT 6`)**:
  - Đọc biến môi trường hoặc khóa cấu hình tùy chọn `NEXUS_WATCH_WEBHOOK_URL` (mặc định rỗng = tắt, 0 overhead).
  - Khi bộ đếm `/timer` hoàn tất (`00:00`) hoặc khi `Reality Checker` phát cảnh báo `[ESCALATED - 3 STRIKES!]`, nếu có URL cấu hình, gửi bất đồng bộ (`fire-and-forget` với timeout `3 s`) một bản tin JSON nhỏ `{"event": "...", "title": "NEXUS Hub", "message": "..."}` để `iPhone 14 Pro Max` đẩy thông báo rung sang `HUAWEI WATCH GT 6` (kèm lệnh kiểm thử nhanh `/watch test` hoặc cờ `--selftest ui-v2`).

### 3.4. Phụ Lục Lộ Trình Các Dự Án Nhỏ Kế Tiếp (Ghi Nhận Vào Kiến Trúc, Chưa Code Ở `TASK-16`)
- **`TASK-15` (Ngay sau `TASK-16`)**: Lệnh `/ctx` (đọc ngữ cảnh cửa sổ + tự bắt lỗi C# trong Clipboard), trang MFD `/net` (kiểm tra cổng TCP/UDP & Ping LAN `192.168.1.1`), và kích hoạt `QuickAgentPtt` với `FIFINE K669 Microphone`.
- **`Phase 4` (`TASK-17` & `TASK-18` — Khi Nhật Hòa mua đồ về & Mở rộng Smart Room)**:
  - *Sub-Project Audio-Light*: Tự động chuyển đầu ra âm thanh sang tai nghe IEM `7Hz Salnotes Zero` (kèm profile Equalizer APO `nexus_7hz_fps.txt` kích tiếng bước chân) và bắn Tuya IR đổi màu đèn `LED NEON FLEX 5V-USB ARGB` khi bật `1. Game Boost`.
  - *Sub-Project Living TV*: Đưa bảng điều khiển & Wallpaper động lên `TV Samsung LAN 192.168.1.97` (đồng bộ bật/tắt qua Tuya IR + LAN).

## 4. Ràng Buộc Nghiêm Ngặt & Verification Steps (Nghiệm Thu Gate 16)
- **Ràng buộc Cứng**:
  - `Private Working Set` lúc rảnh **`<= 45.0 MB`** (cổng chặn cứng `<= 52.0 MB`), CPU lúc rảnh **`0%`** (tuyệt đối không dùng Storyboard/Animation lặp vô tận).
  - Không đăng ký thêm bất kỳ phím tắt toàn cục (`RegisterHotKey`) nào mới; phím `F1` chỉ hoạt động nội bộ khi cửa sổ Cockpit đang có Focus; chi phí API = `$0`.
- **Các bước kiểm thử nghiệm thu (`Verify-Task16.ps1` + `--selftest ui-v2`)**:
  1. **E1 (Build `-warnaserror` & Zero-BOM Check)**: Build sạch 0 lỗi, 0 cảnh báo; toàn bộ kho mã nguồn 0-byte BOM; CI GitHub xanh.
  2. **E2 (B1–B6 Display & Layout Verification)**: Xác nhận `LEFT MFD` chỉ còn 1 dòng `Private WS` duy nhất (bỏ `Working Set`), nhãn `Câu trả lời hiện tại: X thẻ`, log hệ thống nằm ở 1 dòng trạng thái đáy, `F1` bật/tắt bảng phím tắt, và cửa sổ đặt đúng trên màn `MSI G272`.
  3. **E3 (U1 Snap Heights `40 / 330 / 640 px`, Double-Click Toggle & Click-Through `WS_EX_TRANSPARENT`)**: Kiểm증 chuyển đổi chính xác giữa 3 mức độ cao (`40`, `330`, `640 px`), lưu/khôi phục trạng thái, tự thu gọn sau khi rảnh, và bật/tắt cờ xuyên thấu chuột (`Opacity = 0.6` <-> `1.0` khi nhấn `Ctrl+Shift+Space`).
  4. **E4 (U2 YouTube G272 EQ Switcher & U3 Watch Webhook Selftest)**: Kiểm thử bộ chuyển đổi tệp `config.txt` giữa `nexus_k450_youtube.txt` và `nexus_k450_music.txt` (khi mô phỏng tiêu đề YouTube trên G272) và kiểm thử hàm bắn Webhook `U3` không chặn luồng.
  5. **E5 (Settled Idle `Private WS <= 45.0 MB`, `0%` Idle CPU & FastRegression PASS)**: Đo trung vị 8 mẫu `Private WS` lúc rảnh đạt `<= 45.0 MB` và chạy `-FastRegression` đạt `ALL PASS`.