# TASK-17: Phase 4 — Radar Mạng `/net` (2-Column UI + `NITRO5-WORKER`), Định Tuyến Micro `FIFINE K669` (`/mic`, `/ptt` VAD) & Chống Treo Clipboard / Đổi Chiều Cao Cửa Sổ (`ADR-67` -> `v0.17.0`)

## 1. Context Summary & Quyết Định Đóng Gate 16c (`v0.16.5` @ `a09d860`)
- **Gate 16c (`TASK-16c` / `ADR-68` @ `a09d860`) ĐÃ CHỐT (`v0.16.5`)**:
  - `E1–E4` tự thân đạt 100% (`WAKE P50 7.93 ms / P95 12.01 ms`, `Private WS` rảnh **`42.3 MB`**, `CPU 0%`, `zeroLiveCalls = True`), toàn bộ chuỗi hồi quy `Task03..16b` giữ `Private WS <= 45.0 MB`.
  - **4 Quyết định của Architect (`gate16c_handoff.md §3.6 – §3.9`)**:
    1. **(3.9a)**: Đóng Gate 16c theo phương án `(a)`; ghi nhận 2 số đo lệch nhẹ do VSync DWM ở hồi quy (`Task16b P95 17.69 ms` với 9/10 mẫu `7.5–10 ms`; `Task15 P50 10.27 ms` với `P95 12.49 ms`) vào diện **`[SLA-WARN]`** và gộp bước tối ưu đổi chiều cao cửa sổ vào `TASK-17`.
    2. **(3.6)**: Ba lượt treo do deadlock `WM_DESTROYCLIPBOARD` trong script kiểm thử (đã sửa ở `e8bd63d`) không tính vào cầu chì.
    3. **(3.7)**: Gộp hạng mục chống treo luồng UI khi ghi Clipboard (`ClipboardService` Non-Blocking / Timeout) vào `TASK-17`.
    4. **(3.8)**: Chốt vị trí hiển thị `/net` và `NITRO5-WORKER` trên bố cục 2 cột của `TASK-16c` (chi tiết ở Mục 2.1 bên dưới).

## 2. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 2.1. Hạng Mục `N1` (Quyết định 3.8): Lệnh `/net`, Dòng `NITRO5-WORKER` & Lật Khung `Alt+Q` Trên Bố Cục 2 Cột (`0%` Idle CPU)
- **Trong Cột Hội Thoại (Cột trái)**:
  - **Lệnh đơn lẻ (`/net port <số_cổng>`, `/net ping <ip_hoặc_host>`)**: Hiển thị thành **1 dòng cục bộ gọn** giống `/calc` kèm nhãn `cục bộ · 0 token` (ví dụ: `/net port 5055 → FREE (0,8 ms) [cục bộ · 0 token]`).
  - **Lệnh tổng quan (`/net` hoặc `/net status`)**: Hiển thị **1 thẻ Radar Mạng** trong chuỗi thẻ hội thoại (tái sử dụng Card Pool `8/20`), gồm:
    1. **Active Physical NIC**: Ưu tiên card vật lý có Default Gateway `192.168.1.1` (`Ethernet 192.168.1.212`), bỏ qua card ảo `Radmin VPN` (`26.253.18.21`).
    2. **LAN Targets (ICMP Ping song song, Timeout cứng `<= 200 ms`, chỉ đo khi gọi lệnh)**:
       - `Gateway`: `192.168.1.1`
       - `NITRO5-WORKER`: `192.168.1.22` (fallback Wi-Fi `192.168.1.192`). Theo đúng `docs/NITRO5_WORKER_NODE.md` (`56aba96`), hiện chỉ kiểm tra ICMP Ping (vì `5055/5056/6379` chưa có dịch vụ lắng nghe). Nếu tắt máy đem đi học thì hiển thị nhãn xám `AWAY / OFFLINE` mà không báo lỗi.
       - `LAN TV`: `192.168.1.97` (`ONLINE` / `OFFLINE`).
    3. **Top Listening TCP Ports**: Liệt kê nhanh các cổng TCP đang `LISTENING` trên `bitmappp` qua `IPGlobalProperties`.
- **Trên Cột Phải `300 px` & Phím `Alt+Q`**:
  - Giữ nguyên bố cục Bảng 1 (`THẺ ĐÃ GHIM n/5` ở trên, `⏱ Hẹn giờ` ở giữa, `LIÊN MINH` ở đáy).
  - Ngay dưới dòng `⏱ Hẹn giờ`, thêm **1 dòng trạng thái tĩnh mảnh**: `⚡ NITRO5 (.22) · <Trạng thái lần đo gần nhất>` (chỉ cập nhật khi người dùng chạy `/net` hoặc khi đọc giao ca, `0% CPU` nền).
  - **Phím `Alt+Q`**: Lật phần khung trên của cột phải (`300 px`) qua lại giữa **`THẺ ĐÃ GHIM n/5`** ↔ **`MẠNG // /NET`** (hiển thị bản chụp `/net` gần nhất).
- **Công cụ MCP `get_network_radar`**: Trả về JSON trạng thái NIC vật lý, Ping LAN (`Gateway`, `NITRO5-WORKER`, `TV`) và trạng thái cổng TCP/UDP (`portFilter` tùy chọn).

### 2.2. Hạng Mục `V1`: Định Tuyến Micro `FIFINE K669` (`PreferredMicSelector`) & `QuickAgentPtt` (`/mic`, `/ptt` 16kHz VAD)
- **`PreferredMicSelector`**:
  - Tự động ưu tiên thiết bị khớp `preferredMicSubstring` (mặc định `"FIFINE"` -> `Mic (FIFINE K669 Microphone)`) và loại trừ (denylist) các micro ảo (`"iVCam"`, `"DroidCam"`, `"Steam Streaming"`, `"Stereo Mix"`, `"Virtual Audio"`).
- **Lệnh `/mic` (`status` | `list` | `select`) & `/ptt test [giây]`**:
  - Thu PCM `16000 Hz, 16-bit, Mono`, tính `RMS dBFS` + cờ `VAD (Voice Activity Detected)`, và giải phóng sạch 100% bộ đệm ngay khi kết thúc phiên PTT (không tự mở mic khi bấm `Ctrl+Shift+Space`).

### 2.3. Hạng Mục `C1` (Quyết định 3.7 & 3.9a): Ghi Clipboard Chống Treo UI & Tinh Chỉnh Bước Đổi Chiều Cao `WAKE`
- **Chống treo `ClipboardService` (`WM_DESTROYCLIPBOARD` Safe Guard)**:
  - Thực hiện thao tác ghi Clipboard (`Alt+C`, `Alt+G`, `Alt+K`, `Alt+Enter`, lệnh `COPYWIDGET`) với cơ chế chống khóa chết luồng UI (qua worker STA ngắn hạn có timeout `<= 200 ms` hoặc `OpenClipboard` retry ngắn), đảm bảo nếu tiến trình giữ clipboard bên ngoài bị treo thì Cockpit vẫn phản hồi mượt mà.
- **Tối ưu bước đổi chiều cao cửa sổ (`40 <-> 330 px`) & Chuẩn hóa `[SLA-WARN]` hồi quy**:
  - Giảm thiểu số lần ép layout (`UpdateLayout`) không cần thiết khi đổi `Height` từ `40 -> 330 px`; áp dụng quy ước `[SLA-WARN]` (như `Task12 vision`) trong `Verify-Task15.ps1` / `Verify-Task16b.ps1` nếu chỉ có duy nhất 1 mẫu đơn lẻ bị trễ nhịp VSync DWM (`P50 <= 11.0 ms` với `P95 <= 16.0 ms`, hoặc `P95 <= 18.5 ms` khi `>= 9/10` mẫu `<= 12.0 ms`).

## 3. Cổng Nghiệm Thu (`Verify-Task17.ps1` — Chỉ Lấy Báo Cáo Đóng Gate Từ PC `bitmappp`)
1. **E1**: Build `-warnaserror` 0 lỗi/0 cảnh báo, 0-byte BOM toàn repo, `ADR-68` chuyển sang `Chốt v0.16.5` và `ADR-67` đăng ký trong `docs/ADR_REGISTRY.md`.
2. **E2**: Selftest `/net`, `/net port <ephemeral_port>` (`< 15 ms`), dòng `NITRO5-WORKER`, lật khung `Alt+Q` và MCP `get_network_radar`.
3. **E3**: Selftest `PreferredMicSelector` chọn đúng `FIFINE K669 Microphone` và bỏ qua `e2eSoft iVCam`.
4. **E4**: Selftest `QuickAgentPtt` 16kHz VAD (im lặng vs tín hiệu sóng âm) + kiểm chứng `ClipboardService` không treo luồng UI khi một tiến trình phụ đang giữ khóa Clipboard.
5. **E5**: Trên PC `bitmappp`, `Private WS` lắng `<= 45.0 MB`, `CPU rảnh 0%`, chuỗi hồi quy `-SkipLive` đạt `ALL PASS` với `zeroLiveCalls = True`.