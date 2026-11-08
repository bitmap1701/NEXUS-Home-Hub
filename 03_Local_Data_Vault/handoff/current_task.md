# TASK-13: Interactive HUD Micro-Widgets, Thẻ Phản Hồi Tương Tác (`Alt+C`, `Alt+P`) & Bảng Ghim Right MFD

## 1. Context Summary & Cột Mốc Kiến Trúc (Từ Gate 13a — Mốc `v0.12.2` @ `72c9ac8`)
- **Nghiệm thu Gate 13a (`v0.12.2`)**: `PASSED` 100% trên nhánh `main` tại commit `72c9ac8` (Build 0/0, 91 tệp 0-byte BOM; `Private Working Set` sau nhịp dọn 5s giảm từ `58.0 MB` xuống `41.4 MB` và hạ về `31–35 MB` khi rảnh lâu; nhãn HUD khớp thực tế với độ lệch `1.7 MB < 2.0 MB`; `-FastRegression` đạt `54.3 s` với `Count=0` cảnh báo; CI GitHub xanh).
- **Ngân sách Bộ nhớ Mở rộng cho `TASK-13`**:
  - Nhờ `TASK-13a` tạo được **18.6 MB dư địa** dưới trần cứng `60.0 MB` (và **10.6 MB dư địa** dưới cổng xanh `52.0 MB`), `TASK-13` sẽ xây dựng hệ thống **Micro-Widgets & Right MFD** theo kỷ luật tái sử dụng phần tử giao diện cố định để giữ `Private Working Set` sau nhịp dọn luôn đạt **`<= 52.0 MB`**.
- **Vị trí trong Lộ trình Phase 3 (`ROADMAP_FUTURE.md` & `docs/ADR_REGISTRY.md`)**:
  `Gate 11b (v0.11.1)` -> `TASK-14 (v0.12.0)` -> `TASK-14b (v0.12.1)` -> `TASK-13a (v0.12.2)` -> **`TASK-13` (HUD Micro-Widgets & Right MFD)** -> `TASK-12` (`/img`, `/snap`) -> `TASK-15` (`/ctx`).

## 2. Mục Tiêu TASK-13 (Actionable Response Cards, `Alt+C` / `Alt+P` & Right MFD Pinning)
Biến màn hình phụ **MSI Optix G272** thành buồng lái đa chức năng (**Multi-Function Display — MFD**) thực thụ:
1. **Bóc tách Thẻ Tương Tác Tự Động (`WidgetCardExtractor`)**: Tự động nhận diện và bóc tách các khối mã nguồn (**Code Blocks**) và danh sách việc cần làm (**Checklists / Action Steps**) từ câu trả lời của Gemini (`ai_response` / `qa_response`) cũng như kết quả tiện ích cục bộ (`/calc`, `/timer`, `/note`).
2. **Thao tác Nhanh Không Rời Tay Khỏi Bàn Phím (`Alt+C` & `Alt+P` + Slash Commands)**:
   - **`Alt+C` (hoặc `/copy [chỉ_số]`)**: Chép tức thì khối mã nguồn chính (hoặc nội dung thẻ đang chọn) vào Clipboard trong `< 2.5 ms`.
   - **`Alt+P` (hoặc `/pin [chỉ_số|nội_dung]`, `/unpin [id|all]`, `/pins`)**: Ghim thẻ quan trọng (đoạn code C# Socket, công thức `/calc`, checklist debug, hoặc ghi chú) sang khung **Right MFD** để luôn nhìn thấy trên màn MSI G272 trong lúc gõ code hoặc chơi game.
3. **Khung Hiển Thị `Right MFD` Bền Vững & Nhẹ Bộ Nhớ**: Lưu trữ danh sách ghim tại `03_Local_Data_Vault/config/mfd_pins.json` (UTF-8 không BOM, nằm trong `.gitignore`), giới hạn số ô hiển thị cố định để không làm phình cây giao diện WPF.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Bộ Bóc Tách Thẻ Phản Hồi Tương Tác (`WidgetCardExtractor.cs` — Shared Core)
- **Kỷ luật Hiệu năng & Bộ nhớ (`ADR-29`)**:
  - Viết bộ quét văn bản thuần C# theo dòng/khối (không dùng `RegexOptions.Compiled`), độ trễ bóc tách cho một phản hồi 4 KB đạt **`<= 0.5 ms`**.
- **Các loại thẻ (`HudWidgetCard`) được bóc tách tự động từ văn bản phản hồi**:
  1. **`CodeBlockCard` (`type = "code"`)**:
     - Nhận diện các khối mã nằm giữa cặp dấu rào (fenced code blocks), trích xuất `language` (ví dụ `csharp`, `powershell`, `sql`, `json`), số dòng (`lineCount`), tiêu đề tóm tắt dòng đầu và nội dung mã thuần (`codeContent`) đã cắt bỏ dấu rào để sẵn sàng copy bằng 1 phím.
  2. **`ChecklistCard` (`type = "checklist"`)**:
     - Nhận diện các dòng bước thực hiện (`- [ ] ...`, `- [x] ...`, hoặc danh sách bước đánh số ngắn), gom thành thẻ danh sách kiểm tra.
  3. **`UtilityCard` (`type = "utility"`)**:
     - Tạo thẻ gọn cho kết quả `/calc` (Dec/Hex/Bin), bộ đếm `/timer` đang chạy, hoặc ghi chú `/note` vừa lưu.
- Mở rộng `latest_dispatch.json` (tương thích ngược):
  - Bổ sung mảng nhẹ `"extractedCards"` (tối đa `5` thẻ đầu tiên của phản hồi hiện hành: `index`, `type`, `title`, `language`, `lineCount`, `previewText`) và `"pinnedCardsCount"` (int).

### 3.2. Sao Chép Tức Thì (`Alt+C` & Lệnh `/copy` — `ClipboardService`)
- **Cú pháp & Phím tắt**:
  - Phím tắt **`Alt+C`** (chỉ đăng ký bên trong cửa sổ `CockpitUI` khi cửa sổ đang mở ở Command Mode — **tuyệt đối không đăng ký `RegisterHotKey` toàn cục cho `Alt+C` hay `Alt+P`** để không bao giờ xung đột phím với game *Delta Force*, *PUBG* hay IDE Visual Studio).
  - Lệnh Slash Command trên `CockpitUI`: **`/copy`** (mặc định chép khối code đầu tiên `#1`; nếu phản hồi không có khối code thì chép toàn bộ `ai_response` / kết quả tiện ích gần nhất) hoặc **`/copy <index>`** (chép khối code thứ `<index>`, bắt đầu từ 1).
  - Hỗ trợ lệnh qua Named Pipe (ví dụ `COPYWIDGET:<index>`) để script kiểm thử đo thời gian đồng bộ chính xác.
- **Tương thích STA Clipboard & Headless CI**:
  - Trên luồng giao diện STA của `CockpitUI`, ghi nội dung vào Windows Clipboard (`Clipboard.SetText`) kèm cơ chế bắt ngoại lệ an toàn nếu một tiến trình khác đang khóa Clipboard tạm thời; đồng thời lưu vào bộ đệm `LastCopiedText` trong bộ nhớ để các bài kiểm thử tự động và chế độ Headless/MCP luôn xác minh được 100% nội dung vừa copy.

### 3.3. Bảng Ghim Đa Chức Năng `Right MFD` (`Alt+P`, `/pin`, `/unpin`, `/pins` — `MfdPinManager.cs`)
- **Lưu trữ bền vững (`03_Local_Data_Vault/config/mfd_pins.json`)**:
  - Mã hóa **UTF-8 không BOM (0-byte BOM)**, ghi nguyên tử qua file `.tmp` + `File.Move(..., overwrite: true)` (theo `ADR-14`).
  - Thêm `mfd_pins.json` (hoặc giữ nguyên quy tắc bỏ qua thư mục `03_Local_Data_Vault/config/` theo `ADR-24`) trong `.gitignore` để `git status` luôn sạch.
  - **Giới hạn Cố định (`maxPinnedCards = 5` trong `nexus_config.json`)**: Giữ tối đa `5` thẻ ghim trên Right MFD. Nếu đã đầy 5 thẻ mà ghim thêm thẻ mới (hoặc có thể chọn thay thế thẻ cũ nhất theo cơ chế FIFO / cập nhật thẻ trùng), danh sách luôn giữ đúng `<= 5` mục để bảo vệ bộ nhớ WPF và vừa khít khung nhìn màn hình **MSI Optix G272**.
  - **Kiểm tra Chống Lộ Khóa (`Secrets Hygiene`)**: Mọi nội dung đưa vào `/pin` đều đi qua bộ lọc chặn khóa API (`AIza...`) trước khi lưu xuống `mfd_pins.json`.
- **Phím tắt & Lệnh điều khiển**:
  - **`Alt+P`** (trong `CockpitUI`) hoặc **`/pin`** (không tham số): Ghim thẻ trích xuất đầu tiên (`#1`) hoặc kết quả của phiên điều phối hiện tại sang khung **Right MFD**.
  - **`/pin #<index>`**: Ghim thẻ trích xuất thứ `<index>` của phản hồi hiện tại.
  - **`/pin <văn_bản_tùy_ý>`**: Tạo và ghim nhanh một thẻ ghi nhớ trực tiếp lên Right MFD (ví dụ: `/pin IP Server Lab: 127.0.0.1:9091`).
  - **`/pins`** (bí danh `/pin list`): Liệt kê danh sách các thẻ đang ghim trên Right MFD kèm mã `pinId` (`1..5`).
  - **`/unpin <pinId|all>`** (bí danh `/pin clear`): Gỡ một thẻ ghim theo số thứ tự/ID hoặc xóa sạch toàn bộ bảng ghim Right MFD.

### 3.4. Giao Diện `Right MFD` Trên `CockpitUI` & MCP Tool `manage_hud_widgets`
1. **Thiết kế Khung `Right MFD` trên `CockpitUI` (Màn hình phụ MSI Optix G272)**:
   - Giữ nguyên `RenderMode.SoftwareOnly` (ép chạy iGPU Intel UHD 730, 0% tải lên GTX 1660) và `WS_EX_NOACTIVATE` ở chế độ Passive HUD.
   - Bố cục gọn gàng: hiển thị danh sách thẻ đang ghim (`Pinned MFD Cards`, tối đa 5 ô tái sử dụng cố định, `IsUndoEnabled = false`), trạng thái bộ đếm `/timer` đang chạy (nếu có), và thanh chỉ báo số thẻ bóc tách từ câu trả lời hiện hành (ví dụ: `[2 Code Blocks | Alt+C: Copy #1 | Alt+P: Pin]`).
2. **Công cụ MCP Mới trong `NEXUSHome.McpTools`**:
   - Bổ sung tool **`manage_hud_widgets`** (Cấp Xanh):
     - Tham số: `action` (`"extract"` | `"copy"` | `"pin"` | `"unpin"` | `"list_pins"`), `targetOrText` (string, tùy chọn — chỉ số thẻ, `all`, hoặc nội dung cần ghim).
     - Thực thi hành động, cập nhật nguyên tử `mfd_pins.json` & `latest_dispatch.json` để `CockpitUI` đang chạy tự động đồng bộ khung **Right MFD** trong `< 300 ms`, và trả kết quả JSON về cho Claude / Antigravity.

## 4. Verification Steps (Nghiệm Thu Gate 13 — `Verify-Task13.ps1`)
1. **E1 (Build `-warnaserror` & Repository-Wide Zero-BOM Check)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; toàn bộ file theo dõi bởi Git đạt 0-byte BOM; quét xác nhận `0` lần xuất hiện `RegexOptions.Compiled`.
2. **E2 (Widget Card Extraction & `/copy` / `Alt+C` Sub-2.5ms Latency)**:
   - Cung cấp một phản hồi có chứa 2 khối code (`csharp` và `powershell`) cùng 1 danh sách checklist -> xác nhận `WidgetCardExtractor` bóc tách đúng số lượng khối code, ngôn ngữ, nội dung mã thuần (không dính dấu rào) và checklist.
   - Thực thi `/copy 1` và `/copy 2` (qua pipe/command): xác nhận nội dung chép khớp 100% với từng khối code, thời gian thực thi đồng bộ đạt **P50 `<= 1.5 ms`, P95 `<= 2.5 ms`**.
3. **E3 (Right MFD Pinning Lifecycle, FIFO Cap, Secret Guard & Zero-BOM Persistence)**:
   - Thực hiện `/unpin all` -> ghim thẻ từ phản hồi hiện tại (`/pin #1`) và ghim ghi chú tùy chỉnh (`/pin Cổng TCP Socket UTH: 8888`) -> kiểm tra `mfd_pins.json` đạt chuẩn 0-byte BOM và `/pins` trả về đúng 2 thẻ.
   - Ghim liên tiếp 6 thẻ để kiểm tra giới hạn `maxPinnedCards = 5` -> xác nhận danh sách ghim luôn giữ đúng `5` thẻ mà không phình vô hạn.
   - Thử `/pin AIzaSyDummySecretKeyLeakTest123456789` -> xác nhận bị chặn bởi Secret Guard, không lưu vào `mfd_pins.json`.
4. **E4 (MCP `manage_hud_widgets` -> Live Cockpit Right MFD Sync & Focus Safety)**:
   - Gọi `manage_hud_widgets` (`action = "pin"`) từ `NEXUSHome.McpTools` -> xác nhận `CockpitUI` đang chạy tự động nhận và hiển thị thẻ ghim mới trên Right MFD trong `< 300 ms`, đồng thời cửa sổ `GetForegroundWindow()` không bị cướp Focus.
5. **E5 (Post-Slimming Memory Gate `<= 52.0 MB`, CPU `0%` & `-FastRegression`)**:
   - Sau khi nạp đầy 5 thẻ trên Right MFD, chạy chuỗi bóc tách/copy và chờ nhịp dọn rảnh (kèm chờ 2 mẫu đọc ổn định như `Verify-Task13a`): xác nhận **`Private Working Set <= 52.0 MB`**, CPU nền `< 0.5%`, `-FastRegression` PASS với `[SLA-WARN SUMMARY]`, chốt chặn Git `HEAD` không đổi và CI GitHub xanh.