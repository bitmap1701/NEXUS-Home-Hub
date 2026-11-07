# NEXUS HOME HUB — COLLABORATION & ARCHITECTURE RULES (GEMINI + CLAUDE + ANTIGRAVITY)

## 1. Hồ sơ Phần cứng & Đường dẫn Chuẩn (Huỳnh Nhật Hòa)
- **Root Path Cố định**: C:\Agent\NEXUS_Home_Hub (Tuyệt đối không dùng ổ D:\ — luôn dùng NexusPathResolver dò động hoặc biến môi trường NEXUS_ROOT).
- **CPU/RAM**: Intel Core i5-12400 (6C/12T) | 16GB DDR4 Dual-Channel XMP.
- **GPU & Hiển thị**: Galax GTX 1660 (Dành 100% cho màn hình chính LG 27G523B-B 200Hz & Game: Delta Force, FC Online — Nhật Hòa không còn chơi PUBG; `TslGame*` vẫn giữ trong Whitelist cho an toàn).
- **CockpitUI & HostService (ADR-05)**: Chạy chung **1 tiến trình duy nhất (In-Process Hosting)** để giữ RAM ~36-45MB và độ trễ điều phối < 2ms. CockpitUI ép RenderMode.SoftwareOnly trên màn hình phụ **MSI Optix G272**.
- **Phím tắt Chuẩn**:
  - Alt + Space: Bật/tắt Passive HUD (WS_EX_NOACTIVATE — giữ nguyên 100% focus của game).
  - Ctrl + Shift + Space: Bật Interactive Command Mode (nhập lệnh trực tiếp vào Cockpit, bấm Esc hoặc thực thi xong tự trả focus về cửa sổ trước đó).

## 2. Quy tắc Game Boost Tương thích Anti-Cheat (ADR-06)
- **Foreground & Game Shield**: Luôn kiểm tra GetForegroundWindow() và danh sách Whitelist (DeltaForce*, TslGame*, fczf*, Roblox*, SGuard*, ACE-*, BEService*, audiodg, dwm, nvcontainer). Tuyệt đối KHÔNG gọi EmptyWorkingSet lên game hoặc cửa sổ đang active.
- **ACE-Guard Priority Handling**: Không ép PriorityClass = High gây lỗi Access Denied trên tiến trình bị Kernel Anti-Cheat khóa; bắt ngoại lệ êm và chuyển sang chế độ **Background Yield** (hạ ưu tiên tiến trình nền ngốn tài nguyên về BelowNormal) kết hợp tùy chọn cấu hình Registry IFEO PerfOptions (**Cấp Đỏ — chỉ Nhật Hòa tự chạy, agent không tự ghi Registry**).

## 3. Kỷ luật Vận hành & Dev-QA Loop
- Mọi task hiện hành nằm tại 03_Local_Data_Vault/handoff/current_task.md.
- **Minimal Change**: Ưu tiên bản vá gọn sạch, dotnet build -warnaserror bắt buộc đạt 0 lỗi, 0 cảnh báo.
- **SQLite WAL Concurrency (ADR-08)**: Mọi kết nối tới 03_Local_Data_Vault/structured_db/nexus_memory.db đều phải bật PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;.

## 4. Thiết bị & Stack (giữ từ bản trước)
- **Thiết bị ngoại vi**: Bàn phím AULA HERO 68 HE (Global Hotkey gọi nhanh thanh lệnh nổi), Mic rời (PCM 16kHz + VAD), Tai nghe In-ear.
- **Kho Agent**: 283 Agents tại `C:\Agent\.agents\skills` (+ `02_Agents_Registry`).
- **Stack Kỹ thuật**: C# (.NET 8 / .NET 10), Raw/Async TCP & UDP Sockets, Redis / In-Memory Hot Cache (< 128MB), SQLite (`WAL Mode`), Local MCP Server (JSON-RPC stdio).

## 5. Phân vai Liên minh (giữ từ bản trước)
- **Gemini (Architect & Reality Checker)**: Thiết kế kiến trúc, ra đề bài tại `03_Local_Data_Vault/handoff/current_task.md`, nghiệm thu độc lập qua `qa_report.md` và `gate<N>_handoff.md`.
- **Antigravity (Local IDE Executor)**: Viết code C#, chạy `dotnet build` / `dotnet test`, đồng bộ lên GitHub.
- **Claude (Ca luân phiên)**: Tiếp quản code khi Antigravity hết token, làm trên nhánh `claude/*`. Cuối mỗi ca ghi `03_Local_Data_Vault/handoff/gate<N>_handoff.md`. Luật chi tiết xem `CLAUDE.md`.
- **Ghi file đặc tả từ PowerShell**: dùng here-string **nháy đơn** `@' ... '@`. Trong `@" ... "@`, dấu backtick của Markdown bị hiểu là escape (`` `a ``, `` `f ``, `` `0 ``, `` `n ``, `` `t ``), làm hỏng tên như `agent_dispatch_logs`, `fczf*`, `03_Local_Data_Vault`.

## 6. Chốt chặn An toàn (giữ từ bản trước — bắt buộc cho mọi agent)
- **Chốt chặn 3 Cấp (3-Tier Execution Gate)**:
  1. **Cấp Xanh (`Green_SafeRead`)**: Đọc thông số CPU/RAM/Processes, đọc file code, chạy `dotnet build`, `dotnet test`, `git status` — Tự động thực thi 100%.
  2. **Cấp Vàng (`Yellow_ReversibleTune`)**: Tinh chỉnh `ProcessPriorityClass`, dọn Working Set RAM (`EmptyWorkingSet`), sửa file code — Tự động tạo bản lưu Git/Snapshot trước khi ghi.
  3. **Cấp Đỏ (`Red_CriticalSystem`)**: Xóa file, `KillProcessByPort`, can thiệp Windows Registry, chạy PowerShell quyền Admin — **BẮT BUỘC** dừng lại hỏi xác nhận Human-in-the-Loop của Nhật Hòa.
- **Cầu chì 3 Lần Thử (3-Strike Circuit Breaker)**: Nếu `dotnet build` hoặc test thất bại 3 lần liên tiếp, dừng việc tự sửa và xuất báo cáo vào `03_Local_Data_Vault/handoff/qa_report.md`.
- **Bảo mật Khóa API**: Lưu khóa Gemini API bằng Windows DPAPI (`ProtectedData`) hoặc `dotnet user-secrets` / biến môi trường, tuyệt đối không hardcode vào Git.
