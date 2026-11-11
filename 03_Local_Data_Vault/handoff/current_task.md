# TASK-17a: Cấu Hình `networkTargets` Tùy Chọn Trong `nexus_config.json` (Hỗ Trợ `/reload`) & Tối Ưu Thu Hồi Bộ Nhớ Lúc Rảnh (`Idle Trim` Kéo `Task16b` Về `~42 MB`) (`ADR-69` -> `v0.17.1`)

## 1. Context Summary & Trạng Thái Xuất Phát (`v0.17.0` @ `746a3fe` + `claude/task-17-close-v0170`)
- **Gate 17 (`TASK-17` / `ADR-67`) ĐÃ CHỐT (`v0.17.0` @ `746a3fe`)**:
  - Hồ sơ `ADR-67` đã chuyển sang **`Chốt v0.17.0`**, `ROADMAP_FUTURE.md` và `qa_report.md §25.8` đã cập nhật 5 quyết định `17.1–17.5`.
  - Số ADR trống kế tiếp trong `docs/ADR_REGISTRY.md`: **`ADR-69`**.
- **Mục tiêu của `TASK-17a` (`ADR-69` -> `v0.17.1`)**:
  - Giải quyết gọn 2 hạng mục kỹ thuật mở từ Gate 17 trong một task nhỏ trước khi sang tính năng mới:
    1. **Hạng mục `N1a` (Quyết định 17.4)**: Chuyển danh sách đích LAN của `/net` sang đọc cấu hình tùy chọn `networkTargets` trong `nexus_config.json` (hỗ trợ nạp nóng qua `/reload`, tự lùi về 4 đích mặc định nếu thiếu hoặc lỗi).
    2. **Hạng mục `M1a` (Quyết định 17.5)**: Đo đạc trước bằng `--selftest ui-footprint` rồi bổ sung bước thu hồi gọn nhẹ khi dọn `[MEM]` lúc rảnh (xả bản chụp `/net` cũ, co pool thẻ / giải phóng khối văn bản lịch sử) để kéo `Private WS` lúc rảnh của `Task16b` từ `44.9 MB` về vùng đệm an toàn **`~42.0 MB`** (`<= 43.5 MB` trên `Task16b`, `<= 45.0 MB` toàn chuỗi).

## 2. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 2.1. Hạng Mục `N1a`: Cấu Hình `networkTargets` Trong `nexus_config.json` & Nạp Nóng `/reload`
- **Cấu trúc JSON trong `nexus_config.json` (Tùy chọn / Optional)**:
  - Cho phép khai báo mảng `"networkTargets"` gồm các đối tượng `{ "label": "<tên_hiển_thị>", "host": "<ip_hoặc_hostname>" }`.
  - **Quy tắc Fallback & Ràng buộc an toàn**:
    1. Nếu `networkTargets` không được khai báo trong `nexus_config.json`, mảng rỗng, hoặc JSON bị lỗi cú pháp: tự động dùng **4 đích mặc định**:
       - `Gateway` -> `192.168.1.1`
       - `NITRO5-LAN` -> `192.168.1.22`
       - `NITRO5-WIFI` -> `192.168.1.192`
       - `LAN-TV` -> `192.168.1.97`
    2. Nếu có phần tử con trong mảng bị thiếu `label` hoặc `host` (rỗng / khoảng trắng), tự động bỏ qua phần tử hỏng đó; nếu sau khi lọc mà danh sách trống thì lùi về 4 đích mặc định.
    3. Giới hạn tối đa **8 đích** (nếu khai báo nhiều hơn 8 thì chỉ lấy 8 đích đầu tiên) để đảm bảo tổng thời gian ping song song luôn giữ vững SLA `<= 200 ms`.
- **Tích hợp với `/reload`, `/net`, `Alt+Q` và MCP `get_network_radar`**:
  - Khi chạy lệnh `/reload` trên Cockpit, nạp lại danh sách `networkTargets` từ `nexus_config.json` ngay lập tức mà không cần khởi động lại `HostService`.
  - Dòng trạng thái tĩnh `⚡ NITRO5` ở cột phải `300 px` ưu tiên lấy kết quả từ mục có nhãn bắt đầu bằng `"NITRO5"` (hoặc IP `.22` / `.192`) trong danh sách đích.

### 2.2. Hạng Mục `M1a`: Đo `ui-footprint` & Thu Hồi Bộ Nhớ Lúc Rảnh (`[MEM]` Idle Trim)
- **Bước 1 — Đo đạc bằng `--selftest ui-footprint` (Measure-First)**:
  - Chạy `--selftest ui-footprint` để đối chiếu dung lượng heap / visual tree trước và sau khi nạp lịch sử thẻ và bản chụp `/net`.
- **Bước 2 — Thu gọn khi dọn bộ nhớ lúc rảnh (`[MEM]` Idle Trim)**:
  - Dựa trên số đo từ `ui-footprint`, chọn phương án tối giản và hiệu quả nhất trong chu trình dọn bộ nhớ lúc rảnh (`[MEM]`):
    - Xả chuỗi / bộ đệm bản chụp `/net` cũ khi không đang mở khung `MẠNG // /NET` (`Alt+Q`).
    - Co số lượng thẻ giữ sẵn trong `Card Pool` lúc rảnh (ví dụ từ `8` xuống `4` thẻ hoặc xóa trắng các `TextBlock.Inlines` / cây con của các thẻ ẩn ngoài vùng nhìn) để giải phóng các đối tượng WPF `FormattedText` / `GlyphRun` nặng.
  - **Cam kết hiệu năng**:
    - Không làm chậm đường đánh thức `WAKE 40 -> 330 px` (giữ nguyên `P50 <= 10.0 ms`, `P95 <= 16.0 ms`).
    - Kéo `Private WS` lúc rảnh của `Verify-Task16b` từ `44.9 MB` xuống **`<= 43.5 MB`** (mục tiêu quanh **`~42.0 MB`**).

## 3. Ràng Buộc & 5 Cổng Nghiệm Thu (`Verify-Task17a.ps1` — Chạy Trên PC `bitmappp`)
- **Ràng buộc Cứng**:
  - `Private WS` lúc rảnh **`<= 45.0 MB`** trên mọi Gate (riêng `Task17a` và `Task16b` mục tiêu **`<= 43.5 MB`**), **CPU rảnh `0%`**, **`zeroLiveCalls = True`** (`0` lượt gọi Gemini thật), **0-byte BOM** toàn repo.
  - Báo cáo đóng Gate chỉ lấy từ PC chính `bitmappp`.
- **5 Cổng Nghiệm Thu (`E1` – `E5`) trong `Verify-Task17a.ps1`**:
  1. **E1 (Build `-warnaserror`, Zero-BOM & ADR-69 Registry)**: Build sạch `0` lỗi / `0` cảnh báo; 0-byte BOM toàn repo; `ADR-67` đã ở trạng thái `Chốt v0.17.0` và `ADR-69` có mặt trong `docs/ADR_REGISTRY.md`.
  2. **E2 (`networkTargets` Custom Config & `/reload` Hot-Reload)**: Ghi tạm cấu hình `networkTargets` tùy chỉnh (ví dụ `{"label": "LOOPBACK-TEST", "host": "127.0.0.1"}`), gọi `/reload` + `/net` và MCP `get_network_radar`, xác nhận đích `LOOPBACK-TEST` xuất hiện và phản hồi `ONLINE` mà không cần khởi động lại `HostService`.
  3. **E3 (`networkTargets` Missing / Malformed Fallback Safety)**: Thử nghiệm với cấu hình thiếu trường `networkTargets`, mảng rỗng `[]`, và phần tử hỏng `{"label": "", "host": null}` -> gọi `/reload` + `/net` xác nhận hệ thống tự lùi về 4 đích mặc định (`Gateway .1`, `NITRO5 .22/.192`, `TV .97`) an toàn, `0` lỗi.
  4. **E4 (`ui-footprint` Diagnostics & `[MEM]` Idle Trim Verification)**: Kiểm chứng `--selftest ui-footprint` và xác nhận sau khi bơm tải lịch sử + chạy `/net` rồi kích hoạt dọn lúc rảnh (`[MEM]`), bộ đệm bản chụp `/net` / thẻ ẩn được thu hồi đúng thiết kế và `WAKE 40 -> 330 px` giữ vững `P50 <= 10.0 ms` / `P95 <= 16.0 ms`.
  5. **E5 (Settled Idle `Private WS` & Full Regression `Verify-Task17 -> ... -> 03` `-SkipLive`)**: Trên PC `bitmappp`, `Private WS` lúc rảnh của `Task17a` và `Task16b` đạt **`<= 43.5 MB`** (mục tiêu `~42 MB`), mọi Gate khác `<= 45.0 MB`, `CPU rảnh 0%`, toàn bộ chuỗi hồi quy từ `Verify-Task17` trở xuống đạt `ALL PASS` với `zeroLiveCalls = True`.