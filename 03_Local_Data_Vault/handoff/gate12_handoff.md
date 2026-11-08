# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12 + 12b + 12c: Multimodal Vision, tiến trình con "thợ nén", ADR-61)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-09, 03:47–04:12 (nghiệm thu sau TASK-12c). Ca viết code TASK-12c (Claude) làm tới `68e0c3f`. Các lượt trước: §19.4 (Gate 12), §19.6 (TASK-12b).
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude (ca viết code), Antigravity
- **Nhánh**: **`claude/task-12-multimodal-vision`**, tách từ `main` @`149756e`. Đầu nhánh code là **`8477cd2`** (2 bản vá của ca này); sau đó chỉ có commit tài liệu.
- **Đặc tả**: chỉ thị TASK-12c của Gemini; tiêu chí Gate 12 ở `qa_report.md` §19.8
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §19.8 (triển khai) và **§19.9** (kết quả ca này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. CI xanh trên `68e0c3f`; CI cho các commit của ca này chạy sau khi push.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Gate 12 trên máy | Chưa chạy với TASK-12c | 3 lần thử; lần 3: **Task12, Task13a, Task13 đều ALL PASS** |
| 2 | Task13 E2–E4 | Lần 1 FAIL: B1 khôi phục `latest_dispatch.json` ở cuối mọi selftest, nên câu trả lời thử của `widgets` bị xóa trước khi kiểm thật | Bản vá `832fa53`: `NEXUS_SELFTEST_KEEP_LATEST=1` (2 dòng trong `SelfTest.cs`), Verify-Task13 chỉ đặt cho selftest `widgets` và vẫn khôi phục file ở bước cuối |
| 3 | Task12 E5, kiểm HUD | Lần 1–2 FAIL (1,8 / 3,9 MB): so nhãn với lần đọc sau `MEM`, trong khi chính `MEM` làm RAM nhảy một bậc 1–4 MB (probe) | Bản vá `8477cd2`: đọc ngay trước và ngay sau `MEM`; nhãn phải nằm trong đoạn đó ± 0,5 MB |
| 4 | Số đo bộ nhớ | 12b: rảnh 50,8–51,7 MB, đỉnh 65,6 MB | **Rảnh 35,4–36,4 MB, đỉnh `/img` clipboard 51,7–58,6 MB** |
| 5 | Tài liệu | §19.8 "chưa chạy trên máy" | Thêm §19.9; viết lại file này |

Không tắt tiến trình nào bằng tay, không đụng `main` hay tag, không sửa code Cockpit. Trong lúc Verify chạy, Claude không chạy lệnh git nào. Gemini chỉ chạy mock, 0 token. `mfd_pins.json` và chữ trong clipboard đã được khôi phục.

**Sự cố do Claude gây ra:** probe chẩn đoán ở lần thử 2 chạy 6 lệnh `/img` mock trên HostService mà không sao lưu `latest_dispatch.json`. File hiện chứa bản ghi thử `[VISION /img] probe mem2 5`, không còn là dispatch thật gần nhất của Nhật Hòa. Dòng SQLite của probe có `is_test = 1` nên không hiện trong lịch sử. File sẽ đúng lại sau lệnh `/ask` thật kế tiếp.

---

## 2. Trạng thái Gate 12 — ✅ ALL PASS (Task12 + Task13a + Task13, lần thử 3, `8477cd2`)

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-12-multimodal-vision
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task12.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13a.ps1 -SkipBuild
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task13.ps1 -SkipBuild
```
Không chạy lệnh git nào khi Verify đang chạy; không copy/paste; không mở cửa sổ ngân hàng hay trình quản lý mật khẩu trên G272. Lần thử 3 chạy Task12 với `-SkipBuild`, vì bản build của lần 2 đã có sửa đổi C# của `832fa53`; `8477cd2` chỉ sửa script.

### Số đo chính
| Chỉ số | Mục tiêu | Lần 1 / 2 / **3** | Trước đây |
|--------|----------|-------------------|-----------|
| **Đỉnh `/img` clipboard** | ≤ 70 MB | 58,6 / 58,3 / **51,7 MB** | ~145 (Gate 12), 65,6 (12b) |
| **Trung vị rảnh sau 6 ảnh 2K** | ≤ 45,0 MB | 35,6 / 35,4 / **36,4 MB** | 50,8–51,7 (12b) |
| `gdiplus.dll` lúc khởi động / sau tải | vision không nạp thêm | có / có ở cả 3 lần (WPF nạp sẵn) | — |
| **HUD lệch** (Task12) | ≤ 0,5 MB | 1,8 ❌ / 3,9 ❌ (cách cũ) / **0** (34,8 trong [32,1; 36,4]) | — |
| HUD lệch (Task13a) | < 2,0 | 0,0 / 0,0 / **0,0** | 0,1 |
| Tiến trình con | `process` ×6 | ×6 ở cả 3 lần; mỗi lần khoảng 220 ms, đỉnh WS của nó 66,7 MB, rồi thoát | — |
| `vision-mem` (không Cockpit) | ≤ +4,3, chênh ≤ 1 | −1,8 / — / −0,75 MB, chênh 0,2 | +5,8 (12b) |

| Script | E1 | E2 | E3 | E4 | E5 |
|--------|----|----|----|----|----|
| Task12 (lần 3) | ✅ 0/0, 106 file 0 BOM | ✅ JPEG 65 900 B, sha khớp 3 đường, đỉnh 51,7 | ✅ 25/25, selftest P95 2,188 (SLA 2,5), Cockpit 2,602 | ✅ khiên OK, MCP → Cockpit 159,4 ms | ✅ rảnh 36,4, HUD 0, CPU 0 %, tầng nhanh 55,1 s, `Count=0` |
| Task13a (lần 3) | ✅ | ✅ 61,9 → 38,1 MB | ✅ HUD lệch 0,0 | ✅ tầng nhanh 54,1 s | ✅ CPU 0 % |
| Task13 (lần 3) | ✅ | ✅ `/copy` khớp, P95 0,387 ms | ✅ Right MFD 2/5 | ✅ | ✅ 5 ghim, rảnh 32,8 MB |

---

## 3. Điểm mở cần Architect quyết định

1. **Duyệt hai bản vá**:
   - **`832fa53`**: B1 làm selftest `widgets` không còn để lại câu trả lời thử. Biến chỉ dành cho selftest, Cockpit không đổi.
   - **`8477cd2`**: kiểm HUD theo đoạn [trước, sau] lúc xử lý `MEM`. Lưu ý: khi `MEM` gây một bậc lớn, đoạn này rộng (lần 3 rộng 4,3 MB), nên kiểm lỏng hơn một phép so tức thời lý tưởng. Muốn chặt hơn thì cho `MEM` trả cả số đo đọc ngay trước khi dựng JSON.
2. **SLA selftest 2,5 ms của TASK-12c sát nhiễu**: P95 2,233 / **2,537** / 2,188 ms; lần 2 trượt chỉ vì chỉ số này. Giữ 2,5 hay quay về 3,0 (như Cockpit thật, P95 2,6–2,9 ms)?
3. **Task14 E3 `shownAsPassiveHud` trượt một lần** (lần 1): khi bộ đếm hết giờ, Cockpit đang hiện. Nghi vấn: bước khôi phục `latest_dispatch.json` ở cuối selftest `local-timer` làm watcher hiện Cockpit ngay trước phép thử. Lần 2–3 không lặp lại; cần theo dõi.
4. **`latest_dispatch.json` của Nhật Hòa đang là bản ghi thử** (sự cố ở mục 1); sẽ đúng lại sau lệnh `/ask` thật kế tiếp. Đề xuất: mọi probe về sau dùng `Save-NexusLatestDispatch` / `Restore-NexusLatestDispatch`.
5. Các điểm §19.8 của ca viết code vẫn chờ: số phiên bản `v0.14.0`, mỗi ảnh chậm thêm khoảng 180 ms (đo trên máy: tiến trình con 216–281 ms), đánh dấu bản ghi test cũ, làm nóng 3 ảnh trong `vision-mem`, bước CI mới.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Merge Gate 12, gắn `v0.14.0`** *(khuyến nghị)* | Nhật Hòa fast-forward `main` lên nhánh này sau khi CI xanh trên commit cuối, rồi gắn tag | ALL PASS; bộ nhớ vượt mục tiêu (rảnh 36 MB so với 45, đỉnh 52–59 MB so với 70) |
| B. Cockpit UI v2 chặng 1 (TASK-16) | B1–B6 + U1 theo `ui_v2_proposal.md` | Đã có dư địa RAM khoảng 24 MB tới trần 60 MB |
| C. Dọn nhánh thử | Nhật Hòa xóa `claude/task-12-ci-probe` trên GitHub | Chỉ dùng để chạy selftest trên runner |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 12 sau TASK-12c đã ALL PASS**: Verify-Task12, Verify-Task13a và Verify-Task13 đều đạt ở lần thử 3 trên máy Nhật Hòa (Claude Code, 03:47–04:12), nhánh `claude/task-12-multimodal-vision` @`8477cd2`. Chi tiết ở `gate12_handoff.md` và `qa_report.md` §19.9.
>
> - **Đỉnh `/img` clipboard: 51,7–58,6 MB** (mục tiêu 70; ban đầu ~145).
> - **Trung vị RAM rảnh sau 6 ảnh 2K: 35,4–36,4 MB** (mục tiêu 45; TASK-12b 50,8–51,7). Tiến trình con chạy ×6, khoảng 220 ms mỗi ảnh, đỉnh WS của nó 66,7 MB rồi thoát. `gdiplus.dll` có sẵn từ lúc khởi động (WPF), vision không nạp thêm.
> - **HUD lệch: 0** (Task12, nhãn 34,8 trong [32,1; 36,4]); Task13a 0,0 MB.
> - Hai bản vá script, không sửa code Cockpit:
>   - `832fa53`: B1 khôi phục `latest_dispatch.json` ở cuối mọi selftest, làm mất câu trả lời thử của `widgets` (Task13 E2–E4 FAIL). Thêm `NEXUS_SELFTEST_KEEP_LATEST=1` chỉ cho selftest đó.
>   - `8477cd2`: chính `MEM` làm RAM nhảy 1–4 MB, nên kiểm HUD nay so với đoạn đọc trước/sau `MEM`.
> - Các số khác lần 3: build 0/0, 106 file 0 BOM; cache 25/25, selftest P95 2,188 ms (SLA 2,5), Cockpit 2,602; khiên OK, MCP → Cockpit 159 ms; tầng nhanh 55,1 s, `[SLA-WARN SUMMARY] Count=0`.
> - Sự cố: probe chẩn đoán của Claude Code làm `latest_dispatch.json` của Nhật Hòa thành bản ghi thử (dòng SQLite `is_test=1`); file sẽ đúng lại sau lệnh `/ask` thật kế tiếp.
>
> Cần bạn quyết:
> - (1) duyệt hai bản vá;
> - (2) SLA selftest 2,5 ms sát nhiễu (lần 2: 2,537 ms) — giữ hay quay về 3,0;
> - (3) Task14 E3 `shownAsPassiveHud` trượt một lần (nghi do B1 khôi phục file ngay trước phép thử);
> - (4) duyệt merge `main` + tag `v0.14.0` (Nhật Hòa thực hiện), rồi mở TASK-16 (UI v2).
