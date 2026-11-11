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
- **Cổng tường lửa & chia sẻ trên Nitro 5** — hiện trạng đã kiểm bằng lệnh trên máy ngày 2026-11-12 (chi tiết ở mục 5). Mọi luật vào đang bật chỉ áp cho mạng **Private** và chỉ nhận từ **`192.168.1.212`**:
  - `ICMPv4 Echo Request (Ping)`: luật `NEXUS_Nitro5_ICMPv4_In`. Hai luật trùng `NEXUS_Allow_ICMPv4` đã tắt (không xóa).
  - `TCP 5055`, `TCP 5056`: luật `NEXUS_Nitro5_LAN_Node` (dành cho `NitroVoiceWorker` / `TASK-17` PTT STT và đồng bộ MCP / Handoff). Luật `NEXUS_Nitro5_TCP_In` (mở cho mọi mạng, mọi máy) đã tắt. **Chưa có dịch vụ nào lắng nghe hai cổng này** (`netstat`): dịch vụ thuộc TASK-17, chưa viết.
  - `TCP 6379` (Redis): **chưa cài Redis và chưa có luật tường lửa**. Khi cài (TASK-17 trở đi) mới mở, kèm mật khẩu, chỉ nghe trên `192.168.1.22`, chỉ nhận từ `192.168.1.212`.
  - `TCP 445` (SMB): `File and Printer Sharing (SMB-In)` và bản `(Restrictive)` thu về Private + `192.168.1.212`; trước đây có luật mở cả mạng Public.
  - **SMB Share giao ca**: `\\192.168.1.22\nexus_handoff` → `C:\Agent\NEXUS_Home_Hub\03_Local_Data_Vault\handoff`. Quyền chia sẻ: tài khoản cục bộ **`nexus_smb`** (Change); quyền `Everyone – Full` ban đầu đã gỡ, quyền của tài khoản dự án được gỡ bằng `Revoke-SmbShareAccess` sau khi PC mở thư mục thành công qua `nexus_smb`; NTFS cấp `nexus_smb` Modify trên đúng thư mục `handoff`. PC chính đã lưu thông tin đăng nhập bằng `cmdkey /add:192.168.1.22 /user:LAPTOP-QNE26VPO\nexus_smb`.

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
   - Trong lệnh `/net status`, trang NET (vị trí trên giao diện chờ Architect chốt: sau Gate 16c không còn `RIGHT MFD`) và công cụ MCP `get_network_radar`, bổ sung mục kiểm tra nút **`NITRO5-WORKER`** (`192.168.1.22`, fallback `192.168.1.192`) bên cạnh Gateway `192.168.1.1` và TV `192.168.1.97`.
   - Khi lọc danh sách NIC trên PC chính, ưu tiên giao diện vật lý có Gateway `192.168.1.1` (`Ethernet 192.168.1.212`), không lấy nhầm IP `26.x.x.x` của `Radmin VPN`.
3. **Tuân thủ cổng `E1 Zero-BOM`**:
   - Mọi tệp cấu hình hoặc báo cáo đồng bộ giữa 2 máy đều phải ghi bằng `UTF8Encoding($false)` (0-byte BOM).

## 5. Hiện Trạng Bảo Mật Đã Kiểm (Ca Claude cùng Nhật Hòa, 2026-11-12)
Mọi thay đổi do Nhật Hòa tự chạy bằng PowerShell quyền Admin (Cấp Đỏ). Không xóa luật hay tài khoản nào; tường lửa đã sao lưu trước tại `C:\Agent\fw_backup_20261112.wfw` (khôi phục: `netsh advfirewall import "C:\Agent\fw_backup_20261112.wfw"`).
- **Tên máy**: `LAPTOP-QNE26VPO`. Card mạng: Killer E2600 (Ethernet), Killer AX1650i (Wi-Fi), Bluetooth PAN. **Không có Radmin VPN.**
- **Tài khoản**:
  - Tài khoản dự án (tài khoản Microsoft của Nhật Hòa, Admin, hồ sơ `C:\Users\bitma`): chạy NEXUS, Git, Verify.
  - Tài khoản học cũ `lacki` (Admin, giữ nguyên): **không chạy script NEXUS hay `git` trong `C:\Agent` từ tài khoản này**.
  - `nexus_smb` (cục bộ, không thuộc nhóm nào, mật khẩu không hết hạn): chỉ để PC vào thư mục giao ca.
  - Lý do có `nexus_smb`: PC và tài khoản dự án trên laptop dùng cùng một tài khoản Microsoft. Windows trên PC tự gửi thông tin đăng nhập, laptop chưa có mật khẩu của tài khoản đó (đăng nhập bằng PIN / passkey) nên từ chối liên tục → tài khoản bị khóa tạm ("account is currently locked out").
- **Mã hóa ổ đĩa**: Device encryption đã bật; khóa khôi phục đã lưu trong tài khoản Microsoft của Nhật Hòa. Khóa không ghi vào repo hay tài liệu nào.
- **SMB**: SMB1 tắt. `RequireSecuritySignature` chưa bật (tùy chọn: `Set-SmbServerConfiguration -RequireSecuritySignature $true -Force`).
- **Hồ sơ mạng**: Ethernet ở nhà = Private. Khi dùng Wi-Fi ở trường phải là **Public**; không bật "File and printer sharing" cho Public.
- **Cần làm thêm (Nhật Hòa, trên router)**: DHCP reservation cho PC `192.168.1.212`, laptop `192.168.1.22` (Ethernet) và `192.168.1.192` (Wi-Fi). Mọi luật tường lửa phụ thuộc vào địa chỉ của PC.
- **Kiểm chứng số liệu**: báo cáo đóng Gate chỉ lấy từ PC `bitmappp` (ngân sách RAM / WAKE theo phần cứng PC). Laptop chạy Verify để thử, có trường `machine` trong báo cáo để phân biệt.
