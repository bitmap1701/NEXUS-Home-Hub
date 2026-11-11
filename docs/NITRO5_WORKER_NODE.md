# NEXUS Nitro-Edge Worker Node Specification (Acer Nitro 5)

## 1. Vai Trò Kiến Trúc (2-Node Opportunistic Topology)
- **Main Cockpit Node (`bitmappp` - Desktop PC)**:
  - Phần cứng: `Intel Core i5-12400` + `iGPU UHD 730` + `NVIDIA GTX 1660` + Màn chính `LG 27G523B-B (200Hz)` + Màn phụ `MSI Optix G272 (144Hz)`.
  - Nhiệm vụ: Chạy `CockpitUI` + `HostService` siêu nhẹ (`Private WS <= 45.0 MB`, `CPU rảnh 0%`), thu phím tắt và phát âm thanh (`Samsung Soundbar K450` / IEM).
- **Secondary Worker Node (`Acer Nitro 5` - Laptop Phụ)**:
  - Đường dẫn dự án đồng bộ: `C:\Agent\NEXUS_Home_Hub` (đã cài đặt `.NET 9 SDK`, `Git`, mở `ExecutionPolicy RemoteSigned` và chạy thành công toàn bộ project + `Verify-Task16c.ps1`).
  - Nhiệm vụ: Trạm gia tốc tùy chọn (`Opportunistic Heavy Worker`) tại nhà — gánh chạy kiểm thử hồi quy `Verify-Task*.ps1`, giải mã giọng nói cục bộ (`Voice STT Worker` cho mic `FIFINE K669`), chạy `Redis` / Socket Server và đồng bộ `handoff`. Khi Nhật Hòa rút sạc mang đi học, laptop hoạt động độc lập như một trạm `CockpitUI` di động.

## 2. Cấu Hình Mạng Nội Bộ (LAN Topology & Firewall Ports)
- **Default Gateway chung**: `192.168.1.1`
- **Địa chỉ IP của Main Cockpit PC (`bitmappp`)**:
  - Cổng dây `Ethernet`: **`192.168.1.212`**
  - *Lưu ý*: Trên PC chính có card mạng ảo `Radmin VPN` (`26.253.18.21` / Gateway `26.0.0.1`) — khi code tính năng `/net` (`TASK-17`), ưu tiên nhận diện card vật lý `Ethernet` (`192.168.1.x`) thay vì card VPN ảo.
- **Địa chỉ IP của Acer Nitro 5 Worker Node**:
  - Cổng dây `Ethernet` (Ưu tiên khi cắm tại nhà): **`192.168.1.22`** (Ping thực tế từ PC chính: `1ms - 4ms`, trung bình `2ms`, `0% loss`).
  - Cổng `Wi-Fi` (Khi không cắm dây LAN tại nhà): **`192.168.1.192`**.
- **Cổng Tường Lửa & Chia Sẻ Nội Bộ Đã Mở Trên Nitro 5 (`Private Network`)**:
  - `ICMPv4 Echo Request (Ping)`: `Allowed` (cả 2 chiều giữa `192.168.1.212` và `192.168.1.22`).
  - `TCP 5055`: Dành cho dịch vụ giải mã giọng nói qua LAN (`NitroVoiceWorker` / `TASK-17` PTT STT Offload).
  - `TCP 5056`: Dành cho đồng bộ tín hiệu MCP / Handoff qua LAN.
  - `TCP 6379`: Dành cho Redis Server nội bộ.
  - **SMB Share giao ca**: `\\192.168.1.22\nexus_handoff` (trỏ trực tiếp vào `C:\Agent\NEXUS_Home_Hub\03_Local_Data_Vault\handoff` với quyền `Modify` cho `*S-1-5-32-545`).

## 3. Chế Độ Nguồn & Di Động (Power & Mobility State)
- **Khi cắm sạc ở nhà (`Plugged in`)**:
  - `When I close the lid`: `Do nothing` (gập nắp màn hình máy vẫn chạy ngầm).
  - `Put the computer to sleep`: `Never` (`Turn off the display`: `3 minutes`).
- **Khi rút sạc mang đi học (`On battery`)**:
  - `When I close the lid`: `Sleep` (gập máy bỏ vào balo tự động ngủ ngay).
  - `Put the computer to sleep`: `10 minutes`.

## 4. Ràng Buộc Kỹ Thuật Dành Cho Claude Khi Triển Khai `TASK-17` (`ADR-67`) & Các Task Sau
1. **Quy tắc `Opportunistic Non-Blocking` (Không bao giờ treo PC chính khi Nitro 5 đi học)**:
   - Mọi kết nối thăm dò từ PC chính (`192.168.1.212`) sang Nitro 5 (`192.168.1.22` hoặc `192.168.1.192`) bắt buộc có timeout cứng **`<= 200 ms`**.
   - Nếu Nitro 5 không phản hồi (đang ngủ hoặc mang đi học), đánh dấu trạng thái `NITRO5: OFFLINE (AWAY)` và tự động lùi về xử lý cục bộ trên PC chính mà không văng lỗi.
2. **Tích hợp vào Radar Mạng `/net` (`TASK-17` / `ADR-67`)**:
   - Trong lệnh `/net status`, trang `RIGHT MFD: NET` và công cụ MCP `get_network_radar`, bổ sung mục kiểm tra nút **`NITRO5-WORKER`** (`192.168.1.22`, fallback `192.168.1.192`) bên cạnh Gateway `192.168.1.1` và TV `192.168.1.97`.
   - Khi lọc danh sách NIC trên PC chính, ưu tiên giao diện vật lý có Gateway `192.168.1.1` (`Ethernet 192.168.1.212`), không lấy nhầm IP `26.x.x.x` của `Radmin VPN`.
3. **Tuân thủ cổng `E1 Zero-BOM`**:
   - Mọi tệp cấu hình hoặc báo cáo đồng bộ giữa 2 máy đều phải ghi bằng `UTF8Encoding($false)` (0-byte BOM).