# TASK-04: Nối CockpitUI vào SquadDispatcher (In-Process) & Hoàn thiện Anti-Cheat Safe Game Boost

## 1. Context Summary & Quyết định Kiến trúc (Từ Gate 3)
- **Nghiệm thu Gate 3**: PASSED 100% (Verify-Task03 E1–E7 ALL PASS trên máy thật của Nhật Hòa khi đang chơi Delta Force; RAM 36.4MB, CPU 0%, P95 dispatch < 2ms).
- **4 Quyết định Kiến trúc Đã Chốt (ADR-05 .. ADR-08)**:
  1. **ADR-05 (In-Process Architecture)**: Giữ CockpitUI chạy chung trong tiến trình NEXUSHome.HostService. Không tách 2 tiến trình, không dùng Named Pipes IPC. CockpitUI gọi trực tiếp SquadDispatcher và GameBoost trong bộ nhớ.
  2. **ADR-06 (ACE-Guard Safe Game Boost)**:
     - Luôn bảo vệ cửa sổ đang active (GetForegroundWindow()) và Whitelist game/anti-cheat (DeltaForce*, SGuard*, ACE-*, TslGame*, BEService*, fczf*, audiodg, dwm, nvcontainer) khỏi EmptyWorkingSet.
     - Khi gặp tiến trình game được bảo vệ bởi Kernel Anti-Cheat (như ACE-Guard của Delta Force làm SetPriorityClass trả về Access Denied), bắt ngoại lệ êm và chuyển sang **Background Yield Mode** (hạ các tiến trình nền ngốn RAM/CPU > 100MB không nằm trong danh sách hệ thống xuống BelowNormal để nhường CPU cho game), đồng thời báo cáo rõ trạng thái "ACE-Guard Protected (Background Yield Active)".
  3. **ADR-07 (Root Path Standardization)**: Thư mục gốc chuẩn là C:\Agent\NEXUS_Home_Hub. Thay thế toàn bộ chuỗi cứng D:\NEXUS_Home_Hub còn sót bằng NexusPathResolver tự động dò gốc dự án.
  4. **ADR-08 (SQLite WAL Shared Access)**: Bật PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; cho mọi kết nối SQLite để HostService và McpTools đọc/ghi đồng thời không bị SQLITE_BUSY.

## 2. Mục tiêu TASK-04 (Hướng A — Wire CockpitUI into SquadDispatcher)
Biến cửa sổ CockpitUI trên màn hình phụ **MSI Optix G272** thành Bảng điều khiển Điều phối Agent thời gian thực:
1. Khi bấm Ctrl + Shift + Space, người dùng gõ yêu cầu (hoặc lệnh nhanh) vào ô Command Input trên CockpitUI và nhấn Enter:
   - Gọi trực tiếp SquadDispatcher (In-Process, độ trễ < 5ms) để chọn Squad, Primary Agent và QA Agent từ 283 agents.
   - Lưu bản ghi điều phối vào bảng agent_dispatch_logs trong SQLite WAL (nexus_memory.db).
   - Xuất gói ngữ cảnh điều phối mới nhất ra file 03_Local_Data_Vault/handoff/latest_dispatch.json (gồm dispatchId, timestampUtc, taskSummary, nexusMode, squadId, primaryAgent, qaAgent, systemInstructionPreview) để Claude / Antigravity / Gemini có thể đọc ngay khi phối hợp.
2. Hiển thị trực quan kết quả điều phối và lịch sử gần nhất ngay trên CockpitUI (vẫn giữ RenderMode.SoftwareOnly).
3. Trả Focus thông minh (Focus Restoration): Trước khi Ctrl + Shift + Space lấy focus cho CockpitUI, lưu lại HWND của cửa sổ đang active (GetForegroundWindow()). Ngay khi người dùng nhấn Enter (gửi lệnh xong) hoặc nhấn Esc, tự động chuyển CockpitUI về lại chế độ Passive HUD (WS_EX_NOACTIVATE) và gọi SetForegroundWindow(previousHwnd) để trả con trỏ/bàn phím về ngay cho Game hoặc IDE.

## 3. Đặc tả Kỹ thuật Chi tiết (Technical Deliverables)

### A. Luồng Lệnh Tương tác trên CockpitUI (Ctrl + Shift + Space)
- **Hỗ trợ 2 loại đầu vào tại ô Command Box**:
  1. **Slash Commands Nội bộ (Thực thi tức thì)**:
     - /boost (hoặc /boost <ProcessName>): Chạy GameBoost chuẩn ADR-06 (có Foreground Shield + ACE-Guard Safe Yield), hiển thị số MB RAM thu hồi được từ các tiến trình nền và trạng thái bảo vệ game.
     - /status (hoặc /telemetry): Cập nhật nhanh CPU/RAM của HostService, tên cửa sổ Foreground hiện tại và tổng số Agents đã nạp (283).
     - /mode micro|sprint|full: Đổi chế độ NEXUS mặc định cho các lần điều phối tiếp theo.
  2. **Natural Language Task Dispatch (Mặc định khi gõ câu bất kỳ)**:
     - Ví dụ gõ: "Tối ưu UDP Socket C# chống rớt gói tin" hoặc "Kiểm tra frame pacing Delta Force trên GTX 1660".
     - Gọi SquadDispatcher ngay trong tiến trình -> hiển thị ngay lên thẻ **Active Dispatch Card** trên CockpitUI:
       - Squad: (ví dụ Squad 1 — Core .NET & Network hoặc Squad 3 — Gaming Performance)
       - Primary Agent + QA Agent + Reality Checker
       - Dispatch ID (từ SQLite WAL) + Latency (ms)
     - Ghi file 03_Local_Data_Vault/handoff/latest_dispatch.json (UTF-8 không BOM).

### B. Bố cục Giao diện CockpitUI (Dark HUD trên MSI Optix G272, SoftwareOnly)
- Giữ giao diện tối giản, chữ sắc nét, không dùng hiệu ứng Blur/Aero nặng GPU:
  1. **Thanh Trạng Thái Đỉnh (Top Bar)**: Tên NEXUS HOME COCKPIT | Mode (Micro/Sprint/Full) | RAM hiện tại của HostService (MB) | Trạng thái HUD (PASSIVE [Alt+Space] hoặc COMMAND [Ctrl+Shift+Space]).
  2. **Ô Nhập Lệnh (Command Input Box)**: Chỉ bật nhận phím khi ở chế độ Ctrl + Shift + Space; có gợi ý mờ (Gõ nhiệm vụ để gọi Agent Squad hoặc /boost, /status... [Enter: Gửi | Esc: Thoát về Game]).
  3. **Thẻ Điều Phối Hiện Hành (Active Squad Card)**: Hiển thị kết quả vừa gọi (Squad, Primary Agent, QA Agent, DispatchId, ElapsedMs, tóm tắt nhiệm vụ).
  4. **Danh Sách 5 Phiên Điều Phối Gần Nhất (Recent Dispatches Feed)**: Đọc 5 dòng mới nhất từ bảng agent_dispatch_logs trong SQLite WAL (nhờ đó nếu Claude/Antigravity gọi dispatch_agent_squad qua McpTools, bấm làm mới hoặc mở HUD cũng thấy đồng bộ).

### C. Hoàn thiện GameBoost & Chuẩn hóa Đường dẫn (NexusPathResolver)
- Rà soát và đảm bảo GameBoost:
  - Luôn lấy GetForegroundWindow() -> GetWindowThreadProcessId() đưa vào danh sách miễn trừ động trước mỗi lần quét.
  - Không bao giờ ném lỗi chưa bắt khi PriorityClass = High bị từ chối bởi ACE-Guard (Delta Force); trả về thông báo rõ ràng gồm: số tiến trình nền đã thu hồi Working Set, tổng dung lượng RAM giải phóng (MB), và trạng thái của tiến trình mục tiêu/Foreground.
- Rà soát toàn bộ Solution đảm bảo không còn đường dẫn cứng D:\NEXUS_Home_Hub.

## 4. Constraints & Ngân sách Tài nguyên
- **Kỷ luật Minimal Change**: Giữ nguyên toàn bộ test E1–E5 của Verify-Task03 đang PASS.
- **Ngân sách Tài nguyên**: Tổng Working Set RAM của HostService (đã mở CockpitUI và nạp 283 agents) phải giữ **dưới 60 MB**; CPU khi đứng yên ở mức **0%**.
- **UTF-8 No BOM**: Mọi file JSON/log hoặc luồng stdio đều dùng new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).

## 5. Verification Steps (Nghiệm thu Gate 4 — Verify-Task04.ps1)
1. **E1 (Build & Zero-Hardcoded-D-Drive Check)**: dotnet build -warnaserror đạt 0 lỗi, 0 cảnh báo; quét toàn bộ file .cs xác nhận không còn chuỗi cứng D:\NEXUS_Home_Hub.
2. **E2 (In-Process Cockpit Dispatch & latest_dispatch.json)**: Gọi luồng xử lý lệnh của Cockpit với câu "Thiết kế C# Async TCP Server với Redis caching", xác nhận P95 < 10ms, bản ghi mới được lưu vào agent_dispatch_logs (SQLite WAL) và file 03_Local_Data_Vault/handoff/latest_dispatch.json được tạo hợp lệ.
3. **E3 (Slash Command /boost ACE-Guard & Foreground Shield Test)**: Gọi /boost khi có một tiến trình Foreground đang mở, xác nhận tiến trình Foreground và các tiến trình trong Whitelist (DeltaForce*, TslGame*, SGuard*) **không bị giảm Working Set**, không quăng ngoại lệ khi bị từ chối quyền SetPriorityClass.
4. **E4 (Cross-Process SQLite WAL Sync)**: Giữ HostService đang mở kết nối SQLite WAL, đồng thời gọi dispatch_agent_squad qua NEXUSHome.McpTools (stdio) và xác nhận cả hai đọc/ghi thành công 100% không bị SQLITE_BUSY.
5. **E5 (Kiểm tay Thực chiến trên Máy Nhật Hòa)**:
   - Bấm Ctrl + Shift + Space -> gõ lệnh điều phối -> nhấn Enter -> CockpitUI bên màn MSI G272 hiển thị ngay thẻ Squad + Agent mới và tự động trả Focus lại cho cửa sổ trước đó (Game / IDE).
6. **E6 (Resource Budget Check)**: Đo RAM của HostService sau khi thực hiện 5 lần điều phối liên tiếp trên CockpitUI, xác nhận RAM < 60 MB và CPU nền ~0%.
