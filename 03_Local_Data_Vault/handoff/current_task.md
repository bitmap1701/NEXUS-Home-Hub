# TASK-14: Tiện Ích Nội Bộ Không Tốn Token (`/calc`, `/timer`, `/note` — ADR-59) — Mở Màn Phase 3

## 1. Context Summary & Cột Mốc Kiến Trúc (Từ Gate 11b — Mốc v0.11.1)
- **Nghiệm thu Gate 11b (v0.11.1)**: PASSED 100% ở lần chạy 5 tại commit c0618fa và đã fast-forward lên nhánh main (Build 0/0, 0 BOM, bảo toàn 3.001 dòng SQLite WAL; 24.216 lần đọc song song 0 lỗi; gọi thật gemini-3.5-flash-lite trả lời ngay; `/ground` thật trúng cache đạt P50 1.715 ms / P95 2.294 ms dưới ngưỡng 2.5 ms; Private Working Set 49 MB, CPU nền 0%; hồi quy Task10..03 PASS 100%).
- **Thứ tự triển khai Phase 3 đã khóa trong ROADMAP_FUTURE.md & docs/ADR_REGISTRY.md**:
  Gate 11b (v0.11.1) -> **TASK-14 (`/calc`, `/timer`, `/note` — ADR-59)** -> TASK-13 (HUD Micro-Widgets, Right MFD & sửa hiển thị Private Working Set trên HUD) -> TASK-12 (`/img`, `/snap`) -> TASK-15 (`/ctx`).
- **Quyết định bổ sung từ Gate 11b**:
  - Thêm `"Overwolf"` vào danh sách mặc định `combatProcessPrefixes` trong cấu hình để nhận diện đúng trạng thái tải nền khi chơi game.
  - Ghi nhận việc đổi chỉ số hiển thị bộ nhớ trên HUD từ `PrivateMemorySize64` sang `Private Working Set` sẽ thực hiện ở TASK-13 (hoặc có thể chỉnh nhẹ ngay nếu thuận tiện mà không ảnh hưởng test cũ).

## 2. Mục Tiêu TASK-14 (ADR-59 — Zero-Token Local Utilities: `/calc`, `/timer`, `/note`)
Xây dựng bộ 3 tiện ích chạy hoàn toàn nội bộ trong tiến trình C# (**0 token Gemini, 0 gọi mạng, độ trễ đồng bộ P50 <= 1.5 ms / P95 <= 2.5 ms ở chế độ Desktop**) phục vụ trực tiếp trên thanh lệnh CockpitUI (`Ctrl+Shift+Space`) và qua `NEXUSHome.McpTools`:
1. **`/calc <biểu_thức>` (bí danh `/c`, `/=`)**: Máy tính biểu thức toán học, hàm kỹ thuật và chuyển đổi hệ cơ số Dec / Hex (`0x...`) / Bin (`0b...`) tức thì.
2. **`/timer <thời_lượng> [nhãn]`**: Đồng hồ đếm ngược chạy ngầm (0% CPU khi không có timer), khi hết giờ tự động thông báo trực quan trên màn hình phụ MSI Optix G272 ở chế độ Passive HUD (`WS_EX_NOACTIVATE`) tuyệt đối không cướp Focus của game.
3. **`/note <nội_dung>`**: Ghi chú nguyên tử tốc độ cao vào kho `03_Local_Data_Vault/zettelkasten/quick_notes.md` (UTF-8 không BOM), chặn rò rỉ khóa API và đồng bộ tức thì với bộ tìm kiếm của `/ground` (`/g`) ngay trong cùng tiến trình.

## 3. Đặc Tả Kỹ Thuật Chi Tiết (Technical Deliverables)

### 3.1. Bộ Tính Toán Biểu Thức Cục Bộ (`/calc` — `LocalCalculator.cs`)
- **Cú pháp hỗ trợ**:
  - `/calc <biểu_thức>`, `/c <biểu_thức>`, hoặc `/= <biểu_thức>`.
- **Tập phép toán & hàm hỗ trợ**:
  - Số học: `+`, `-`, `*`, `/`, `%`, lũy thừa `^` (hoặc `pow(a,b)`), dấu ngoặc `(...)`, số âm/dương một ngôi.
  - Hằng số & Hàm: `pi`, `e`, `sqrt(x)`, `abs(x)`, `round(x)`, `floor(x)`, `ceil(x)`, `min(a,b)`, `max(a,b)`, `log(x)`, `log2(x)`, `sin(x)`, `cos(x)`.
  - Hệ cơ số & Bitwise (phục vụ tính toán cổng mạng, subnet, gói tin TCP/UDP): Nhận đầu vào số nguyên dạng thập phân, Hex (`0xFF`), Binary (`0b1010`), hỗ trợ phép dịch bit / bitwise (`<<`, `>>`, `&`, `|`) hoặc khi kết quả là một số nguyên trong phạm vi `Int64` thì định dạng chuỗi trả về gồm cả 3 hệ:
    `[CALC] <biểu_thức> = <Dec> (Hex: 0x<Hex> | Bin: 0b<Bin>)`
- **Kỷ luật Bộ nhớ & An toàn (ADR-29)**:
  - Viết bộ phân tích biểu thức thuần C# (Recursive Descent hoặc Shunting-Yard), **không** dùng `RegexOptions.Compiled` hay sinh Dynamic IL Assembly làm tăng LoaderHeap.
  - Bắt mọi lỗi chia cho 0, tràn số hoặc sai cú pháp êm ái, trả về `[CALC ERROR] <mô tả ngắn>` mà không ném ngoại lệ chưa bắt.

### 3.2. Bộ Đếm Ngược Không Xâm Phạm Focus (`/timer` — `LocalTimerManager.cs`)
- **Cú pháp hỗ trợ**:
  - `/timer <thời_lượng> [nhãn]`: Hỗ trợ các đơn vị `ms` (phục vụ kiểm thử tự động nhanh, ví dụ `250ms`), `s`, `m`, `h`, ghép đơn vị `1h30m` hoặc `2m30s`, và số nguyên thuần (mặc định tính bằng giây, ví dụ `/timer 60 Nghỉ mắt`).
  - `/timer list` (bí danh `/timers`): Hiển thị danh sách các bộ đếm đang chạy (`Active`) kèm mã `timerId`, nhãn và thời gian còn lại.
  - `/timer cancel [id|all]` (bí danh `/timer stop [id|all]`, `/timer clear`): Hủy một bộ đếm cụ thể hoặc toàn bộ bộ đếm đang chạy.
- **Vận hành & Bảo vệ Focus Game (ADR-05)**:
  - Khi không có timer nào đang chạy, tuyệt đối không duy trì vòng lặp đánh thức định kỳ để giữ CPU nền ở mức 0%.
  - Khi timer đếm về 0:
    - Chuyển trạng thái timer sang `EXPIRED` / `COMPLETED`.
    - Ghi cập nhật thông báo `[TIMER EXPIRED] #<id>: <nhãn> (<thời_lượng>)` lên thẻ trạng thái của CockpitUI và `latest_dispatch.json`.
    - Nếu cửa sổ CockpitUI đang ẩn, hiển thị cửa sổ lên màn hình MSI Optix G272 ở chế độ **Passive HUD (`WS_EX_NOACTIVATE`)** — bảo toàn 100% cửa sổ `GetForegroundWindow()` hiện tại của người dùng.

### 3.3. Ghi Chú Nhanh Zettelkasten & Liên Thông `/ground` (`/note` — `LocalNoteService.cs`)
- **Cú pháp hỗ trợ**:
  - `/note <nội_dung>`: Ghi nối tiếp một mục ghi chú có đánh dấu thời gian (`- [YYYY-MM-DD HH:mm:ss UTC] <nội_dung>`) vào file `03_Local_Data_Vault/zettelkasten/quick_notes.md` (lưu chuẩn **UTF-8 không BOM**).
  - `/note list [N]`: Trả về `N` ghi chú mới nhất (mặc định `5`).
  - `/note find <từ_khóa>` (bí danh `/note search <từ_khóa>`): Tìm và trả về các ghi chú trong `quick_notes.md` khớp từ khóa (không phân biệt hoa thường).
- **Bảo mật Khóa & Vệ sinh Git (ADR-24 & Gate 7)**:
  - **Chốt chặn Lộ khóa (Secret Guard)**: Kiểm tra đầu vào của `/note` bằng bộ lọc chống lộ khóa API (`AIza...` hoặc mẫu khóa bí mật); nếu phát hiện, từ chối ghi xuống đĩa và trả về thông báo chặn an toàn.
  - Đảm bảo `03_Local_Data_Vault/zettelkasten/quick_notes.md` được khai báo trong `.gitignore` để các ghi chú cá nhân hoặc ghi chú sinh ra khi chạy kiểm thử không bao giờ làm bẩn `git status`.
- **Liên thông Tức thì với `/ground` (ADR-50)**:
  - Ngay sau khi `/note` ghi một thông tin mới vào `quick_notes.md`, bộ tìm kiếm Vault (`VaultGrounding` / `/ground` / `/g` và MCP `search_local_vault`) phải nhận diện được ngay nội dung mới đó ở lệnh gọi kế tiếp mà không cần khởi động lại `HostService` (làm mới/vô hiệu hóa cache danh mục file Vault nếu có).

### 3.4. Lưu Vết Zero-Token, Đồng Bộ `latest_dispatch.json` & MCP Tool `run_local_utility`
1. **Không làm sai lệch Quota & Thống kê AI**:
   - Khi thực thi `/calc`, `/timer`, `/note`, hệ thống ghi nhận `prompt_tokens = 0`, `completion_tokens = 0`, `total_tokens = 0`, `ai_model = "local-utility"`, `status = "PASSED"` (hoặc cập nhật vào bảng log / `latest_dispatch.json` theo chuẩn hiện hành sao cho bộ đếm token Live và Mock đều tăng thêm đúng `0` token).
2. **MCP Tool mới trong `NEXUSHome.McpTools`**:
   - Bổ sung công cụ `run_local_utility` (Cấp Xanh):
     - Tham số: `utility` (`"calc"` | `"timer"` | `"note"`), `commandText` (string — phần biểu thức hoặc tham số lệnh).
     - Thực thi tiện ích tương ứng, cập nhật `latest_dispatch.json` nguyên tử (ADR-14) để `CockpitUI` đang chạy cập nhật trong `< 300 ms`, và trả kết quả JSON về cho MCP client.

## 4. Verification Steps (Nghiệm Thu Gate 14 — `Verify-Task14.ps1`)
1. **E1 (Build `-warnaserror`, Zero-BOM & Database Integrity)**:
   - `dotnet build -warnaserror` đạt 0 lỗi, 0 cảnh báo; tất cả file cấu hình/tài liệu đều 0-byte BOM; bảo toàn toàn bộ `>= 3.001` dòng trong SQLite WAL.
2. **E2 (`/calc` Precision, Hex/Bin Formatting, Error Guard & Sub-2.5ms SLA)**:
   - Kiểm tra biểu thức số học hỗn hợp, hàm toán học (`sqrt(144) + 2^8`), biểu thức Hex/Bin (`0xFF + 0b101`), và bắt lỗi chia cho 0 (`10 / 0`) / sai cú pháp.
   - Đo 30 lần gọi `/calc` liên tiếp: xác nhận P50 `<= 1.5 ms`, P95 `<= 2.5 ms` (Desktop Mode), và tổng token tiêu thụ (cả Live lẫn Mock) tăng đúng `0`.
3. **E3 (`/timer` Sub-Second Expiration, Cancel & Zero-Focus-Steal Check)**:
   - Tạo một timer ngắn (`/timer 250ms Kiểm thử tự động Gate 14`), kiểm tra `/timer list` xác nhận timer đang chạy.
   - Chờ `~400 ms` cho timer hoàn tất -> xác nhận trạng thái báo hết giờ (`EXPIRED` / `COMPLETED`) xuất hiện trong kết quả/HUD và cửa sổ `GetForegroundWindow()` trước/sau khi hết giờ giữ nguyên 100%.
   - Tạo tiếp một timer `/timer 60s ToCancel` rồi gọi `/timer cancel all` -> xác nhận `/timer list` trống (0 timer đang chạy).
4. **E4 (`/note` CRUD, Secret-Key Block & Immediate `/ground` Cohesion)**:
   - Gọi `/note Mã cổng thử nghiệm của Gate 14 là 49152` -> kiểm tra file `quick_notes.md` đạt chuẩn 0-byte BOM, gọi `/note list` và `/note find 49152` đều trả về đúng ghi chú.
   - Thử gọi `/note Khóa thử nghiệm AIzaSyDummySecretKeyForTest123456789` -> xác nhận bị chặn bởi Secret Guard và chuỗi khóa không xuất hiện trong `quick_notes.md` hay SQLite.
   - Gọi ngay `/ground Mã cổng thử nghiệm của Gate 14 là bao nhiêu?` (chế độ Mock) -> xác nhận ngữ cảnh trích xuất từ Vault có chứa đoạn ghi chú `49152` vừa tạo mà không cần khởi động lại tiến trình.
5. **E5 (MCP `run_local_utility` Live Sync, Resource Budget & Regression Suite)**:
   - Gọi `run_local_utility` qua `NEXUSHome.McpTools` -> xác nhận `CockpitUI` đang chạy nhận kết quả đồng bộ trong `< 300 ms`.
   - `Private Working Set` của `HostService` duy trì `< 60 MB` (mục tiêu vùng 45–52 MB), CPU nền `< 1%`, `git status` sạch tuyệt đối (dọn sạch ghi chú test tạm sau khi провер) và bộ kiểm thử hồi quy PASS 100%.