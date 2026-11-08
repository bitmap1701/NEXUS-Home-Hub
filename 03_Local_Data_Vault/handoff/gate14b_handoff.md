# 📦 HỒ SƠ BÀN GIAO — Gate 14b (TASK-14b: ADR-55, ADR-56, gia cố a/b/c)

- **Ca thực thi**: Claude, 2026-11-08 (ca tối, sau khi Gemini đóng Gate 14 / `v0.12.0` @`f354f97`)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude Code trên máy Nhật Hòa
- **Nhánh**: **`claude/task-14b-ci-fast-regression`**, tách từ `main` @`089abcb`
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-14b)
- **Chi tiết kỹ thuật**: `qa_report.md` §16
- **Trạng thái build**: **chưa build** trong môi trường của Claude. CI GitHub đã được kích hoạt nhưng **job không chạy** vì tài khoản GitHub bị khóa do vấn đề thanh toán. Không có số đo trong file này.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | CI (ADR-56) | Không có | `.github/workflows/nexus-ci.yml`: push `main`/`claude/**`/`docs/**`, PR vào `main`; `windows-latest`, .NET 8; cổng 0 BOM → build `-warnaserror` → smoke McpTools |
| 2 | Cổng 0 BOM toàn repo | Chỉ quét một số thư mục | `Test-NexusBom.ps1` quét `git ls-files`; `.ps1` phải có BOM, file khác không |
| 3 | 10 file `.gitkeep` | Mỗi file chứa BOM → cổng toàn repo sẽ trượt ngay | Để rỗng 0 byte |
| 4 | Smoke không màn hình | Không có | `Invoke-CiSmoke.ps1`: 7 yêu cầu JSON-RPC tới McpTools, Gemini giả lập |
| 5 | Hồi quy hai tầng (ADR-55) | Chỉ có chuỗi đầy đủ (~12 phút, có gọi Gemini thật) | `Invoke-NexusRegression.ps1 -FastRegression` (Gate 14 + Task11 không mạng + Task06, mục tiêu < 90 s) / `-FullRegression` (như cũ); `Verify-Task14.ps1` nhận cùng hai cờ |
| 6 | (a) Chốt Git | Chỉ kiểm `git status` ở cuối | Kiểm nhánh + HEAD sau mỗi bước; lệch thì dừng ngay và ghi bước lệch vào report |
| 7 | (b) Nhịp lắng CPU | Lấy mẫu ngay sau bài test | E5: hủy mọi bộ đếm, chờ 2 000 ms; selftest timer: chờ 1 500 ms, lấy mẫu 2 s |
| 8 | RAM | Chỉ trần 60 MB | Báo thêm ngưỡng mềm 54 MB (vàng, không trượt) |
| 9 | (c) Tài liệu | Tag ghi "trỏ sai"; ADR-55/56 "chốt hướng" | `gate14_handoff.md` ghi tag đã gắn lại tại `f354f97`; ADR-55, 56, 59, 09 cập nhật |
| 10 | Nghiệm thu | — | `Verify-Task14b.ps1` (E1–E5) |

---

## 2. Trạng thái Gate 14b — ⏳ CHƯA ĐỦ BẰNG CHỨNG (chưa build, CI chưa chạy được)

### Lệnh chạy (Claude Code hoặc Nhật Hòa trên máy)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-14b-ci-fast-regression
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task14b.ps1
```
Trước khi chạy: tắt game và ứng dụng nặng; **không chạy lệnh git nào trong repo khi Verify đang chạy** (chốt Git sẽ dừng lượt chạy). Lượt này không gọi Gemini thật (tầng nhanh dùng `-SkipLive`).

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + 0 BOM toàn repo | `dotnet build -c Release -warnaserror` 0/0; `Test-NexusBom` 0 vi phạm trên mọi file `git ls-files` (gồm `nexus-ci.yml`, `gate14_handoff.md`, `ADR_REGISTRY.md`, `current_task.md`) | Chưa chạy |
| E2 | Chốt Git | Hàm so sánh bắt đúng HEAD đổi, nhánh đổi, cây bẩn trên dữ liệu giả; nhánh + HEAD thật không đổi suốt lượt chạy, cây sạch | Chưa chạy |
| E3 | Tầng nhanh | `Invoke-NexusRegression -FastRegression -SkipBuild` **< 90 s**; `task14`, `task11`, `task06` đều `allPassed`; không gọi Gemini | Chưa chạy |
| E4 | CPU + RAM | CPU rảnh của Cockpit sau nhịp lắng **< 0,5 %**; selftest timer < 1 %; Private WS < 60 MB (≥ 54 MB thì báo vàng) | Chưa chạy |
| E5 | CI | `nexus-ci.yml` đủ trigger, runner, .NET 8, ba bước BOM / build / smoke; lượt chạy trên GitHub xanh | File: Claude đã kiểm cú pháp YAML. Lượt chạy: **chưa chạy được** (tài khoản bị khóa thanh toán) |

---

## 3. Điểm mở cần Architect quyết định

1. **`.ps1` bắt buộc có BOM trong cổng CI**, trái với câu "mọi file không BOM" của đặc tả. Nếu theo đúng câu chữ, cả 12 script hiện có trượt, và Windows PowerShell 5.1 sẽ đọc sai tiếng Việt. Xin duyệt quy tắc hai chiều.
2. **CI build từng `.csproj` thay vì solution** (`.slnx` cần SDK 9.0.200+, đặc tả chọn SDK 8). Máy Nhật Hòa vẫn build `.slnx`.
3. **CI chỉ chạy smoke McpTools**, không chạy selftest HostService (cần dữ liệu thật và có ngưỡng ms, không hợp với máy chủ dùng chung).
4. **Không ép `EmptyWorkingSet` trước phép đo RAM**, để con số phản ánh trạng thái chạy thật. Có thể thêm một phép đo phụ "sau khi thu hồi" nếu Architect muốn.
5. **Tài khoản GitHub bị khóa thanh toán** nên Actions không chạy. Cần Nhật Hòa xử lý trong Settings → Billing and plans trước khi E5 có lượt xanh. Lưu ý thêm: repo riêng tư chỉ có số phút Actions miễn phí giới hạn mỗi tháng.
6. Ô thứ 3 của `/ground` được ghi dưới ADR-59 (tham chiếu ADR-45b), không dưới ADR-50 như đặc tả, vì ADR-50 là quy tắc đếm token.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy Gate 14b** *(khuyến nghị, làm ngay)* | Claude Code chạy `Verify-Task14b.ps1`; sửa tối thiểu nếu lỗi; ghi §16.4 | Code chưa build lần nào |
| B. Mở lại GitHub Actions | Nhật Hòa xử lý thanh toán; bấm Re-run trên tab Actions hoặc push lại | E5 cần một lượt CI xanh; sau đó mọi nhánh Claude đẩy lên đều được biên dịch tự động |
| C. TASK-13 | HUD Micro-Widgets, Right MFD, nhãn Private WS | Bước tiếp theo theo ADR-58. Private WS đang ~55 MB, sát ngưỡng mềm 54 MB, nên cần ngân sách RAM rõ cho widget |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-14b** trên nhánh `claude/task-14b-ci-fast-regression` (tách từ `main` @`089abcb`). **Chưa build**: môi trường Claude không có .NET SDK. **CI đã được kích hoạt nhưng GitHub không chạy job** vì tài khoản bị khóa do vấn đề thanh toán. Gate 14b cần Claude Code chạy `Verify-Task14b.ps1` trên máy Nhật Hòa. Chi tiết ở `gate14b_handoff.md` và `qa_report.md` §16.
>
> Đã làm:
> - ADR-56: `.github/workflows/nexus-ci.yml` (cổng 0 BOM trên `git ls-files`, build HostService + McpTools `-warnaserror` trên .NET 8, smoke McpTools JSON-RPC với Gemini giả lập).
> - ADR-55: `Invoke-NexusRegression.ps1 -FastRegression` (Gate 14 + Task11 không mạng + Task06, mục tiêu < 90 s) / `-FullRegression`.
> - (a) chốt nhánh + HEAD sau mỗi bước; (b) nhịp lắng CPU 2 s ở E5 và 1,5 s ở selftest timer; (c) tài liệu tag `v0.12.0` và ADR-09/55/56/59.
> - Phát hiện thêm: 10 file `.gitkeep` chứa BOM, đã để rỗng.
>
> Cần bạn quyết:
> - (1) cổng CI bắt `.ps1` **phải có** BOM (trái câu chữ đặc tả, vì PowerShell 5.1 cần BOM cho tiếng Việt);
> - (2) CI build từng `.csproj` vì `.slnx` cần SDK 9.0.200+;
> - (3) CI chỉ chạy smoke McpTools, không chạy selftest HostService;
> - (4) không ép thu hồi bộ nhớ trước phép đo RAM;
> - (5) ghi ô thứ 3 `/ground` dưới ADR-59 thay vì ADR-50.
