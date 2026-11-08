# 📦 HỒ SƠ BÀN GIAO — Gate 13a (TASK-13a: Cockpit Memory Slimming, nhãn Private WS, `[SLA-WARN SUMMARY]`)

- **Ca thực thi**: Claude, 2026-11-08 (ca tối, sau khi Gemini đóng Gate 14b / `v0.12.1` @`67d01eb`)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude Code trên máy Nhật Hòa
- **Nhánh**: **`claude/task-13a-memory-slimming`**, tách từ `main` @`54cf26c`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-13a)
- **Chi tiết kỹ thuật**: `qa_report.md` §17
- **Trạng thái build**: phần C# đã được **CI GitHub build xanh** (`-warnaserror`, .NET 8). Script PowerShell và số đo bộ nhớ **chưa chạy** (môi trường của Claude không có Windows). Không có số đo bộ nhớ trong file này.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Đo Private WS trong tiến trình | Không có; HUD hiện `PrivateMemorySize64` (~94 MB) và Working Set | `MemoryDiagnostics`: `GetProcessMemoryInfo` EX2, dự phòng `QueryWorkingSet`; không dùng `PerformanceCounter` |
| 2 | Giải phẫu bộ nhớ | Không có | `/mem` và pipe `MEM`: Private WS, WS, Commit, managed heap, GC heap/committed/fragmented, SQLite native, agent, Vault, luồng, handle |
| 3 | SQLite page cache | Mặc định 2 MB mỗi kết nối | `PRAGMA cache_size=-512` (512 KB) |
| 4 | Dọn khi rảnh | Chỉ có `EmptyWorkingSet` khi PanicHide | Bộ đếm chạy một lần sau 5 s rảnh: đóng pool SQLite rảnh, GC Aggressive có nén; không vòng lặp định kỳ, không `EmptyWorkingSet` |
| 5 | Công tắc A/B | — | `NEXUS_MEMORY_SLIMMING=0` tắt các biện pháp 3–4 để đo "trước" |
| 6 | Nhãn HUD | `RAM: <WS> MB`, `Private Mem : ~94 MB` | `Private WS: xx.x MB / 60 MB` trên thanh trạng thái, MFD và `/status` |
| 7 | `[SLA-WARN SUMMARY]` | Chỉ in từng dòng `[SLA-WARN]` | Dòng tổng kết sau mỗi lượt nhanh; `[DRIFT-ALERT]` khi > 2 cảnh báo, hoặc cùng metric ở 3 Gate liên tiếp |
| 8 | RAM ở Verify-Task14 E5 | Đọc trước khi lấy mẫu CPU | Đọc sau 10 mẫu CPU (trạng thái rảnh, sau lần dọn) |
| 9 | Kiểm thử | — | Selftest `mem-profile` + `Verify-Task13a.ps1` (E1–E5, A/B "trước/sau" tự động) |

---

## 2. Trạng thái Gate 13a — ⏳ CHƯA ĐỦ BẰNG CHỨNG (C# đã build trên CI; chưa chạy trên máy)

### Lệnh chạy (Claude Code hoặc Nhật Hòa trên máy)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-13a-memory-slimming
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13a.ps1
```
Không chạy lệnh git nào trong repo khi Verify đang chạy. Lượt này không tốn token Gemini: E2 chạy HostService với Gemini giả lập, còn tầng nhanh ở E4 bỏ qua lượt gọi thật. Script sẽ tắt và khởi động lại HostService vài lần (qua pipe SHUTDOWN).

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + 0 BOM | `dotnet build -c Release -warnaserror` 0/0; `Test-NexusBom` 0 vi phạm | Chưa chạy (CI: build 0/0, 0 BOM) |
| E2 | A/B bộ nhớ trên HostService thật | Cùng tải (`/ask` ×6 mock, `/g` ×10, `/calc` ×30, `/note list` + `find`), ẩn, chờ 8 s: lượt **sau** (bật giảm bộ nhớ) **≤ 52,0 MB** (mục tiêu ≤ 50,0); lần dọn đã chạy; selftest `mem-profile` PASS; in bảng giải phẫu cả hai lượt | Chưa chạy |
| E3 | Nhãn HUD | `Private WS: xx.x MB / 60 MB` lệch < 2,0 MB so với số đo WMI; `/status` có "Private WS:"; khác Commit (`PrivateMemorySize64`) | Chưa chạy |
| E4 | Tầng nhanh | `Invoke-NexusRegression -FastRegression` < 90 s, PASS, in `[SLA-WARN SUMMARY] Count=…` | Chưa chạy |
| E5 | CPU + Git + CI | CPU Cockpit rảnh < 0,5 %; nhánh + HEAD không đổi, cây sạch; CI xanh | CI: xanh trên commit WIP |

---

## 3. Điểm mở cần Architect quyết định

1. **Không làm LRU cho AgentRegistry / VaultGrounding.** Hai thành phần này chỉ giữ khoảng 0,1–0,2 MB metadata và một memo 5 s, nên LRU không mang lại gì. `/mem` báo số liệu để đối chiếu.
2. **Chưa đổi `UndoLimit`.** Cockpit chỉ có một ô lệnh, bị xóa sau mỗi lệnh; đầu ra là danh sách ảo hóa đã giới hạn 200 dòng.
3. **Dọn khi rảnh bằng GC Aggressive và xóa pool SQLite**, không dùng `EmptyWorkingSet`. Lệnh đầu tiên sau khi dọn chậm hơn khoảng 1 ms (mở lại kết nối). Selftest đo P50/P95 không bị ảnh hưởng vì không có Cockpit.
4. **RAM ở Verify-Task14 E5 đổi sang đọc sau các mẫu CPU** (trạng thái rảnh). Như vậy có hợp ý ADR-09 không?
5. **Nếu E2 vẫn trên 52 MB**, các bước tiếp theo theo rủi ro tăng dần: tắt GC đồng thời, tắt `TieredPGO`, rồi xem phần render WPF. Mỗi bước cần đo lại.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 13a** *(khuyến nghị, làm ngay)* | Claude Code chạy `Verify-Task13a.ps1`, ghi bảng "trước/sau" vào §17.4 | Cần số đo thật trên máy có Cockpit |
| B. Nếu E2 > 52 MB | Thử lần lượt từng bước ở §3 mục 5, mỗi bước đo lại bằng E2 | Kỷ luật thay đổi tối thiểu |
| C. TASK-13 | HUD Micro-Widgets, Right MFD | Chỉ bắt đầu khi đã có dư địa RAM |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-13a** trên nhánh `claude/task-13a-memory-slimming` (tách từ `main` @`54cf26c`). Phần C# đã được **CI GitHub build xanh**; số đo bộ nhớ cần Claude Code chạy `Verify-Task13a.ps1` trên máy Nhật Hòa. Chi tiết ở `gate13a_handoff.md` và `qa_report.md` §17.
>
> Đã làm:
> - Đo Private WS bằng `GetProcessMemoryInfo` EX2 (dự phòng `QueryWorkingSet`). Mã nguồn không dùng `PerformanceCounter` ở đâu, nên giả thuyết Perflib bị loại.
> - `/mem` và pipe `MEM` giải phẫu bộ nhớ.
> - Giảm bộ nhớ: SQLite `cache_size` 512 KB mỗi kết nối, và dọn khi rảnh một lần sau 5 s (đóng pool SQLite rảnh + GC Aggressive có nén, không vòng lặp định kỳ, không `EmptyWorkingSet`). Có công tắc `NEXUS_MEMORY_SLIMMING=0` để E2 tự đo "trước/sau" trên cùng bản build.
> - HUD và `/status` hiện `Private WS: xx.x MB / 60 MB`.
> - `[SLA-WARN SUMMARY]` và `[DRIFT-ALERT]` (> 2 cảnh báo, hoặc cùng metric ở 3 Gate liên tiếp).
>
> Cần bạn quyết:
> - (1) không làm LRU cho AgentRegistry/Vault (chỉ ~0,1–0,2 MB metadata);
> - (2) chưa đổi `UndoLimit` (chỉ có một ô lệnh);
> - (3) dọn khi rảnh bằng GC Aggressive + xóa pool SQLite thay vì `EmptyWorkingSet`;
> - (4) Verify-Task14 E5 đọc RAM sau các mẫu CPU (trạng thái rảnh).
