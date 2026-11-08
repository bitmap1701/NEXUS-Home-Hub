# TASK-12: Multimodal Vision Engine (`/img` & `/snap` — ADR-61), Nén WIC `<= 250 KB`, Vision SHA-256 Cache, Khiên Anti-Cheat & Bảo Vệ Quyền Riêng Tư

## 1. Context Summary & Quyết Định Kiến Trúc (Từ Gate 13 — Mốc `v0.13.0` @ `e62ddb7`)
- **Nghiệm thu Gate 13 (`v0.13.0`)**: `PASSED` 100% ngay ở lượt chạy 1 và đã fast-forward lên `main` kèm tag `v0.13.0` (Build 0/0, 96 tệp 0-byte BOM; bóc tách thẻ 4 KB trong `0.015 ms`; 30x `COPYWIDGET` đạt P50 `0.302 ms` / P95 `0.433 ms`; Right MFD FIFO giữ đúng 5 thẻ; MCP đồng bộ lên Cockpit trong `131.9 ms`; `Private Working Set` đạt `40.7 MB`, CPU 0%, `-FastRegression` 55.2 s với `Count=0` cảnh báo).
- **2 Tinh chỉnh Nhỏ Từ Gate 13 Gộp Vào `TASK-12`**:
  1. **Bỏ dòng xem trước trùng tiêu đề trên Right MFD**: Khi dòng đầu của `previewText` trên thẻ code trùng với `title`, lược bỏ dòng trùng đó để ô thẻ hiển thị tiếp ngay các dòng nội dung phía dưới.
  2. **Không lưu vào lịch sử Clipboard `Win+V` (`ExcludeClipboardContentFromMonitorProcessing`)**: Khi ghi Clipboard trong `ClipboardService`, đặt thêm định dạng `"ExcludeClipboardContentFromMonitorProcessing"` (và `"CanIncludeInClipboardHistory"` = 0) trên `IDataObject` để Windows không lưu các đoạn mã kiểm thử hoặc nội dung nhạy cảm vào lịch sử `Win+V` / Cloud Clipboard.
- **Vị trí trong Lộ trình Phase 3 (`ROADMAP_FUTURE.md` & `docs/ADR_REGISTRY.md`)**:
  `Gate 11b (v0.11.1)` -> `TASK-14 (v0.12.0)` -> `TASK-14b (v0.12.1)` -> `TASK-13a (v0.12.2)` -> `TASK-13 (v0.13.0)` -> **`TASK-12` (`/img`, `/snap` — ADR-61)** -> `TASK-15` (`/ctx`).

## 2. Mục Tiêu TASK-12 (ADR-61 — Multimodal Vision Engine)
Đưa khả năng thị giác máy tính (Multimodal Vision) vào `CockpitUI` và `NEXUSHome.McpTools` với độ an toàn tuyệt đối trước Kernel Anti-Cheat (`ACE-Guard` / `BattlEye`) và bảo vệ quyền riêng tư:
1. **Lệnh `/img [câu_hỏi]`**: Đọc ảnh từ Windows Clipboard (sau khi người dùng bấm `Win+Shift+S` hoặc copy ảnh lỗi code/sơ đồ mạng) hoặc từ đường dẫn file ảnh cục bộ, nén xuống `<= 250 KB` và gửi kèm câu hỏi lên Gemini Multimodal (`inline_data`).
2. **Lệnh `/snap [câu_hỏi]`**: Chụp nhanh màn hình phụ **MSI Optix G272** (hoặc vùng màn hình làm việc) kèm **2 lớp khiên bảo vệ cứng**:
   - **Khiên Anti-Cheat (`Combat-Mode Shield`)**: Tự động chặn `/snap` ngay lập tức nếu phát hiện bất kỳ tiến trình nào trong `combatProcessPrefixes` đang chạy (tránh gọi GDI `BitBlt`/`CopyFromScreen` khi đang chạy *Delta Force* / *PUBG*).
   - **Khiên Quyền Riêng Tư (`Sensitive Window Denylist`)**: Tự động chặn `/snap` nếu tiêu đề cửa sổ đang mở khớp với danh sách từ khóa nhạy cảm (`privacyRedactedWindowKeywords`: mật khẩu, ngân hàng, `.env`, khóa API, trình duyệt ẩn danh).
3. **Bộ nhớ đệm Thị giác (`Vision SHA-256 Cache`)**: Băm `SHA-256(CompressedImageBytes + Prompt + Agent + Model)` lưu trong SQLite WAL; khi hỏi lại cùng bức ảnh và câu hỏi, trả kết quả tức thì trong **`P50 <= 2.0 ms / P95 <= 3.0 ms`** với **0 token**.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Tinh Chỉnh Gate 13: Right MFD Preview & `Win+V` Clipboard Hygiene
- Trong bộ tạo thẻ / hiển thị Right MFD (`WidgetCardExtractor` / `MfdPinManager` / `CockpitUI`): nếu `previewText` bắt đầu bằng đúng chuỗi `title` (hoặc dòng đầu trùng nhau), cắt bỏ dòng trùng để phần xem trước hiển thị từ dòng thứ hai trở đi.
- Trong `ClipboardService`: khi đặt văn bản lên Windows Clipboard, đóng gói qua `DataObject`, gọi `SetText(...)` và thêm `SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1 }))` cùng `SetData("CanIncludeInClipboardHistory", 0)` để ngăn làm bẩn lịch sử `Win+V`.

### 3.2. Bộ Nén Ảnh WIC / WPF Siêu Nhẹ `<= 250 KB` (`VisionImageProcessor.cs`)
- **Không cài thêm thư viện xử lý ảnh bên thứ ba nặng nề**: Sử dụng trực tiếp hạ tầng WIC có sẵn trong `.NET 8 Windows / WPF` (`System.Windows.Media.Imaging.BitmapSource`, `TransformedBitmap`, `JpegBitmapEncoder`) hoặc `System.Drawing.Common` sẵn có.
- **Thuật toán thu phóng & nén (`CompressToJpegUnderBudget`)**:
  - Giới hạn cạnh dài nhất (`maxDimensionPx = 1280` trong `nexus_config.json`, giữ nguyên tỷ lệ khung hình).
  - Mã hóa sang định dạng `image/jpeg` ở mức chất lượng khởi điểm `qualityLevel = 78`. Nếu mảng byte đầu ra vẫn `> 250 * 1024` (250 KB), giảm bậc chất lượng (`60`, `45`) hoặc thu nhỏ thêm kích thước một bước để đảm bảo **luôn `<= 250 KB`** (`maxVisionImageBytes = 256000`).
  - Tính mã băm `imageSha256Hex` (64 ký tự hex chữ thường) ngay trên mảng byte đã nén.
  - Giải phóng ngay lập tức các đối tượng Bitmap / MemoryStream trung gian để không giữ ảnh thô trong RAM (bảo vệ cổng `Private WS <= 52.0 MB`).

### 3.3. Khiên Chống Anti-Cheat & Khiên Quyền Riêng Tư Cho `/snap` (`VisionSafetyGuard.cs`)
- Mở rộng `nexus_config.json` (tương thích ngược):
  - `"maxVisionImageBytes": 256000`
  - `"maxVisionDimensionPx": 1280`
  - `"privacyRedactedWindowKeywords": ["Password", "1Password", "Bitwarden", "KeePass", "Bank", "Vietcombank", "Techcombank", "TPBank", "MoMo", "Binance", ".env", "gemini_key", "AI Studio", "Incognito", "InPrivate"]`
- **Quy tắc Kiểm tra Bắt buộc Trước Khi Chụp Màn Hình (`/snap`)**:
  1. **Kiểm tra Chế độ Chiến đấu (`Combat-Mode Shield`)**:
     - Nếu đang ở chế độ `LiveCombat` (phát hiện tiến trình trong `combatProcessPrefixes` đang chạy, hoặc khi biến môi trường kiểm thử `NEXUS_SIMULATE_COMBAT_MODE=1` được bật):
     - **CHẶN NGAY LẬP TỨC** thao tác chụp màn hình `/snap`, không gọi bất kỳ hàm chụp GDI/DXGI nào, trả về thông báo:
       `"[COMBAT SHIELD] /snap screen capture is disabled while combat/anti-cheat processes are active. Use /img with clipboard image instead."`
  2. **Kiểm tra Cửa sổ Nhạy cảm (`Privacy Window Guard`)**:
     - Đọc tiêu đề cửa sổ đang active (`GetForegroundWindow` -> `GetWindowText`) và các cửa sổ hiển thị chính (hoặc chuỗi tiêu đề giả lập qua biến môi trường kiểm thử `NEXUS_SIMULATE_WINDOW_TITLE` để `Verify-Task12.ps1` kiểm thử tất định).
     - Nếu tiêu đề chứa bất kỳ từ khóa nào trong `privacyRedactedWindowKeywords` (không phân biệt hoa thường):
     - **CHẶN NGAY LẬP TỨC** thao tác `/snap`, không chụp ảnh, trả về thông báo:
       `"[PRIVACY GUARD] /snap blocked because a sensitive window title was detected (<keyword>)."`
  3. **Khi an toàn (Ngoài Combat & Không vướng cửa sổ nhạy cảm)**:
     - `/snap [câu_hỏi]` ưu tiên chụp vùng màn hình phụ **MSI Optix G272** (hoặc màn hình làm việc hiện hành đã trừ khung cửa sổ CockpitUI) rồi đưa qua `VisionImageProcessor.CompressToJpegUnderBudget`.

### 3.4. Luồng `/img`, Bộ Nhớ Đệm `Vision Cache` & Nối `GeminiAgentClient` (`inline_data`)
1. **Lệnh `/img [câu_hỏi]`**:
   - Hỗ trợ đọc ảnh trực tiếp từ Windows Clipboard (`Clipboard.ContainsImage()`) HOẶC nhận đường dẫn ảnh kiểm thử cục bộ qua cú pháp `/img --file <đường_dẫn_tương_đối_hoặc_tuyệt_đối> <câu_hỏi>` (và qua pipe / MCP) để chạy kiểm thử tự động không phụ thuộc Clipboard hệ thống khi cần.
   - Nếu Clipboard không có ảnh (và không truyền `--file`), trả về thông báo hướng dẫn `"[VISION INFO] No image found in Clipboard. Copy an image (Win+Shift+S) or use /img --file <path> <prompt>."`
2. **Vision Cache trong SQLite WAL**:
   - Tạo khóa băm tổng hợp: `SHA-256("VISION_V1|" + imageSha256Hex + "|" + normalizedPrompt + "|" + primaryAgentId + "|" + modelName)`.
   - Tận dụng bảng `response_cache` hiện có (hoặc cột mở rộng tương thích): nếu trúng cache (`Cache Hit`), trả kết quả ngay trên luồng đồng bộ trong **`P50 <= 2.0 ms / P95 <= 3.0 ms`**, ghi nhận `total_tokens = 0` và `cacheHit = true`.
3. **Mở rộng `GeminiAgentClient` hỗ trợ Multimodal `inline_data`**:
   - Khi gửi request lên Gemini API có đính kèm ảnh (`imageBytes`, `mimeType = "image/jpeg"`), đóng gói phần tử trong `contents[0].parts` gồm:
     - `{ "inline_data": { "mime_type": "image/jpeg", "data": "<Base64>" } }`
     - `{ "text": "<prompt>" }`
   - Trong chế độ Mock (`NEXUS_GEMINI_MOCK_RESPONSE`), chuỗi trả về giả lập đính kèm dấu xác thực thị giác: `"[VISION-MOCK mime=image/jpeg bytes=<imageBytes.Length> sha256=<imageSha256Hex.Substring(0,12)>] <mock_text>"` để `Verify-Task12.ps1` kiểm chứng chính xác 100% luồng nén ảnh và truyền dữ liệu.
4. **Mở rộng `latest_dispatch.json` & MCP Tool `analyze_visual_input`**:
   - Thêm vào `latest_dispatch.json` các trường nhẹ: `"hasVisionAttachment"` (bool), `"visionImageBytes"` (int), `"visionImageSha256"` (string).
   - Bổ sung MCP Tool **`analyze_visual_input`** (Cấp Xanh) trong `NEXUSHome.McpTools` với tham số: `source` (`"clipboard"` | `"snap"` | `"file"`), `prompt` (string), `filePath` (string, tùy chọn), `nexusMode` (string, tùy chọn).

## 4. Verification Steps (Nghiệm Thu Gate 12 — `Verify-Task12.ps1`)
1. **E1 (Build `-warnaserror`, Zero-BOM & Gate 13 Polish Verification)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; toàn bộ file trong `git ls-files` đạt 0-byte BOM.
   - Kiểm chứng 2 điểm gộp từ Gate 13: (a) thẻ code trên Right MFD không còn lặp dòng tiêu đề ở phần preview, và (b) `ClipboardService` có gắn cờ `"ExcludeClipboardContentFromMonitorProcessing"`.
2. **E2 (WIC Image Compression `<= 250 KB` & Deterministic `/img` Flow)**:
   - Tạo một ảnh Bitmap thử nghiệm kích thước lớn (ví dụ `2560x1440` nhiều màu nhiễu, dung lượng gốc `> 2 MB`), đưa qua `/img` (qua Clipboard và/hoặc `--file`).
   - Xác nhận ảnh sau khi nén có dung lượng **`<= 256,000 bytes` (250 KB)**, cạnh dài `<= 1280 px`, định dạng `image/jpeg`, và phản hồi Mock nhận đúng `visionImageBytes` + mã băm `visionImageSha256`.
3. **E3 (Vision SHA-256 Cache Hit SLA `P50 <= 2.0 ms / P95 <= 3.0 ms` & Zero-Token)**:
   - Gọi lại `/img` với cùng bức ảnh và cùng câu hỏi 25 lần liên tiếp: xác nhận trúng cache 100%, `total_tokens = 0`, thời gian đồng bộ đạt **P50 `<= 2.0 ms`, P95 `<= 3.0 ms`**; khi đổi 1 pixel trên ảnh hoặc đổi câu hỏi thì băm SHA-256 thay đổi (Cache Miss đúng chuẩn).
4. **E4 (Combat-Mode Shield, Sensitive Window Privacy Guard & MCP `analyze_visual_input`)**:
   - Bật `NEXUS_SIMULATE_COMBAT_MODE=1` -> gọi `/snap` -> xác nhận bị chặn ngay bởi `[COMBAT SHIELD]` trong `< 2 ms`, không chụp màn hình.
   - Bật `NEXUS_SIMULATE_WINDOW_TITLE="Vietcombank - Password Manager"` -> gọi `/snap` -> xác nhận bị chặn ngay bởi `[PRIVACY GUARD]`.
   - Khi tắt cờ giả lập nguy hiểm -> `/snap` và MCP `analyze_visual_input` chạy thành công và đồng bộ lên `CockpitUI` trong `< 300 ms`.
5. **E5 (Memory Gate `<= 52.0 MB` After Large Bitmap Processing, CPU `0%` & `-FastRegression`)**:
   - Sau khi xử lý nén ảnh 2K và chạy hết E1–E4, chờ nhịp dọn rảnh: xác nhận **`Private Working Set <= 52.0 MB`** (chứng minh không rò rỉ bộ nhớ Bitmap/Unmanaged), CPU nền `< 0.5%`, `-FastRegression` PASS kèm `[SLA-WARN SUMMARY]`, chốt chặn Git `HEAD` không đổi và CI GitHub xanh.