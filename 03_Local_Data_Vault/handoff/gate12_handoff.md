# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12 + TASK-12b: Multimodal Vision `/img` & `/snap`, ép bộ nhớ đường ảnh, ADR-61)

- **Ca thực thi**: Claude, 2026-11-09 (sau khi Gemini duyệt 5 điểm TASK-12 và commit đặc tả TASK-12b @`b25f6c0`). Ca trước (Claude Code trên máy Nhật Hòa, 01:59–02:13) chạy Gate 12 lượt 1–3 ở `3a7b00f`, kết quả giữ nguyên ở `qa_report.md` §19.4.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude Code trên máy Nhật Hòa
- **Nhánh**: **`claude/task-12-multimodal-vision`**, tách từ `main` @`149756e`. TASK-12b làm ngay trên nhánh này, trước khi merge.
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-12b)
- **Chi tiết kỹ thuật**: `qa_report.md` §19.5
- **Trạng thái build**: C# **CI GitHub build xanh**. Selftest `vision`, `vision-guard`, `vision-mem` và các hàm đo mới (Windows PowerShell 5.1) **PASS trên runner Windows của CI**. `Verify-Task12/13a/13.ps1` trên máy Nhật Hòa **chưa chạy** với code mới.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | `/img` từ clipboard | Chép khối PNG/DIB ra `byte[]` rồi chép tiếp khi bọc BMP (2 mảng 11–15 MB trên LOH); probe §19.4: đỉnh 144,8 MB, managed 92 MB | Giải mã tại chỗ từ khối `GlobalLock` qua `UnmanagedMemoryStream` (CF_DIB: thêm 14 byte header, không chép điểm ảnh); clipboard chỉ mở trong lúc giải mã + thu nhỏ |
| 2 | Xuất JPEG | `MemoryStream` mới cho mỗi lần thử + `ToArray()` | Một `MemoryStream` cho mọi lần thử, kết quả là `ReadOnlyMemory` trên bộ đệm của nó (`TryGetBuffer`); SHA-256 và Base64 đọc từ span |
| 3 | Ảnh nguồn lớn | Nhận tới 16 384 px | `maxSourceDimensionPx` = 4096 (cấu hình được): PNG/BMP bị từ chối từ header, trước khi GDI+ nhận stream → `[VISION ERROR] Source image dimensions exceed 4096px safety limit` |
| 4 | Sau mỗi `/img` / `/snap` | Chờ lần dọn khi rảnh 5 s | `CompactAfterVision`: LOH `CompactOnce` + GC gen2 ngay trong tác vụ nền (khoảng 1,2 ms trên runner) |
| 5 | Đo đỉnh | Không có | Trong tiến trình: mẫu 100 ms cho mỗi lần (`VISIONSTATE.lastBurst`, MCP `burst`); ngoài tiến trình: `Measure-NexusPeakPrivateWs` trong Verify-Task12 E2 |
| 6 | Hàm đo RAM | 3 bản `Get-PrivateWsMb` (WMI, trễ ~1 s) + 3 kiểu chờ khác nhau | `NexusVerify.Common.ps1`: Immediate (EX2, dự phòng WMI), Settled Median (lần dọn ≥ 10 s, trung vị 8 lần đọc), Peak Sampler (~100 ms). Gate 13a E3 so nhãn HUD với lần đọc Immediate |
| 7 | Cổng E5 của Gate 12 | ≤ 52,0 MB (3 lần đọc bằng nhau) | Trung vị ≤ **45,0 MB** + selftest `vision-mem` (không tăng theo từng ảnh) |
| 8 | Thử nghiệm bỏ đi | — | `HeapCompact` trên heap của tiến trình: không đổi số đo trên runner, đã gỡ |

---

## 2. Trạng thái Gate 12 — ⏳ CHƯA ĐỦ BẰNG CHỨNG cho TASK-12b (CI xanh, 3 selftest PASS trên runner; chưa chạy lại trên máy)

### Lệnh chạy (cả ba bộ, theo thứ tự)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-12-multimodal-vision
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task12.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13a.ps1 -SkipBuild
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1 -SkipBuild
```
Không chạy lệnh git nào khi Verify đang chạy; không copy/paste; không mở cửa sổ ngân hàng hay trình quản lý mật khẩu trên G272. Gemini chỉ chạy mock, 0 token. Ba bộ mất khoảng 12–15 phút.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM + CI | `-warnaserror` 0/0; 0 vi phạm BOM; CI xanh | Chưa chạy (CI: 0/0, 0 BOM, `tools/*.ps1` parse sạch) |
| E2 | Nén không chép + đỉnh | 2K → JPEG ≤ 250 KB; `zeroCopyOk`, `sourceLimitOk`; đỉnh Private WS (mẫu ~100 ms) quanh `/img` clipboard và `--file` được ghi, giảm rõ so với ~145 MB (mềm 120 / 200, không chặn) | Chưa chạy (runner: PNG và CF_DIB không chép cho cùng sha `07e90604…`; giới hạn 4096 px hoạt động; đỉnh headless 40,4 MB) |
| E3 | Vision cache | 25 lần: P50 ≤ 2,0 / P95 ≤ 3,0 ms, rõ hơn mức 2,988 cũ; 0 token | Chưa chạy (runner P50 0,693 / P95 0,857 ms) |
| E4 | Khiên + MCP | Như Gate 12 | Chưa chạy (runner: combat P95 0,134 ms, privacy chặn `Password`) |
| E5 | RAM rảnh + chéo Gate | Trung vị 8 lần đọc sau lần dọn ≥ 10 s **≤ 45,0 MB**; `vision-mem` PASS; CPU < 0,5 %; tầng nhanh có `[SLA-WARN SUMMARY]`; Verify-Task13a và Verify-Task13 ALL PASS với hàm đo chung | Chưa chạy (runner `vision-mem`: tăng 3,6 MB rồi phẳng, 3 lần cuối chênh 0,2 MB; managed 1,04 MB sau mỗi lần) |

---

## 3. Điểm mở cần Architect quyết định

1. **Bậc native khoảng 2,7 MB của GDI+.** Trên runner, bộ nhớ native tăng một lần ở ảnh 2K thứ 3 rồi đứng yên; managed và LOH đã sạch. Nếu trên Cockpit thật mức rảnh vẫn > 45 MB, phần còn lại là native: bước kế tiếp sẽ là giải mã theo dòng qua COM `IWICBitmapScaler` (đặc tả để dành), không phải nén GC thêm.
2. **`vision-mem` chấm "không tăng theo từng ảnh"** (3 lần cuối chênh ≤ 1,0 MB), còn mức tăng sau phiên (3,6 MB trên runner, mục tiêu 4,3 = 45,0 − 40,7) chỉ ghi lại. Lý do: tiến trình headless không phải Cockpit WPF; cổng thật là trung vị trên Cockpit ở E5 (≤ 45,0).
3. **Clipboard bị giữ mở trong lúc giải mã + thu nhỏ** (cái giá của việc không chép). Thời gian giữ được ghi ở `lastBurst.ClipboardHoldMs`. Ảnh nguồn ≤ 4096 px nên thời gian này có giới hạn.
4. **Hàm Immediate đọc `GetProcessMemoryInfo` EX2 từ ngoài tiến trình** thay vì WMI (trễ ~1 s), WMI làm dự phòng. Gate 13a đã kiểm chứng hai nguồn trùng nhau đến 0,1 MB; runner cũng cho kết quả trùng (37,9 / 37,9 MB).
5. **Gate 13a E3** so nhãn HUD với lần đọc Immediate ngay sau `MEM` (đúng đặc tả), không so với trung vị.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Chạy lại Gate 12 + 13a + 13** *(khuyến nghị)* | Claude Code chạy ba lệnh ở mục 2; sửa tối thiểu nếu lỗi; ghi §19.6 | Code đã build và selftest đã PASS trên runner; mức rảnh trên Cockpit thật và đỉnh `/img` clipboard chỉ đo được trên máy |
| B. Merge + tag `v0.14.0` | Nhật Hòa fast-forward `main` sau khi Gemini duyệt kết quả, rồi gắn tag | Theo đặc tả TASK-12b |
| C. Dọn nhánh thử | Nhật Hòa xóa `claude/task-12-ci-probe` trên GitHub | Chỉ dùng để chạy selftest trên runner |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai **TASK-12b** trên nhánh `claude/task-12-multimodal-vision` (trước khi merge). C# **CI xanh**; ba selftest `vision`, `vision-guard`, `vision-mem` và các hàm đo mới (Windows PowerShell 5.1) **PASS trên runner Windows của CI**. Mức rảnh trên Cockpit thật cần Claude Code chạy lại Verify-Task12, 13a, 13 trên máy Nhật Hòa. Chi tiết ở `gate12_handoff.md` và `qa_report.md` §19.5.
>
> 4 tối ưu:
> - (1) `/img` clipboard giải mã tại chỗ từ khối `GlobalLock` qua `UnmanagedMemoryStream` (CF_DIB thêm 14 byte header, không chép điểm ảnh);
> - (2) JPEG ghi vào một `MemoryStream` dùng lại, kết quả là `ReadOnlyMemory` qua `TryGetBuffer`, không `ToArray`;
> - (3) `maxSourceDimensionPx` = 4096, PNG/BMP bị từ chối từ header;
> - (4) LOH `CompactOnce` + GC gen2 ngay sau mỗi `/img` / `/snap`.
>
> Hàm đo chung trong `NexusVerify.Common.ps1`: Immediate (EX2, dự phòng WMI), Settled Median (lần dọn ≥ 10 s, trung vị 8 lần đọc), Peak Sampler (~100 ms). Verify-Task12 E5 nay chấm ≤ 45,0 MB; Gate 13a E3 so nhãn HUD với lần đọc Immediate.
>
> Số tham khảo trên runner (headless, không phải số nghiệm thu): sau mỗi lần nén, managed chỉ còn 1,04 MB; tăng 3,6 MB sau phiên rồi phẳng (3 lần cuối chênh 0,2 MB, là bậc native một lần của GDI+); đỉnh 40,4 MB; cache P95 0,857 ms. `HeapCompact` đã thử, không có tác dụng, đã gỡ.
>
> Cần bạn quyết:
> - (1) nếu mức rảnh trên máy vẫn > 45 MB thì phần còn lại là native, bước kế là COM `IWICBitmapScaler`;
> - (2) `vision-mem` chấm "không tăng theo từng ảnh", mức tăng chỉ ghi lại;
> - (3) clipboard giữ mở trong lúc giải mã + thu nhỏ;
> - (4) hàm Immediate dùng EX2 thay WMI.
