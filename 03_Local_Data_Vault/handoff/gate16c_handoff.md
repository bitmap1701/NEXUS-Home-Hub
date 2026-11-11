# 📦 HỒ SƠ BÀN GIAO — Gate 16c (TASK-16c: Cockpit UI v2 phần nhìn theo 6 bảng thiết kế, ADR-68 → `v0.16.5`)

- **Ca thực thi**: Claude, 2026-11-11
- **Gửi tới**: Claude Code trên máy Nhật Hòa (chạy Verify), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-16c-cockpit-ui-v2-visual`**, tách từ `main` @`60322e2` (`v0.16.0`). Đặc tả: `current_task.md` (`1763b81`)
- **Chi tiết kỹ thuật**: `qa_report.md` §24
- **Trạng thái build**: Claude **chưa build** trên máy mình; CI (Windows runner) xanh ở `dd2e93c`. Verify lần 1/3 (`99eb019`) **FAIL** E4 + E5; bản vá cho lần 2/3 dựa trên số đo trên máy Nhật Hòa (`Measure-CockpitFootprint.ps1`, không tính lượt cầu chì).

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Bố cục | Terminal 3 cột `LEFT MFD \| CENTER \| RIGHT MFD` | 2 cột: hội thoại `*` + cột phải 300 px (`THẺ ĐÃ GHIM n/5`, Hẹn giờ, `LIÊN MINH`) |
| 2 | Thanh đỉnh | Dòng chữ trạng thái | 44 px: chấm + `NEXUS COCKPIT` + `COMMAND`/`PASSIVE`, chip RAM / CPU / WAL, `Gọn · Chuẩn · Cao`, `Xuyên thấu`, `F1` |
| 3 | Mức 40 px | Phần trên của giao diện cũ | Một dòng `● NEXUS · RAM … · trạng thái`. Vẫn cắt thân như Gate 16b, `Window.Height` không đổi |
| 4 | Hỏi–đáp | Danh sách lịch sử ở LEFT MFD + khung trả lời | Chuỗi thẻ ≤ 20 mục (Card Pool 20 view), dòng `/calc` có nhãn `cục bộ · 0 token`, thẻ mở có nút Copy / Ghim / Hỏi tiếp / Chạy lại + chip 1–4 |
| 5 | Đang chạy | Không có tiến trình, không hủy được | Viền xanh, đồng hồ giây (timer chỉ tồn tại khi có yêu cầu bay), 4 bước, **`Hủy Esc`** = `CancellationTokenSource` → `[ĐÃ HỦY]`, bỏ phản hồi đến muộn, 0 token |
| 6 | Ô lệnh | Ô chữ trơn | `NEXUS›`, bảng `/` (VỪA DÙNG + TẤT CẢ, 18 lệnh, lọc, Tab) và bảng `@` (lọc registry, Tab → `@<agentId> `). `@<agentId> việc` giao đúng agent |
| 7 | Chế độ lười | Chỉ có phím | Thẻ VIỆC TIẾP THEO (Alt+Enter) + sơ đồ 4 bước, thẻ ảnh clipboard (Enter / 2 / 3, kiểm một lần mỗi lần đánh thức) |
| 8 | `Alt+A` | Khối chữ | Bảng: Gate + huy hiệu, E1–E5, VIỆC KẾ TIẾP, `Alt+G` lời nhắn Gemini, `Alt+K` prompt Claude Code, ĐÃ ĐÓNG (sắp theo số), THÀNH VIÊN, BẢNG TIN |
| 9 | 6 công tắc nhanh | Cột phải | Đáy bảng F1 (giữ tên, handler, trường `UISTATE`) |
| 10 | `Ctrl+Shift+Space` | Tự tick ô Mic | Không bao giờ đụng tới micro |
| 11 | Phông chữ | Consolas | Segoe UI Variable + Cascadia Mono có sẵn trong Windows, 0 byte nhúng |
| 12 | Kiểm thử | — | Selftest `ui-v2-visual` (CI) + `Verify-Task16c.ps1` (E1–E5); ADR-68 vào sổ (số trống kế tiếp ADR-69), ROADMAP thêm TASK-16c |
| 13 | RAM chuỗi thẻ (sau lần 1) | 20 thẻ luôn hiện, 20 câu trả lời luôn vẽ sẵn markdown | Hiện 8 thẻ mới nhất + dòng `▲ n thẻ cũ hơn` (bấm / ↑ / mở câu cũ → đủ 20), vẽ sẵn 8 câu; về PASSIVE / ẩn thì thu lại và giải phóng phần thừa |
| 14 | Bảng ẩn (sau lần 1) | Bảng F1 / Alt+A, bảng `/ @`, thẻ lười / ảnh khi đóng vẫn nằm trong cây giao diện | Rời cây giao diện khi ẩn, gắn lại đúng chỗ khi mở (mọi nơi vẫn chỉ đặt `Visibility`) |
| 15 | Cache hit chậm dần (sau lần 1, ngoài phạm vi 16c) | Mỗi cache hit `SUM` cả bảng `gemini_response_cache` không index (3 000 dòng ≈ 1 ms, 10 000 ≈ 3,2 ms) | Index phủ `idx_gemini_cache_saved` (10 000 dòng ≈ 0,35 ms); có từ TASK-11, chờ Architect phê |
| 16 | Công cụ chẩn đoán | — | Selftest `ui-footprint` (+ biến thể `classic`, `threadN`), `tools/Measure-CockpitFootprint.ps1 [-Baseline]`; nhánh phụ `claude/task-16c-footprint-baseline` (v0.16.0 + selftest, **không merge**) |

---

## 2. Trạng thái Gate 16c — ⏳ chưa đủ bằng chứng (lần 2/3 trượt chuỗi; bản vá `ccf739f`; chờ Verify **lần 3/3** khi máy rảnh)

**Lần 1/3** (`99eb019`, 2026-11-11 03:2x): E1 ✅ E2 ✅ E3 ✅ (Hủy 7,5 ms qua pipe; registry 283 agent; `@agent` giao đúng agent), **E4 ❌** (P50 9,22 / **P95 21,42 ms**: 3/10 lần đổi chiều cao cửa sổ mất 16–20 ms; cùng bản build, Task15 E4 P95 11,6 và Task16b E4 P95 10,2 đều đạt), **E5 ❌** (RAM rảnh **49,2 MB**; chuỗi hồi quy trượt: Task16b RAM 49,6 MB, Task12 độ trễ cache hit selftest p50 2,32 / p95 3,65 ms + vision-mem plateau 1,8 MB, Task15 ctx cache hit P95 3,46 ms). 0 lượt gọi Gemini thật, CPU 0 %, git sạch, CI xanh.

**Lần 2/3 tính cầu chì** (`ad9b7f9`, 2026-11-11 22:55–23:15): Gate 16c tự thân E1–E4 ✅ (P50 8,96 / P95 11,87 ms), RAM 39,8 MB ✅; chuỗi: Task16 / 13 / 12 / 13a ✅, selftest ctx Task15 P95 2,21 ms ✅. Trượt: đuôi độ trễ rải rác lúc máy bận (Task07 sync `/ask` 7,6 ms > 5; Task14 `/calc` P95 2,56 > 2,5; Task15 ctx thật P95 3,26 > 3; Task16b WAKE P50 11,4 > 10) và **Task16b RAM 45,3 MB**. Bản vá `ccf739f`: Cockpit ẩn không giữ thẻ hội thoại / câu trả lời vẽ sẵn (đo trên máy: lúc ẩn mới 35,9 / 39,4 MB, cũ 38,5 / 39,0 MB). Chi tiết `qa_report.md` §24.8.

**Lần chạy hỏng (không tính)** — **Lần 2/3** (`9ab559e`, 2026-11-11 22:2x): E1 ✅ E2 ✅ E3 ✅ **E4 ✅** (Warmup 13,5 ms, P50 7,9 / P95 10,7 ms), E5: RAM rảnh **40,9 MB ✅**, CPU 0 % ✅, `idleCompact` ✅, 0 lượt gọi Gemini thật ✅; **trượt** `regressionChain` + `gitUnchanged`:
- **Lỗi của Claude**: 2 file `task16c_footprint_*.json` do `Measure-CockpitFootprint.ps1` ghi vào handoff không có trong `.gitignore` → git guard từ Task05 trở đi báo "không sạch" → cả chuỗi đổ dây chuyền. Đã sửa `0bd3df7` (không xóa file nào).
- **Trượt thật**: Task16b RAM **45,3 MB** (> 45; lần 1: 49,6) → bản vá `0bd3df7` (bỏ câu trả lời lịch sử vẽ sẵn khi dọn bộ nhớ lúc rảnh); Task15 selftest cache hit P95 **3,28 ms** (SLA 3; P50 1,68); Task12 selftest vision cache hit p95 **2,57 ms** (SLA 2,5; p50 1,84). Hai cái sau là đường Core có từ TASK-11/12, ngoài 16c; index đã kéo trung vị xuống ~0,5 ms nhưng đuôi còn sát ngưỡng.

**Chẩn đoán trên máy Nhật Hòa** (selftest `ui-footprint`, cửa sổ thật ngoài màn hình, lịch sử thật, đo sau GC mạnh; mỗi ô trung bình 2 lần):

| Lúc ẩn | v0.16.0 | Bản lần 1 | Bản bỏ 20 thẻ | Bản phông cũ | **Bản vá `dd2e93c`** |
|---|---|---|---|---|---|
| Private WS | 39,3 MB | 44,0 MB (+4,6) | 40,8 MB | 43,1 MB | **42,1 MB (+2,1)** |
| GC heap | 4,9 MB | 7,3 MB | 6,6 MB | 7,2 MB | 6,8 MB |

→ 20 thẻ hội thoại ≈ 3,2 MB, phông chữ quyết định 1 ≈ 0,5–0,9 MB. Phần còn lại ≈ 2 MB bộ nhớ .NET (cây XAML lớn hơn: 574 phần tử so với 137).

| # | Bằng chứng | Lần 1/3 → 2/3 | Lần 3/3 cần |
|---|------------|---------|-------------|
| E1 | build, BOM, ADR-68, không font nhúng, không animation, selftest `ui-v2-visual` (thêm `e2.threadCompact`, `e2.threadCompactReleases`, `e3.hiddenPanelsDetached`) | ✅ | ✅ |
| E2 | Hủy trên HostService thật, thanh 44, 2 cột / 300, dòng 40, pool ≤ 20, dòng cục bộ | ✅ | ✅ |
| E3 | `/`, `@sec` (283 agent), VIỆC TIẾP THEO, thẻ ảnh, F1, Alt+A / G / K | ✅ | ✅ |
| E4 | Warmup ≤ 60, N = 10 P50 ≤ 10 / P95 ≤ 16 ms | ❌ P95 21,42 → lần 2 ✅ P95 10,7 | ✅ |
| E5 | RAM ≤ 45, CPU 0, `idleCompact` (mới), chuỗi Verify-Task15 → … → 03 PASS, zeroLiveCalls | ❌ 49,2 MB + chuỗi → lần 2: RAM ✅ 40,9, chuỗi ❌ | ✅ |

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-16c-cockpit-ui-v2-visual
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task16c.ps1
```
Kết quả ghi vào `03_Local_Data_Vault/handoff/task16c_verification.json`. Bảng `Alt+A` đọc E1–E5 từ file này. Lệnh chạy tự build, chuỗi hồi quy đầy đủ mất khoảng 12–15 phút. Khi đang sửa lỗi, có thể thêm `-FastE5`, nhưng cần một lần chạy không có `-FastE5` để đóng Gate.

### Sau khi Architect đóng dấu (Nhật Hòa làm, Claude không push được `main` hay tag)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-16c-cockpit-ui-v2-visual
git push origin main
git tag -a v0.16.5 -m "Gate 16c: TASK-16c Cockpit UI v2 visual layer (ADR-68)"
git push origin v0.16.5
```

---

## 3. Điểm mở cần Architect quyết định

**Đã quyết (Gemini, 2026-11-11):** (3.1) lần chạy hỏng vì `.gitignore` không tính, lượt tới là lần 2/3; (3.2) phương án **(b)** — xóa dòng cache hết hạn + checkpoint WAL nhẹ lúc khởi động (đã làm, `9337b7f`), và Task12 vision P95: 2,5 ms là [SLA-WARN], 3,0 ms là ngưỡng cứng (đã làm); (3.3) duyệt chuỗi thẻ 8/20 + bỏ vẽ sẵn khi rảnh; (3.4) giữ index cache; (3.5) giữ phông quyết định 1.

**Còn mở (không chặn Gate):** VỪA DÙNG chỉ giữ trong bộ nhớ; task dọn control cũ đang ẩn; ngưỡng registry ≥ 100 (máy có 283); trang NET của TASK-17 ở dải MFD hay cột phải.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Verify-Task16c lần 3/3 khi máy rảnh** *(khuyến nghị)* | Lệnh ở mục 2, chạy đủ; không dùng máy 15 phút. Nếu ALL PASS: Gemini đóng dấu, Nhật Hòa merge + tag `v0.16.5`. Nếu trượt: dừng theo cầu chì, gửi báo cáo cho Architect | Lượt cuối của cầu chì |
| B. Sau khi PASS và có tag `v0.16.5` | TASK-17 (ADR-67) trên nhánh `claude/task-17-net-radar-voice-ptt`, rebase lên `v0.16.5` | Trang NET và nút mic gắn vào giao diện mới |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **Verify-Task16c lần 2/3** (`ad9b7f9`): Gate 16c tự thân đạt (E1–E4, WAKE P95 11,9 ms, RAM 39,8 MB); trong chuỗi, Task16 / 13 / 12 / 13a đã đạt và selftest ctx Task15 xuống P95 2,21 ms nhờ dọn cache (3.2b). Còn trượt: (1) đuôi độ trễ rải rác ở đường không liên quan UI, lúc máy đang dùng (Task07 sync `/ask` 7,6 ms; Task14 `/calc` P95 2,56 ms dù phần tính 0,02 ms; Task15 ctx thật P95 3,26; Task16b WAKE P50 11,4 trong khi 16c cùng lượt 8,96); (2) Task16b RAM 45,3 MB.
>
> Chẩn đoán kịch bản 16b trên máy (duyệt 20 câu × 3 → PASSIVE → dọn lúc rảnh → ẩn, so với v0.16.0): thẻ hội thoại giữ bộ nhớ gốc mà lần dọn không lấy lại. Bản vá `ccf739f`: Cockpit ẩn không giữ thẻ hội thoại và câu trả lời vẽ sẵn; hiện lại thì dựng ở mức Background, ngoài bộ đếm WAKE. Đo xác nhận lúc ẩn: mới 35,9 / 39,4 MB, cũ 38,5 / 39,0 MB. CI xanh.
>
> Claude Code chạy **Verify-Task16c lần 3/3** (lượt cuối) với máy để rảnh 15 phút. Nếu trượt, tôi dừng theo cầu chì và gửi báo cáo đầy đủ để bạn quyết.
