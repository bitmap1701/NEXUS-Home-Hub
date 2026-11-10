# TASK-16c: Cockpit UI v2 Visual Overhaul — Giao Diện 2 Cột, Chuỗi Thẻ Hội Thoại, Bảng Lệnh `/` & `@Agent`, Bảng Liên Minh & Chế Độ Lười Theo 6 Bảng Thiết Kế (`ADR-68` -> `v0.16.5`)

## 1. Context Summary & Lý Do Mở `TASK-16c` Trước `TASK-17` (`v0.16.0` @ `60322e2`)
- **Trạng thái hiện tại**:
  - `main` và tag `v0.16.0` đang ở commit `60322e2` (`ADR-62` đã chuyển sang `Chốt v0.16.0`, `ADR-67` đã ghi nhận cho `TASK-17`).
  - `TASK-17` tạm dừng khi chưa viết code theo yêu cầu của Nhật Hòa vì giao diện thực tế của Cockpit vẫn mang hình thức terminal 3 cột cũ (`LEFT MFD | CENTER | RIGHT MFD`), chưa phản ánh phần nhìn của **6 bảng thiết kế "NEXUS Cockpit UI v2"** đã duyệt ngày 08/11.
- **Mục tiêu cốt lõi của `TASK-16c` (`ADR-68` -> `v0.16.5`)**:
  - Thay thế hoàn toàn lớp giao diện 3 cột terminal cũ bằng **giao diện 2 cột hiện đại** theo đúng 6 bảng thiết kế đã duyệt, trong khi **giữ nguyên toàn bộ cơ chế hậu trường đã đạt chuẩn ở Gate 16 / 16b / 15** (`Private WS <= 45.0 MB`, `CPU rảnh 0%`, `WAKE 40 -> 330 px P50 <= 10 ms / P95 <= 16 ms`, cắt thật phần thân khi ở `40 px`, tương thích ngược 100% với `Verify-Task04..16b`).

## 2. Bốn Quyết Định Kiến Trúc Đã Chốt Từ Architect
1. **Quyết định (1) — Phông chữ (0 byte nhúng thêm)**:
   - Dùng **`Segoe UI Variable Display` / `Segoe UI Variable Text`** (fallback `Segoe UI`) cho chữ giao diện và **`Cascadia Mono`** (fallback `Consolas`) cho code/số liệu. Không nhúng file font `.ttf/.otf` vào binary để giữ `Private WS` ở mức tối thiểu.
2. **Quyết định (2) — Chỗ đặt 6 ô `Quick Switches`**:
   - Bỏ danh sách 6 checkbox khỏi cột phải để cột phải (`300 px`) hiển thị đúng 3 khối trong Bảng 1: `THẺ ĐÃ GHIM n/5`, dòng `⏱ Hẹn giờ`, và ô `LIÊN MINH` ở đáy.
   - Đưa 6 công tắc (`1. Game Boost`, `2. iGPU Lock`, `3. Mic VAD`, `4. 3-Tier Gate`, `5. Tự thu gọn 30s`, `6. Xuyên thấu chuột`) vào một khu vực gọn ở đáy bảng **`F1 Phím tắt`** (và thêm nút bật/tắt nhanh `Xuyên thấu` trên thanh đỉnh như Bảng 6).
   - **Bắt buộc giữ nguyên các trường trạng thái trong `UISTATE` và lệnh pipe** mà `Verify-Task04..16b` đang đọc.
3. **Quyết định (3) — Nút `Hủy Esc` trên thẻ đang chạy**:
   - Triển khai ngay trong `TASK-16c`: gắn `CancellationTokenSource` cho yêu cầu đang chạy. Khi người dùng nhấn `Esc` (lúc đang có thẻ chạy) hoặc bấm nút `Hủy Esc` trên thẻ, hủy yêu cầu ngay lập tức, chuyển trạng thái thẻ sang `[ĐÃ HỦY]` và bỏ qua phản hồi đến muộn.
4. **Quyết định (4) — Tách `Ctrl+Shift+Space` khỏi tự động mở Mic**:
   - Phím `Ctrl+Shift+Space` chỉ đánh thức cửa sổ và đưa con trỏ vào ô `NEXUS›`, không tự động kích hoạt luồng thu âm micro (nếu script kiểm thử cũ kiểm tra cờ boolean trong `UISTATE` thì chỉ giữ tương thích cờ dữ liệu, tuyệt đối không mở thiết bị âm thanh khi `WAKE`).

## 3. Đặc Tả Chi Tiết 16 Hạng Mục Giao Diện Theo 6 Bảng Thiết Kế

### 3.1. Bảng Màu Chuẩn & Hệ Thống Chữ (Mục 16 + Quyết Định 1)
- **Màu sắc chủ đạo (Tất cả dùng `SolidColorBrush` đã `Freeze()` một lần duy nhất)**:
  - Nền cửa sổ chính: **`#0B1220`** (Nền thẻ / khối nổi: `#0F172A` đến `#131C31`).
  - Viền thẻ & khung chia: **`#1E2A40`** (Viền thẻ đang chạy / ô lệnh đang focus: **`#38BDF8`**).
  - Màu nhấn chính (Primary Accent): **`#38BDF8`** / **`#7DD3FC`**.
  - Chữ chính: **`#E2E8F0`** · Chữ phụ: **`#CBD5E1`** · Chữ mờ / nhãn phụ: **`#94A3B8`**.
  - Trạng thái Xanh lá (`PASS` / hoàn tất): **`#34D399`** · Vàng / Hổ phách (`inline code` / cảnh báo): **`#FDE68A`** · Đỏ (`Hủy Esc` / lỗi): **`#F87171`**.

### 3.2. Thanh Đỉnh `44 px` & Chế Độ Thu Gọn `40 px` (Mục 4, 6 & Bảng 1, 6)
- **Ở chế độ Chuẩn (`330 px`) và Cao (`640 px`) — Thanh trên cao `44 px`**:
  - **Cụm trái**: Chấm tròn trạng thái (`#38BDF8` hoặc `#34D399`) + tiêu đề đĩnh đạc **`NEXUS COCKPIT`** + huy hiệu chế độ **`COMMAND`** (nền xanh `#0284C7`/`#38BDF8`) hoặc **`PASSIVE`** (hoặc huy hiệu `• Claude Code xong ca...` khi có thông báo ca).
  - **Cụm phải**:
    1. Chip **`RAM <PrivateWS> / 60 MB`** (ví dụ `RAM 41,4 / 60 MB`).
    2. Chip **`CPU 0%`**.
    3. Chip **`WAL · Micro`** (chế độ vận hành hiện tại).
    4. Cụm nút chọn độ cao 3 nấc: **`Gọn` | `Chuẩn` | `Cao`** (tương ứng `40 / 330 / 640 px`, nút đang chọn tô nổi `#38BDF8`) + nút **`Xuyên thấu`**.
    5. Nút **`F1 Phím tắt`**.
- **Ở chế độ Thu gọn (`40 px`) — Chỉ còn đúng 1 dòng gọn (Mục 6 & Bảng 6)**:
  - Cắt thật phần thân cửa sổ (không dùng `Visibility.Collapsed` giữ layout nặng, bảo toàn cơ chế cắt `40 px` của Gate 16b).
  - Hiển thị một dòng duy nhất: **`● NEXUS · RAM <X> MB · <Trạng thái Gemini / Liên minh>`** (khi có câu trả lời mới đến ở chế độ thu gọn thì đổi màu nhấn tĩnh trên nhãn, không tự bung cửa sổ và không chạy animation lặp).

### 3.3. Bố Cục 2 Cột & Chuỗi Thẻ Hội Thoại Có Tái Sử Dụng Phần Tử (Mục 1, 3, 7, 9, 10 & Bảng 1)
- **Xóa bỏ cột `LEFT MFD` cũ**: Chia thân cửa sổ thành **2 cột**:
  1. **Cột Hội thoại bên trái (`Width = *`)**: Chứa chuỗi thẻ hỏi–đáp phía trên và ô nhập lệnh cố định ở đáy.
  2. **Cột Phải cố định (`Width = 300 px`)**: Chứa `THẺ ĐÃ GHIM n/5`, dòng `⏱ Hẹn giờ`, và thẻ `LIÊN MINH` ở đáy.
- **Giới hạn & Tái sử dụng phần tử (Object Pooling `<= 20` thẻ)**:
  - Chuỗi lịch sử trong cột hội thoại giữ tối đa **20 mục gần nhất** (ẩn toàn bộ bản ghi `isTest = true`) và tái sử dụng các phần tử UI (`Card Pool`) để `Private WS` không tăng theo thời gian sử dụng.
- **Ba loại mục trong Cột Hội thoại (Bảng 1)**:
  1. **Dòng lệnh cục bộ 1 dòng gọn (Mục 9)**:
     - Ví dụ `/calc sqrt(144) + 2^8 = 268`, `/timer`, `/note`... hiển thị trên **1 dòng mảnh** kèm giờ (`03:02`), kết quả màu xanh lá `#34D399` và huy hiệu **`cục bộ · 0 token`**.
  2. **Thẻ Câu hỏi đã hoàn tất (Mục 7)**:
     - Dòng đầu: Giờ (`03:03`) + **Câu hỏi in đậm (`#E2E8F0`)**.
     - Dòng siêu dữ liệu: `<Squad> · <Agent> · <Model> · QA PASSED` (nhãn `QA PASSED` màu `#34D399`).
     - Phần thân: Đoạn trả lời hiển thị Markdown nhẹ (tô nổi các cụm `inline code` bằng màu vàng `#FDE68A` trên nền tối `#1E293B`).
     - Hàng nút thao tác ở đáy thẻ: **`Copy Alt+C`** · **`Ghim Alt+P`** · **`Hỏi tiếp`** (điền `/f ` vào ô lệnh) · **`Chạy lại`** (gửi `/retry`).
  3. **Thẻ Câu hỏi đang chạy (Mục 10)**:
     - Viền sáng `#38BDF8`, góc phải hiện đồng hồ giây (cập nhật nhẹ khi có sự kiện chuyển bước hoặc nhịp 250–500 ms chỉ khi đang có yêu cầu bay, tắt hoàn toàn về `0% CPU` ngay khi xong) và nút viền đỏ **`Hủy Esc`**.
     - Thanh tiến trình 4 bước ngang: **`✓ Điều phối <X> ms` ─── `✓ Nén ảnh <Y> KB` (chỉ hiện bước này nếu là `/img` hoặc `/snap`) ─── `● Gemini đang trả lời` ─── `○ QA`**.

### 3.4. Ô Lệnh `NEXUS›` & Hai Bảng Gợi Ý Tự Động `/` và `@Agent` (Mục 11, 12 & Bảng 1, 2)
- **Ô lệnh ở đáy cột trái (Mục 11)**:
  - Nhãn phía trên: `Lệnh hoặc câu hỏi`.
  - Khung nhập bo góc viền `#38BDF8`, bên trái có tiền tố **`NEXUS›`** màu `#38BDF8`, chữ gợi ý mờ `Gõ câu hỏi · / xem lệnh · @ chọn agent`, bên phải hiện nhắc `Enter gửi · Esc về game`.
- **Popup A — Khi gõ `/` ở đầu ô lệnh (Bảng 2A)**:
  - Hiện khung gợi ý nổi ngay phía trên ô lệnh với tiêu đề `GÕ "/" — DANH SÁCH LỆNH, LỌC THEO CHỮ, TAB ĐỂ CHỌN`.
  - Chia 2 nhóm: **`VỪA DÙNG`** (tối đa 2–3 lệnh gần nhất) và **`TẤT CẢ`** (`/img`, `/calc`, `/ask`, `/g`, `/snap`, `/pin`, `/ctx`, `/timer`, `/note`, `/copy`, `/unpin`, `/qa`, `/retry`, `/mem`, `/status`, `/boost`, `/ui`), mỗi dòng gồm 3 cột: **Tên lệnh** (`#38BDF8`), **Mô tả tiếng Việt**, và **Ví dụ mẫu**.
  - Gõ thêm chữ sau `/` sẽ lọc danh sách theo thời gian thực; phím `↑↓` di chuyển dòng chọn, `Tab` (hoặc `Enter` khi chưa có tham số) điền lệnh vào ô, `Esc` đóng bảng.
- **Popup B — Khi gõ `@` trong ô lệnh (Bảng 2B)**:
  - Hiện khung `CHỌN 1 TRONG 283 AGENT`, lọc trực tiếp từ danh sách 283 agent trong bộ nhớ theo từ khóa sau `@` (tên agent + phân khu).
  - Khi bấm `Tab` / `Enter` chọn, gắn nhãn pill **`@<Tên Agent>`** vào đầu ô lệnh (hoặc chèn tiền tố `@<AgentId> `) để gửi câu hỏi kèm chỉ định agent đó.

### 3.5. Cột Phải `300 px` & Bảng Điều Phối Liên Minh `Alt+A` (Mục 13 & Bảng 1, 3)
- **Cột phải `300 px` (Mục 13 & Bảng 1)**:
  1. Tiêu đề **`THẺ ĐÃ GHIM`** kèm bộ đếm **`n/5`** bên phải: hiển thị danh sách thẻ ghim dạng thẻ bo góc (`#1 code · csharp`, `#2 note`, `#3 checklist · 1/3`) với tối đa **2 dòng xem trước** bằng font monospace.
  2. Dòng **`⏱ Hẹn giờ`**: hiển thị bộ đếm `/timer` đang chạy (hoặc ẩn/ghi `Không có hẹn giờ` khi rảnh).
  3. Khối **`LIÊN MINH`** neo ở đáy cột phải: hiển thị tên Task & trạng thái hiện tại (ví dụ `TASK-16c · đang triển khai`), dòng `Việc kế tiếp: ...`, và nút lớn màu xanh da trời **`Mở bảng điều phối · Alt+A`**.
- **Bảng Điều Phối Liên Minh khi nhấn `Alt+A` (Bảng 3)**:
  - Nút `← Cockpit` để quay lại + tiêu đề `LIÊN MINH // ĐIỀU PHỐI` (`Đọc từ current_task.md và gate<N>_handoff.md · chỉ đọc file, 0 token`).
  - Khung trái: Tên nhánh + tiêu đề `Gate <N> · TASK-<ID>` + huy hiệu `ALL PASS` / `CHƯA ALL PASS`; dãy **5 ô cổng `E1`..`E5`** (xanh lá khi đạt, đỏ khi trượt); dòng `VIỆC KẾ TIẾP`; 2 thẻ song song **`Lời nhắn cho Gemini`** (nút `Copy lời nhắn · Alt+G`) và **`Prompt cho Claude Code`** (nút `Copy prompt · Alt+K`); dòng lịch sử `ĐÃ ĐÓNG` ở đáy.
  - Khung phải: Lưới 4 ô **`THÀNH VIÊN`** (`Gemini Web`, `Claude`, `Claude Code`, `Antigravity`) và danh sách **`BẢNG TIN · CLAUDE CODE GỬI QUA MCP`**.

### 3.6. Bảng Phím Tắt `F1`, Chế Độ Lười & Thanh Kéo Đáy (Mục 14, 15 & Bảng 4, 5, 6)
- **Bảng Phím Tắt `F1` (Bảng 4 + Quyết định 2)**:
  - Mở/đóng bằng phím `F1` (khi Cockpit có focus) hoặc bấm nút `F1 Phím tắt` trên thanh đỉnh.
  - Bố cục 2 cột `CHUYỂN CHẾ ĐỘ` và `LÀM VIỆC VỚI CÂU TRẢ LỜI` (có nhãn vàng `MỚI` cho `Alt+A`, `Alt+G`, `Alt+K`), mục `TRONG Ô LỆNH` (`/`, `@`, `↑`), và hàng **6 công tắc `Quick Switches`** ở dưới cùng.
- **Chế độ Lười (Mục 14 & Bảng 5)**:
  - Khi rảnh / mới mở hoặc khi bật chế độ Lười: hiển thị thẻ lớn **`VIỆC TIẾP THEO CỦA ANH`** với nút lớn **`Alt+Enter`** (`Copy, anh chỉ cần Ctrl+V`) và sơ đồ 4 bước **`✓ Claude viết code ─── ✓ Claude Code chạy Verify ─── ● Gemini duyệt ─── ○ Merge + tag`**.
  - Khi phát hiện Clipboard đang chứa ảnh (kiểm tra lúc đánh thức cửa sổ, `0% CPU` nền): hiện thẻ gợi ý **`CLIPBOARD VỪA CÓ ẢNH`** (`Enter: Lỗi gì đây, sửa sao?`, `2: Tóm tắt nội dung ảnh`, `3: Dịch chữ trong ảnh sang tiếng Việt`).
  - Sau mỗi câu trả lời: hiện các nút bấm số **`1..4`** (`1 Copy code #1`, `2 Ghim...`, `3 Nói ngắn hơn`, `4 Viết test...`) có thể bấm chuột hoặc gõ phím số khi ô lệnh trống.
- **Thanh kéo ở đáy cửa sổ (Mục 15 & Bảng 6)**:
  - Vạch nắm kéo màu `#38BDF8` ở giữa kèm dòng chữ hướng dẫn: **`Kéo xuống mở rộng · kéo lên thu gọn · nhấp đúp: Thu gọn ↔ Nửa`**.

## 4. Ràng Buộc Nghiêm Ngặt & 5 Cổng Nghiệm Thu (`Verify-Task16c.ps1`)
- **Ràng buộc Cứng**:
  - `Private Working Set` lúc rảnh **`<= 45.0 MB`** (cổng chặn cứng `<= 52.0 MB`), **CPU lúc rảnh `0%`**, không dùng Storyboard/Animation lặp, không tạo timer nền chạy liên tục.
  - `WAKE` từ `40 px -> 330 px` giữ nguyên SLA **`P50 <= 10.0 ms`** và **`P95 <= 16.0 ms`** (nhờ cắt thật phần thân ở `40 px` và tái sử dụng tối đa 20 thẻ trong Card Pool).
  - Tương thích ngược 100% với các lệnh pipe và trường `UISTATE` mà chuỗi `Verify-Task04..16b` đang sử dụng; `zeroLiveCalls = True` (`0` lượt gọi Gemini thật); **0-byte BOM** trên toàn repo.
- **5 Cổng Nghiệm Thu (`E1` – `E5`) trong `Verify-Task16c.ps1` + `--selftest ui-v2-visual`**:
  1. **E1 (Build `-warnaserror`, Zero-BOM & ADR-68 Registry)**: Build 0 lỗi, 0 cảnh báo; 0-byte BOM toàn repo; `ADR-68` được đăng ký trong `docs/ADR_REGISTRY.md`.
  2. **E2 (2-Column Layout, Top Bar `44 px`, Collapsed `40 px` Single-Line & Card Pool `<= 20`)**: Kiểm chứng bố cục 2 cột (cột phải `300 px`), thanh trên `44 px`, chế độ `40 px` chỉ hiện 1 dòng và cắt phần thân, danh sách thẻ hội thoại giới hạn `<= 20` phần tử tái sử dụng, dòng lệnh cục bộ có nhãn `cục bộ · 0 token`, và nút `Hủy Esc` hủy được yêu cầu đang chạy.
  3. **E3 (Slash `/` Command Popup, `@Agent` 283 Filter & Lazy Mode / Alliance Board `Alt+A`)**: Kiểm chứng gõ `/` hiện bảng lệnh (`Vừa dùng` + `Tất cả`, lọc theo chữ, `Tab` điền), gõ `@sec` lọc đúng các agent bảo mật từ registry 283 agent, thẻ `VIỆC TIẾP THEO` (`Alt+Enter`), gợi ý ảnh clipboard và bảng `Alt+A` (`Alt+G` / `Alt+K`).
  4. **E4 (`WAKE` Latency `40 -> 330 px` SLA `P50 <= 10 ms` / `P95 <= 16 ms`)**: Đo 10 chu kỳ đánh thức từ `40 px` lên `330 px` trên máy Nhật Hòa (tách `ColdFirstWake` chỉ báo cáo như Gate 15).
  5. **E5 (Settled Idle `Private WS <= 45.0 MB`, `0%` Idle CPU & Full Regression `-SkipLive` Zero Live Calls)**: Đo RAM rảnh trên máy Nhật Hòa đạt `<= 45.0 MB`, CPU `0%`, và chạy toàn bộ chuỗi hồi quy `Task16b -> 16 -> 15 -> 13 -> 12 -> 13a -> Task14..03` với `-SkipLive` đạt `ALL PASS` và `zeroLiveCalls = True`.