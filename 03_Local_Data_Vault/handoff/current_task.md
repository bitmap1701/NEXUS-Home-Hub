# TASK-17: Phase 4 Kickoff — Radar Mạng & Cổng Socket (`/net` + Trang MFD `NET`) & Định Tuyến Micro `FIFINE K669` / `QuickAgentPtt` (`/mic`, `/ptt`) (`ADR-67`)

## 1. Context Summary & Trạng Thái Xuất Phát (`v0.16.0` @ `1e1d6cf`)
- **Gate 16b (`v0.15.5` @ `939da6b`) & Gate 15 (`v0.16.0` @ `1e1d6cf`) ĐÃ HOÀN TẤT (`ALL PASS`)**:
  - `TASK-15` (`ADR-62`) vượt qua toàn bộ 5 cổng nghiệm thu ở lần chạy 3/3: `Private WS` lắng ở **`39.2 MB`** (HUD `38.1 MB`), **CPU rảnh `0%`**, toàn bộ chuỗi hồi quy `Verify-Task16b` (`Task16 -> 13 -> 12 -> 13a -> Task14...03`) đạt `ALL PASS` trong `686 s` với **`zeroLiveCalls = True` (`0` lượt gọi Gemini thật)**.
  - **Quyết định Kiến trúc của Architect về `ColdFirstWake` & Đổi độ cao cửa sổ**:
    1. **Không mở task riêng trước Phase 4**: `ColdFirstWake` (`~55 ms` ở `Activate()`) chỉ diễn ra 1 lần duy nhất khi cấp phát bề mặt DWM đầu tiên sau khi khởi động `HostService`; còn chi phí đổi độ cao `40 <-> 330 px` (`5.5–11 ms`) chính là 1 nhịp VSync vật lý (`6.94 ms` @ `144Hz`) của `dwm.exe` trên màn hình phụ `MSI G272` (iGPU UHD 730). Giữ nguyên cơ chế đo hiện tại của `Verify-Task15.ps1`.
    2. **Cập nhật sổ cái ADR**: Trong `TASK-17`, chuyển trạng thái `ADR-62` trong `docs/ADR_REGISTRY.md` sang **`Chốt v0.16.0`** và đăng ký **`ADR-67`** cho `TASK-17` (mục tiêu phát hành **`v0.17.0`**).

## 2. Mục Tiêu TASK-17 (Mở Màn Phase 4 — Không Tốn Phí, 0% CPU Rảnh, Không Đụng Cấp Đỏ)
1. **Hạng mục `N1` — Radar Mạng & Kiểm Tra Socket Cổng Cục Bộ (`/net` + Trang `RIGHT MFD: NET` + MCP `get_network_radar`)**:
   - Phục vụ trực tiếp việc lập trình mạng C# Socket/TCP và kiểm tra kết nối LAN (`192.168.1.1`, TV Samsung `192.168.1.97`) ngay trên buồng lái mà không cần mở `netstat` hay `Resource Monitor`.
2. **Hạng mục `V1` — Bộ Chọn Thiết Bị Thu Âm Thông Minh (`PreferredMicSelector` ưu tiên `FIFINE K669 Microphone`) & Luồng `QuickAgentPtt` (`/mic`, `/ptt`)**:
   - Trên máy Nhật Hòa, phần mềm webcam ảo `e2eSoft iVCam` thường tự chiếm quyền `Default Capture Device` của Windows trong khi micro thật là **`FIFINE K669 Microphone`**. Cần bộ chọn thiết bị tự động nhận diện đúng `FIFINE K669`, bỏ qua các mic ảo (`iVCam`, `DroidCam`, `Steam Streaming`), kết hợp bộ thu PCM 16kHz + VAD (`3. Mic 16kHz VAD Stream`) giải phóng sạch bộ nhớ ngay khi nhả PTT.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Hạng Mục `N1`: Lệnh `/net`, Trang `RIGHT MFD: NET` & MCP `get_network_radar` (`0%` Idle CPU)
- **Nguyên tắc On-Demand (Không chạy vòng lặp ngầm khi không xem)**:
  - Chỉ thu thập thông số mạng khi người dùng gõ lệnh `/net ...`, khi chuyển cột `RIGHT MFD` (`[Alt+Q]`) sang trang **`NET`**, hoặc khi MCP gọi `get_network_radar`. Khi ở trang khác hoặc lúc rảnh, chi phí CPU là **`0%`**.
- **Cú pháp lệnh `/net` (0 token, chạy cục bộ `< 15 ms` cho bảng socket)**:
  - `/net` hoặc `/net status`: Hiển thị tóm tắt:
    1. **Giao diện mạng đang hoạt động (Active NIC)**: Tên card mạng (Ethernet LAN / Intel Wi-Fi 6E AX211), IPv4 cục bộ, trạng thái kết nối.
    2. **Độ trễ ICMP Ping nhanh (Timeout cứng `250 ms`/đích, chạy song song)**: Ping tới Gateway mặc định (ví dụ `192.168.1.1`) và đích LAN tùy chọn trong cấu hình (như TV Samsung `192.168.1.97` — nếu tắt máy thì báo `OFFLINE` trong `<= 250 ms`, không treo luồng).
    3. **Danh sách cổng TCP Listening / Established tiêu biểu**: Dùng API .NET `IPGlobalProperties.GetIPGlobalProperties()` để liệt kê các cổng cục bộ quan trọng (ưu tiên hiển thị các cổng dev/NEXUS hoặc lọc theo yêu cầu).
  - `/net port <số_cổng>` (ví dụ `/net port 5000` hoặc `/net port 6379`): Kiểm tra ngay cổng TCP/UDP đó đang `FREE` hay đang ở trạng thái `LISTENING / ESTABLISHED` để gỡ lỗi `AddressAlreadyInUse` khi làm đồ án Lập trình mạng.
  - `/net ping <host_hoặc_ip>`: Đo nhanh 3 mẫu ICMP ping (`Min / Avg / Max ms`) hiển thị gọn trong ô trả lời.
- **Trang `RIGHT MFD: NET` (Chuyển qua bằng `Alt+Q`)**:
  - Bổ sung trang `NET` vào vòng xoay `Alt+Q` của `RIGHT MFD`, hiển thị gọn: `NIC IPv4`, `Gateway Ping (ms)`, `LAN TV (.97) Status`, và `Top Listening Ports`.
- **Công cụ MCP `get_network_radar`**:
  - Cho phép Claude kiểm tra trạng thái cổng TCP/UDP và độ trễ LAN trên máy Nhật Hòa qua 1 lệnh gọi MCP (`portFilter` tùy chọn).

### 3.2. Hạng Mục `V1`: Định Tuyến Micro `FIFINE K669` (`PreferredMicSelector`) & `QuickAgentPtt` (`/mic`, `/ptt`)
- **Bộ chọn Micro thông minh (`PreferredMicSelector`)**:
  - Liệt kê các thiết bị thu âm (Capture Endpoints) đang `Active` trên Windows.
  - **Quy tắc ưu tiên tự động**:
    1. Khớp chuỗi cấu hình `preferredMicSubstring` trong `cockpit_ui.json` (mặc định: **`"FIFINE"`**).
    2. Nếu không cắm `FIFINE`, chọn thiết bị thu âm vật lý khác nhưng **tự động loại trừ (denylist) các micro ảo** trừ khi người dùng ép chọn: `"iVCam"`, `"DroidCam"`, `"Steam Streaming"`, `"Stereo Mix"`, `"Virtual Audio"`.
- **Các lệnh điều khiển `/mic` và `/ptt` (0 token, chạy cục bộ)**:
  - `/mic` hoặc `/mic status`: Hiển thị micro đang được NEXUS chọn (ví dụ: `[ACTIVE MIC] Mic (FIFINE K669 Microphone)`), trạng thái các micro ảo bị bỏ qua (ví dụ `e2eSoft iVCam [Ignored Virtual Mic]`), và cấu hình thu âm (`16000 Hz, 16-bit, Mono PCM`).
  - `/mic list`: Liệt kê toàn bộ thiết bị thu âm đang cắm kèm nhãn `[Selected]` / `[Virtual-Ignored]`.
  - `/mic select <tên_hoặc_số>`: Lưu thiết bị ưu tiên vào `cockpit_ui.json`.
  - `/ptt test [số_giây]` (hoặc bật/tắt nút `3. Mic 16kHz VAD Stream` trên `LEFT MFD`):
    - Mở luồng thu PCM `16 kHz, 16-bit, Mono` trong thời gian ngắn (mặc định `2 s` khi test hoặc theo trạng thái giữ PTT), tính mức năng lượng âm thanh **`RMS dBFS`** và cờ phát hiện giọng nói **`VAD (Voice Activity Detected)`** theo ngưỡng năng lượng + Zero-Crossing Rate (ZCR).
    - Ngay khi kết thúc phiên PTT hoặc tắt nút `3. Mic 16kHz VAD Stream`, **đóng thiết bị thu và giải phóng hoàn toàn bộ đệm âm thanh** để `Private WS` trở về mức lắng `<= 45.0 MB` và `CPU = 0%`.
    - Cung cấp giao diện `ISpeechToTextProvider` (Offline/Mock-First: mặc định dùng `LocalVadTranscriptStub` không tốn phí và không gọi mạng; sẵn sàng gắn Whisper.net cục bộ hoặc Cloud STT khi Nhật Hòa yêu cầu).

### 3.3. Cập Nhật Tài Liệu & Sổ Cái Kiến Trúc
- Cập nhật `docs/ADR_REGISTRY.md`:
  - Đổi trạng thái `ADR-62` (`TASK-15`) sang **`Chốt v0.16.0`**.
  - Thêm **`ADR-67`** cho `TASK-17` (`/net` Network & Socket Radar + `FIFINE K669` Smart Mic Routing & `QuickAgentPtt` VAD Stream).
- Cập nhật `ROADMAP_FUTURE.md` đồng bộ trạng thái `v0.16.0` và `TASK-17` (`v0.17.0`).

## 4. Ràng Buộc Nghiêm Ngặt & Các Cổng Nghiệm Thu (`Verify-Task17.ps1`)
- **Ràng buộc Cứng**:
  - `Private Working Set` lúc rảnh **`<= 45.0 MB`** (ngưỡng cứng `<= 52.0 MB`), **CPU lúc rảnh `0%`**, **`zeroLiveCalls = True`** (0 lượt gọi Gemini thật trong toàn bộ kiểm thử), **0-byte BOM** trên toàn repo.
  - Không thêm phím tắt toàn cục (`RegisterHotKey`) mới để tránh xung đột với các ứng dụng khác.
- **5 Cổng Nghiệm Thu (`E1` – `E5`) trong `Verify-Task17.ps1` + `--selftest net-ptt`**:
  1. **E1 (Build `-warnaserror`, Zero-BOM & PS 5.1 Syntax)**: Build sạch 0 lỗi, 0 cảnh báo; 0-byte BOM trên toàn bộ tệp; `ADR-62` đã chốt `v0.16.0` và `ADR-67` có mặt trong `docs/ADR_REGISTRY.md`.
  2. **E2 (`/net` Radar, Port Inspector & MCP `get_network_radar` Selftest)**: Mở một `TcpListener` cục bộ trên cổng ngẫu nhiên (ephemeral port), kiểm chứng `/net port <port>` và MCP `get_network_radar` phát hiện chính xác trạng thái `LISTENING` trong `< 15 ms`, sau đó đóng cổng và xác nhận trạng thái `FREE`; kiểm chứng ping timeout an toàn `<= 300 ms`.
  3. **E3 (`PreferredMicSelector` FIFINE Priority & Virtual Mic Denylist)**: Kiểm thử đơn vị và thực tế xác nhận danh sách thiết bị có cả `"e2eSoft iVCam"` (mặc định OS) và `"Mic (FIFINE K669 Microphone)"` sẽ luôn chọn **`FIFINE K669 Microphone`** và gắn cờ bỏ qua `e2eSoft iVCam`.
  4. **E4 (`QuickAgentPtt` 16kHz VAD Engine & Zero-Leak Buffer Disposal)**: Kiểm thử luồng PCM 16kHz với sóng âm tổng hợp (im lặng vs giọng nói 440Hz/1kHz), xác nhận `VAD = False` khi im lặng và `VAD = True` khi có tín hiệu, đồng thời bộ đệm thu âm được giải phóng sạch 100% sau khi đóng phiên.
  5. **E5 (Settled Idle `Private WS <= 45.0 MB`, `0%` Idle CPU & Full Regression `-SkipLive` Zero Live Calls)**: Đo RAM rảnh trên máy Nhật Hòa đạt `<= 45.0 MB`, CPU `0%`, và chạy chuỗi hồi quy `-SkipLive` đảm bảo `zeroLiveCalls = True`.