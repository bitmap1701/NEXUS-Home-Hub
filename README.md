# NEXUS Home Hub — Ngôi Nhà AI Trên PC (Windows 11 Local Agentic OS Overlay)

> **Dự án Liên minh Đa Tác Nhân (Twin-Engine Setup)**: **Gemini Web (Architect & Reality Checker)** ↔ **Google Antigravity (Local IDE Executor)**  
> **Chủ sở hữu**: Huỳnh Nhật Hòa

---

## 1. Tổng quan Kiến trúc 4 Tầng (`NEXUS Home Hub`)

`NEXUS Home Hub` là hệ thống điều phối AI chạy nền trên Windows 11, kết nối **Gemini API (Live WebSocket + Function Calling)** với **Kho 283 Chuyên gia (`The Agency`)** và **Hệ điều hành Windows** thông qua giao thức **Local Model Context Protocol (MCP)**.

```
C:\Agent\NEXUS_Home_Hub\
├── GEMINI.md                        # Quy tắc phối hợp chuẩn giữa Gemini Web & Antigravity
├── README.md                        # Tài liệu tổng quan kiến trúc & trạng thái dự án
├── 01_Foundation_Engine\            # [PHẦN MÓNG] Solution & Mã nguồn lõi C# (.NET)
│   ├── NEXUSHomeHub.sln             # Solution chính của dự án
│   ├── NEXUSHome.Core\              # AgentRegistry, GovernanceGate 3 cấp, SqliteWalStore, LocalPcMcpTools
│   └── NEXUSHome.McpTools\          # Local MCP Server chuẩn JSON-RPC stdio (gắn trực tiếp vào Antigravity)
├── 02_Agents_Registry\              # Liên kết trực tiếp tới C:\Agent\.agents\skills (283 Agents)
└── 03_Local_Data_Vault\             # [KHO DỮ LIỆU 4 TẦNG] Lưu trữ cục bộ 100% trên SSD NVMe
    ├── hot_cache\                   # Cấu hình & snapshot của In-Memory / Redis Hot Cache
    ├── structured_db\               # CSDL SQLite (WAL Mode): nexus_memory.db
    ├── vector_index\                # Chỉ mục tìm kiếm ngữ nghĩa
    ├── zettelkasten\                # Thẻ ghi chú nguyên tử Markdown
    └── handoff\                     # Hộp thư bàn giao tự động giữa Gemini & Antigravity
        ├── current_task.md          # Đặc tả task hiện tại
        └── qa_report.md             # Báo cáo kết quả build/test gửi Gemini Reality Checker
```

---

## 2. Cấu hình Phần cứng & Phân luồng Tài nguyên
- **CPU / RAM**: Intel Core i5-12400 (6 nhân / 12 luồng) | 16GB DDR4 Dual-Channel.
- **Màn hình chính (Gaming & Coding)**: LG UltraGear 200Hz chạy trên card rời **Galax GTX 1660** (giữ 100% hiệu năng cho game PUBG, Delta Force, FC Online và IDE).
- **Màn hình phụ (AI Cockpit)**: MSI Optix G272 chạy trên nhân đồ họa tích hợp **Intel UHD 730**.
- **Bàn phím**: AULA HERO 68 HE (tích hợp phím tắt toàn cục gọi nhanh thanh lệnh nổi).

---

## 3. Chốt chặn An toàn 3 Cấp (`GovernanceGate`)
1. **Cấp Xanh (`Green_SafeRead`)**: Đọc thông số CPU/RAM/Processes (`get_system_telemetry`), truy vấn bộ nhớ SQLite WAL, đọc danh sách 283 Agents — Tự động thực thi.
2. **Cấp Vàng (`Yellow_ReversibleTune`)**: Tối ưu hóa Working Set RAM & nâng độ ưu tiên CPU (`optimize_pc_for_gaming_or_coding`) — Thực thi kèm ghi log kiểm toán (`Audit Log`) vào SQLite WAL.
3. **Cấp Đỏ (`Red_CriticalSystem`)**: Giải phóng cổng mạng TCP/UDP (`kill_process_by_port`), xóa file, can thiệp hệ thống — Bắt buộc xác nhận **Human-in-the-Loop (HITL)** trước khi thực thi.

---

## 4. Hướng dẫn Chạy & Tích hợp MCP Server

### Build toàn bộ Solution:
```powershell
cd C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine
dotnet build NEXUSHomeHub.sln
```

### Cấu hình MCP Server trong Antigravity (`mcp_config.json`):
```json
{
  "mcpServers": {
    "nexus-home-hub": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\Agent\\NEXUS_Home_Hub\\01_Foundation_Engine\\NEXUSHome.McpTools\\NEXUSHome.McpTools.csproj"
      ],
      "env": {
        "NEXUS_VAULT_PATH": "C:\\Agent\\NEXUS_Home_Hub\\03_Local_Data_Vault",
        "NEXUS_AGENTS_PATH": "C:\\Agent\\.agents\\skills"
      }
    }
  }
}
```
