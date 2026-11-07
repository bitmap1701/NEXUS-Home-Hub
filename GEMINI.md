# NEXUS HOME HUB — ANTIGRAVITY & GEMINI COLLABORATION RULES

## 1. Hồ sơ Phần cứng & Môi trường Cố định (Huỳnh Nhật Hòa)
- **CPU/RAM**: Intel Core i5-12400 (6C/12T) | 16GB DDR4 Dual-Channel XMP.
- **GPU & Hiển thị**:
  - **Màn hình chính**: LG UltraGear 27G523B-B 200Hz chạy trên **Galax GTX 1660** (Ưu tiên 100% tài nguyên cho Game & IDE chính, giữ tải GPU < 80%, frame time < 5ms).
  - **Màn hình phụ**: MSI Optix G272 chạy trên **iGPU Intel UHD 730** (Nơi ghim cố định giao diện Cockpit UI của NEXUS Home Hub).
- **Thiết bị ngoại vi**: Bàn phím AULA HERO 68 HE (Global Hotkey gọi nhanh thanh lệnh nổi), Mic rời (PCM 16kHz + VAD), Tai nghe In-ear.
- **Workspace & Đường dẫn Gốc**: `C:\Agent\NEXUS_Home_Hub` (Kho 283 Agents nằm sẵn tại `C:\Agent\.agents\skills`).
- **Stack Kỹ thuật**: C# (.NET 8 / .NET 10), Raw/Async TCP & UDP Sockets, Redis / In-Memory Hot Cache (< 128MB), SQLite (`WAL Mode`), Local MCP Server (JSON-RPC stdio).

## 2. Giao thức Phối hợp Liên minh (NEXUS Dev-QA Loop: Gemini Web ↔ Antigravity)
- **Phân vai**:
  - **Gemini (Architect & Reality Checker)**: Thiết kế kiến trúc, chuẩn hóa CSDL, ra đề bài tại `03_Local_Data_Vault/handoff/current_task.md` và nghiệm thu độc lập qua `03_Local_Data_Vault/handoff/qa_report.md`.
  - **Antigravity (Local IDE Executor)**: Trực tiếp viết code C#, chạy `dotnet build` / `dotnet test`, cấu hình Local MCP Server và đồng bộ lên GitHub repository để Gemini đọc trực tiếp.
  - **Claude (Ca luân phiên)**: Tiếp quản code khi Antigravity hết token, làm trên nhánh `claude/*`. Cuối mỗi ca, Claude ghi `03_Local_Data_Vault/handoff/gate2_handoff.md` (bàn giao + đề xuất task tiếp theo). Luật chi tiết xem `CLAUDE.md`.
- **Kỷ luật Minimal Change**: Khi sửa bug, ưu tiên bản vá tối thiểu (< 30 dòng), không tự ý đập bỏ kiến trúc cũ hoặc thêm tính năng thừa (No Luxury Features).
- **Chốt chặn An toàn 3 Cấp (3-Tier Execution Gate)**:
  1. **Cấp Xanh (`Green_SafeRead`)**: Đọc thông số CPU/RAM/Processes, đọc file code, chạy `dotnet build`, `dotnet test`, `git status` — Tự động thực thi 100%.
  2. **Cấp Vàng (`Yellow_ReversibleTune`)**: Tinh chỉnh `ProcessPriorityClass`, dọn Working Set RAM (`EmptyWorkingSet`), sửa file code — Tự động tạo bản lưu Git/Snapshot trước khi ghi.
  3. **Cấp Đỏ (`Red_CriticalSystem`)**: Xóa file, `KillProcessByPort`, can thiệp Windows Registry, chạy PowerShell quyền Admin — **BẮT BUỘC** dừng lại hỏi xác nhận Human-in-the-Loop của Nhật Hòa.
- **Cầu chì 3 Lần Thử (3-Strike Circuit Breaker)**: Nếu chạy `dotnet build` hoặc test thất bại 3 lần liên tiếp, dừng ngay việc tự sửa và xuất báo cáo vào `03_Local_Data_Vault/handoff/qa_report.md`.
- **Bảo mật Khóa API**: Lưu trữ khóa Gemini API bằng Windows DPAPI (`ProtectedData`) hoặc `dotnet user-secrets` / biến môi trường, tuyệt đối không hardcode vào Git.
