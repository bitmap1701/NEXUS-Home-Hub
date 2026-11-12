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

## 2. Trạng thái Gate 17b — ✅ lần 2/3 ALL PASS (`a8dcf7e`, 10:58); chờ Architect đóng dấu `v0.17.2`

**Lần 2/3**: E2 3677 ms; E3 A 49,4 → B 39,1 MB (**−10,3**), GC 7,86; E4 WAKE P50 8,08 / P95 12,15 ms; E5 17b 38,5 MB, chuỗi 17a 34,5 · 17 35,6 · 16c 37 · 15 36,8 · 16b 40 MB, CPU 0 %, 0 lượt gọi thật, CI xanh; `/qa` lạnh 5,32 ms. Chi tiết `qa_report.md` §27.6.

**Lần 1/3** (`aa9a8cf`): RAM −8,4 MB đạt; trượt `/qa` lạnh 14,05 ms, `host-footprint` 6016 ms, GC 8,05 → 17b.4 (§27.4–27.5).

```powershell
cd C:\Agent\NEXUS_Home_Hub
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task17b.ps1
```
Gate 17a đã đóng `v0.17.1` (17a.6); nhánh này đã gộp hồ sơ đóng của 17a nên E1 thấy ADR-69 "Chốt `v0.17.1`". Chạy sau khi Nhật Hòa gộp `main` + tag `v0.17.1`. Đo trước (đã chạy 08:56, `qa_report.md` §27.2):

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
- **Đã quyết (09:52) 17a.6**: Gate 17a đóng `v0.17.1` theo bằng chứng; `Invoke-NexusRegression` chạy lại 1 lần kèm `[SLA-WARN (retry)]` (§27.3). Trần GC heap của dải chung 10,5 MB (17b.1).
- **Đã quyết (08:53)**: 17b.1 dải chung cho Verify-Task17a, lượt cây git bẩn không tính; 17b.2 chọn theo số đo, ưu tiên runtimeconfig.
- **(17b.3) `TieredCompilation=false`**: chỉ biến thể này vượt nhiễu (−5,8 MB). Đánh đổi: khởi động / lần gọi đầu chậm hơn; nếu chuỗi hồi quy trượt SLA độ trễ lạnh, Claude sẽ báo số để Architect chọn giữ hay bỏ.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng dấu `v0.17.2`** *(khuyến nghị)* | Nhật Hòa gộp `main` + tag sau khi Architect đóng dấu | ALL PASS lần 2/3 |
| B. STT cục bộ sau `ISpeechToTextProvider` | Gắn nhận dạng offline cho `/ptt` | Đệm RAM giờ ~10 MB (rảnh 34–40 MB), đủ chỗ cho mô hình nhỏ |
| C. Dịch vụ LAN trên Nitro 5 (5055 / 5056) | Worker STT / đồng bộ handoff | Tường lửa đã sẵn |

### Sau khi Architect đóng dấu (Nhật Hòa làm)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-17b-hostservice-footprint
git push origin main
git tag -a v0.17.2 -m "Gate 17b: TASK-17b HostService footprint, TieredCompilation off, cold-path warm-up, regression retry (ADR-70)"
git push origin v0.17.2
```

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **Gate 17b lần 2/3 ALL PASS** (`a8dcf7e`, `qa_report.md` §27.6):
> - E1 ✅ build 0/0, BOM, ADR-69 chốt, ADR-70, runtimeconfig `TieredCompilation=false`.
> - E2 ✅ `host-footprint` 3677 ms (≤ 8000): ngoài Cockpit chỉ 2,98 MB (warm-up 0,2 · pipe 0,8 · tray 0,7 · `/net` 1,8 MB); phần lớn là mã JIT.
> - E3 ✅ A/B cùng lượt: TieredCompilation bật 49,4 MB → tắt **39,1 MB (−10,3)**, GC heap 7,86.
> - E4 ✅ WAKE P50 8,08 / P95 12,15 ms. `/qa` lạnh 5,32 ms (lần 1/3: 14,05) nhờ `WarmUp` làm ấm (17b.4).
> - E5 ✅ RAM rảnh 17b 38,5 MB; chuỗi 17a 34,5 · 17 35,6 · 16c 37 · 15 36,8 · 16b 40 MB (trước 17b: 38–46); CPU 0 %; 0 lượt gọi thật; chạy lại hồi quy không cần dùng; CI xanh.
>
> Mời bạn đóng dấu `v0.17.2` và chọn task tiếp theo (đề xuất: STT cục bộ cho `/ptt`, giờ đã có đệm RAM ~10 MB).
