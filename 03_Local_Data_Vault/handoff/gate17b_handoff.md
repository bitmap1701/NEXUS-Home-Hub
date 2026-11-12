# 📦 HỒ SƠ BÀN GIAO — Gate 17b (TASK-17b, ADR-70 → `v0.17.2`): chẩn đoán & thu gọn bộ nhớ nền HostService đầy đủ

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa (đo trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-17b-hostservice-footprint`, tách từ đầu nhánh TASK-17a (`c97cd1f`) vì `v0.17.1` chưa chốt (`main` = `v0.17.0` @`aaa5ec7`)
- **Chi tiết kỹ thuật**: `qa_report.md` §27
- **Trạng thái build**: Claude **chưa build**; CI Windows runner build `-warnaserror` + selftest `host-footprint`. Chưa có số đo nào trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Đo RAM HostService | Chỉ `ui-footprint` (cửa sổ Cockpit) | Selftest `host-footprint`: 12 pha theo đúng thứ tự `Program.Main`, mỗi phần một dòng (Private WS, GC heap, commit, paged / non-paged pool, JIT, luồng, handle), ≤ 5000 ms |
| 2 | Pipe | Tên cố định | `CockpitPipeServer` nhận tên pipe (selftest dùng tên riêng, không đụng host thật) |
| 3 | Đo trước | — | `Measure-HostFootprint.ps1`: 6 biến thể runtime .NET (TieredPGO, gcConcurrent, TieredCompilation, GCgen0size, kết hợp) trên selftest và HostService thật |
| 4 | Dải RAM `[SLA-WARN]` | Mỗi Verify tự so ≤ 45,0 | `Test-NexusIdleRam` dùng chung cho E5 của Verify-Task12 / 15 / 16 / 16b / 16c / 17 / 17a (cũng có trên nhánh 17a, `c97cd1f`) |
| 6 | Thu gọn | — | `<TieredCompilation>false</TieredCompilation>` trong csproj HostService (đo PC: 37,1 → 31,3 MB, GC heap 6,3–8,0) |
| 7 | Nghiệm thu | — | `Verify-Task17b.ps1` E1–E5 (E3 đo A/B trong cùng lượt) |
| 5 | Sổ cái | ADR-70 trống | ADR-70 đang triển khai (`v0.17.2`), trống kế tiếp ADR-71; ROADMAP thêm TASK-17b |

---

## 2. Trạng thái Gate 17b — ⏳ bước 2 xong (mã + Verify); chờ `v0.17.1` chốt rồi chạy lần 1/3

```powershell
cd C:\Agent\NEXUS_Home_Hub
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task17b.ps1
```
Chạy sau khi Gate 17a đóng dấu `v0.17.1` và `main` được gộp vào nhánh này (E1 kiểm ADR-69 "Chốt `v0.17.1`"). Đo trước (đã chạy 08:56, `qa_report.md` §27.2):

```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-17b-hostservice-footprint
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Measure-HostFootprint.ps1
```
Khoảng 5 phút, để máy rảnh; script tắt rồi bật lại HostService. Kết quả: bảng trên màn hình + `task17b_footprint_variants.json` (git bỏ qua).

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build `-warnaserror` 0/0, Zero-BOM, ADR-70 có, ADR-69 chốt `v0.17.1` | ⏳ chờ `v0.17.1` |
| E2 | `--selftest host-footprint` bảng phân rã từng phần, ≤ 5000 ms | ⏳ PC đo trước 3188–3960 ms |
| E3 | Tải giống Task16b + `[MEM]` trên HostService đầy đủ: GC heap ≤ 8,0 MB, rảnh ≤ 46,7 MB (49,7 − 3,0); A/B báo cáo | ⏳ đo trước: 31,3 MB, GC 6,28 |
| E4 | WAKE P50 ≤ 10,0 / P95 ≤ 16,0 ms | ⏳ |
| E5 | RAM rảnh Task17b + Task16b ≤ 45,0 (`[SLA-WARN]` ≤ 50,0 khi GC heap ≤ 8,0), CPU 0 %, chuỗi `-SkipLive` ALL PASS, `zeroLiveCalls` | ⏳ dải đã có |

---

## 3. Điểm mở cần Architect quyết định

- **(17b.1) Thứ tự với Gate 17a**: đặc tả 17b dựa trên `v0.17.1`, nhưng Gate 17a còn ở lần 1/3 FAIL (17a.4 / 17a.5 chưa trả lời). Đề xuất: áp dụng dải `[SLA-WARN]` của E5 17b cho Verify-Task17a lần 2/3 (đã có trên nhánh 17a), chốt `v0.17.1`, rồi gộp `main` vào nhánh 17b. Lần 1/3 của 17a có tính vào cầu chì không?
- **Đã quyết (08:53)**: 17b.1 dải chung cho Verify-Task17a, lượt cây git bẩn không tính; 17b.2 chọn theo số đo, ưu tiên runtimeconfig.
- **(17b.3) `TieredCompilation=false`**: chỉ biến thể này vượt nhiễu (−5,8 MB). Đánh đổi: khởi động / lần gọi đầu chậm hơn; nếu chuỗi hồi quy trượt SLA độ trễ lạnh, Claude sẽ báo số để Architect chọn giữ hay bỏ.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đo `Measure-HostFootprint` trên PC** *(khuyến nghị)* | Lệnh ở mục 2 | "Đo trước": TASK-17a cho thấy đoán thì không giảm được gì |
| B. Bước 2 TASK-17b | Ghi biến thể thắng + `Verify-Task17b.ps1` (E1–E5) | Sau khi có số và quyết định 17b.1 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. Đã nhận đặc tả TASK-17b. Đối chiếu repo: `v0.17.1` **chưa chốt** (`main` = `v0.17.0` @`aaa5ec7`), nên nhánh `claude/task-17b-hostservice-footprint` tách từ đầu nhánh 17a (`c97cd1f`).
> - Bước 1 (đo trước) xong: selftest `host-footprint` (12 pha theo thứ tự `Program.Main`, pipe test tên riêng, ≤ 5000 ms) và `Measure-HostFootprint.ps1` so 6 biến thể runtime .NET (TieredPGO, gcConcurrent, TieredCompilation, GCgen0size, kết hợp) trên HostService thật. MCP là tiến trình riêng nên không nằm trong bảng.
> - Dải E5 `[SLA-WARN]` (≤ 45,0 PASS; ≤ 50,0 khi GC heap ≤ 8,0) đã vào hàm chung `Test-NexusIdleRam` cho mọi Verify, có cả trên nhánh 17a.
>
> Mời bạn quyết: **(17b.1)** cho Verify-Task17a lần 2/3 dùng dải này để chốt `v0.17.1` trước không, và lần 1/3 (lỗi cây git) có tính vào cầu chì không; **(17b.2)** đồng ý chọn cách thu gọn theo số đo (ưu tiên chỉ sửa runtimeconfig trong csproj) không.
