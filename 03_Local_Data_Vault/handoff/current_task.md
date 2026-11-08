# TASK-12b: Tối Ưu Bộ Nhớ LOH Cho Vision (`Idle WS <= 45 MB`), Hợp Nhất Đo RAM `NexusVerify.Common.ps1` & Hoàn Tất Gate 12 (`ADR-61`)

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Kết Quả Chạy Thực Tế Gate 12)
- **Trạng thái Gate 12 (`claude/task-12-multimodal-vision` @ `07353e3`)**:
  - Đã hoàn thành toàn bộ tính năng `/img`, `/snap`, nén GDI+ `<= 250 KB` (`65,900 B`), `Combat-Mode Shield` (`0.183 ms`), `Privacy Window Guard` (quét mọi cửa sổ giao cắt vùng chụp), `Vision Cache` và 2 tinh chỉnh từ Gate 13.
  - Phép đo thực tế trên máy Nhật Hòa cho thấy: sau phiên xử lý ảnh 2K, `Private Working Set` lúc rảnh dừng ở `51.6 MB` (sát cổng `52.0 MB`, cao hơn mức `40.7 MB` của Gate 13 do `~92 MB` mảng byte cấp phát trên Large Object Heap trong lúc xử lý ảnh), và E3 Vision Cache P95 đạt `2.988 ms` (sát ngưỡng `3.0 ms` do nhiễu GC từ đợt cấp phát trước đó).
- **Phê chuẩn của Architect (5 Điểm Thiết kế `TASK-12` + 4 Điểm `TASK-12b`)**:
  1. **Duyệt 5 quyết định `TASK-12`**: (1) Dùng `GDI+ System.Drawing.Common` cho `net8.0`; (2) Win32 Clipboard + `RegisterClipboardFormat` cho cờ chặn lịch sử `Win+V`; (3) Ảnh `1280x720` cho phép thử đổi 1 pixel; (4) Privacy Guard kiểm tra mọi cửa sổ giao cắt vùng chụp; (5) Phạm vi Privacy Guard kiểm tra theo tiêu đề cửa sổ.
  2. **Mô hình Bộ nhớ Hai Tầng (`ADR-09` & `ADR-61` Dual-Envelope Memory Model)**:
     - **Ngưỡng Rảnh Nền (Settled/Idle Envelope)**: Mục tiêu `TASK-12b` kéo `Private WS` lúc rảnh sau phiên Vision về **`<= 45.0 MB`** (cổng chặn cứng Phase 3 `<= 52.0 MB`, trần tuyệt đối `ADR-09` `< 60.0 MB`).
     - **Ngưỡng Đỉnh Tức Thời (Transient Burst Envelope)**: Trong `<= 5 giây` xử lý nén ảnh lớn, cho phép đỉnh bộ nhớ tạm thời vọt lên nhưng được giám sát bằng mẫu đo `100 ms` với ngưỡng cảnh báo mềm `120.0 MB` và ngưỡng trần mềm `200.0 MB` (không làm cổng chặn cứng nếu sau nhịp dọn rảnh đã về `<= 45.0 MB`).

## 2. Mục Tiêu TASK-12b (Thực Hiện Trước Khi Merge `TASK-12` Vào `main`)
1. **Triệt tiêu các bản sao mảng byte trên LOH trong `VisionImageProcessor` & Clipboard**: Kéo `Private Working Set` lúc rảnh sau phiên Vision về **`<= 45.0 MB`** và giảm đỉnh tức thời từ `~145 MB` xuống vùng **`~65–95 MB`**.
2. **Hợp nhất hàm đo `Get-PrivateWsMb` vào `NexusVerify.Common.ps1`**: Chuẩn hóa cách lấy mẫu trung vị (median 8 mẫu sau khi dọn rảnh `>= 10 s`, ghi kèm `min/max`) và bộ lấy mẫu đỉnh `100 ms`.
3. **Chạy nghiệm thu lại `Verify-Task12.ps1`, `Verify-Task13a.ps1` và `Verify-Task13.ps1`** đạt `ALL CHECKS PASS` (với E3 P95 của Task 12 cải thiện rõ rệt dưới `3.0 ms`).

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Bốn Tối Ưu Bộ Nhớ GDI+ & Large Object Heap (LOH)
1. **Đọc Clipboard Zero-Copy qua `UnmanagedMemoryStream`**:
   - Khi đọc dữ liệu DIB / Bitmap từ Win32 Clipboard (`GetClipboardData` + `GlobalLock`), bọc trực tiếp con trỏ bộ nhớ unmanaged bằng `unsafe new UnmanagedMemoryStream((byte*)ptr, size)` để GDI+ đọc thẳng mà **không** chép ra mảng `byte[]` trung gian trên LOH.
2. **Loại bỏ `MemoryStream.ToArray()` khi xuất JPEG**:
   - Khi lưu ảnh JPEG vào `MemoryStream`, dùng `ms.TryGetBuffer(out ArraySegment<byte> segment)` (hoặc `Span<byte>` / `ArrayPool<byte>.Shared`) để tính SHA-256 (`SHA256.HashData(segment.AsSpan())`) và chuyển sang Base64 (`Convert.ToBase64String(segment.AsSpan())`) mà không cấp phát thêm mảng `byte[]` bản sao thứ hai.
3. **Giới hạn Cạnh Ảnh Nguồn Tối Đa (`maxSourceDimensionPx = 4096`)**:
   - Kiểm tra kích thước ảnh đầu vào trước khi cấp phát bề mặt vẽ: nếu chiều rộng hoặc chiều cao `> 4096 px` (thay vì `16384 px`), từ chối sớm với thông báo `[VISION ERROR] Source image dimensions exceed 4096px safety limit` để bảo vệ bộ nhớ.
4. **Thu hồi & Nén LOH Tức thì Sau Phiên Vision**:
   - Ngay sau khi hoàn tất xử lý `/img` hoặc `/snap` (trong khối `finally` sau khi đã `Dispose()` toàn bộ `Bitmap`, `Graphics`, `Stream`), đặt `GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce` (kết hợp thu hồi LOH nhẹ hoặc tích hợp vào nhịp dọn rảnh) để trả ngay các khối `> 85 KB` về cho hệ điều hành.
   *(Lưu ý: Chỉ cần giữ GDI+; không cần viết COM `IWICBitmapScaler` nếu 4 bước trên đã đưa mức rảnh về `<= 45.0 MB`).*

### 3.2. Hợp Nhất `Get-PrivateWsMb` Trong `NexusVerify.Common.ps1`
- Đưa hàm đo bộ nhớ chuẩn vào `01_Foundation_Engine/tools/NexusVerify.Common.ps1` và dùng chung cho `Verify-Task12.ps1`, `Verify-Task13a.ps1`, `Verify-Task13.ps1`:
  - **Chế độ Đo Rảnh Chuẩn (`Settled Median Mode`)**: Đảm bảo đã qua nhịp dọn rảnh `>= 10 s`, lấy `8` mẫu liên tiếp (cách nhau `~250 ms`), trả về giá trị **Trung vị (Median)** kèm `Min` và `Max` để loại bỏ hoàn toàn nhiễu dao động tức thời của Windows WMI/WorkingSet.
  - **Chế độ Đo Tức Thời (`Immediate Mode` cho E3 của `Verify-Task13a.ps1`)**: Đọc mẫu đơn ngay lập tức để đối chiếu trực tiếp với con số hiển thị trên nhãn HUD tại cùng thời điểm.
  - **Chế độ Đo Đỉnh Tức Thời (`Peak Sampler ~100ms` cho E2/E5 của `Verify-Task12.ps1`)**: Lấy mẫu mỗi `~100 ms` trong lúc thực thi lệnh `/img` với ảnh 2K để ghi nhận `PeakPrivateWsMb` (in rõ thông số đỉnh vào báo cáo; cảnh báo mềm nếu `> 200 MB`, không chặn cứng Gate nếu mức rảnh sau đó đạt `<= 45.0 MB`).

## 4. Verification Steps (Nghiệm Thu Gate 12b / Gate 12 Hoàn Chỉnh)
1. **E1 (Build `-warnaserror` & Repository-Wide Zero-BOM Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; toàn bộ file trong `git ls-files` đạt 0-byte BOM; CI GitHub xanh.
2. **E2 (Zero-LOH-Copy Vision Processing & Peak Telemetry)**:
   - Nén ảnh 2K (`2560x1440`) qua `/img` xuống `<= 250 KB`, ghi nhận `PeakPrivateWsMb` (lấy mẫu `100 ms`) giảm rõ rệt so với mức `145 MB` trước đó.
3. **E3 (Vision SHA-256 Cache Hit SLA `P50 <= 2.0 ms / P95 <= 3.0 ms` Without GC Jitter)**:
   - 25 lần gọi `/img` trúng cache đạt `P50 <= 2.0 ms` và `P95 <= 3.0 ms` với dư địa an toàn rộng hơn mức `2.988 ms` cũ nhờ không còn rác LOH.
4. **E4 (Combat Shield, Multi-Window Privacy Guard & MCP `analyze_visual_input`)**:
   - Đạt 100% các bài kiểm tra khiên bảo vệ và đồng bộ MCP -> CockpitUI.
5. **E5 (Post-Vision Settled `Private WS <= 45.0 MB` & Cross-Gate Verification Task12 / 13a / 13)**:
   - Sau phiên Vision và nhịp dọn rảnh (`>= 10 s`), trung vị 8 mẫu `Private Working Set` đạt **`<= 45.0 MB`** (vượt xa cổng `52.0 MB`).
   - Chạy lại cả 3 bộ `Verify-Task12.ps1`, `Verify-Task13a.ps1` và `Verify-Task13.ps1` với hàm đo chung trong `NexusVerify.Common.ps1` đều đạt **ALL CHECKS PASS**.