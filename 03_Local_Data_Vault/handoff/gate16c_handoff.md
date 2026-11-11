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

## 2. Trạng thái Gate 16c — ⏳ chưa đủ bằng chứng (lần 1/3 FAIL; bản vá xong, chờ Verify lần 2/3)

**Lần 1/3** (`99eb019`, 2026-11-11 03:2x): E1 ✅ E2 ✅ E3 ✅ (Hủy 7,5 ms qua pipe; registry 283 agent; `@agent` giao đúng agent), **E4 ❌** (P50 9,22 / **P95 21,42 ms**: 3/10 lần đổi chiều cao cửa sổ mất 16–20 ms; cùng bản build, Task15 E4 P95 11,6 và Task16b E4 P95 10,2 đều đạt), **E5 ❌** (RAM rảnh **49,2 MB**; chuỗi hồi quy trượt: Task16b RAM 49,6 MB, Task12 độ trễ cache hit selftest p50 2,32 / p95 3,65 ms + vision-mem plateau 1,8 MB, Task15 ctx cache hit P95 3,46 ms). 0 lượt gọi Gemini thật, CPU 0 %, git sạch, CI xanh.

**Chẩn đoán trên máy Nhật Hòa** (selftest `ui-footprint`, cửa sổ thật ngoài màn hình, lịch sử thật, đo sau GC mạnh; mỗi ô trung bình 2 lần):

| Lúc ẩn | v0.16.0 | Bản lần 1 | Bản bỏ 20 thẻ | Bản phông cũ | **Bản vá `dd2e93c`** |
|---|---|---|---|---|---|
| Private WS | 39,3 MB | 44,0 MB (+4,6) | 40,8 MB | 43,1 MB | **42,1 MB (+2,1)** |
| GC heap | 4,9 MB | 7,3 MB | 6,6 MB | 7,2 MB | 6,8 MB |

→ 20 thẻ hội thoại ≈ 3,2 MB, phông chữ quyết định 1 ≈ 0,5–0,9 MB. Phần còn lại ≈ 2 MB bộ nhớ .NET (cây XAML lớn hơn: 574 phần tử so với 137).

| # | Bằng chứng | Lần 1/3 | Lần 2/3 cần |
|---|------------|---------|-------------|
| E1 | build, BOM, ADR-68, không font nhúng, không animation, selftest `ui-v2-visual` (thêm `e2.threadCompact`, `e2.threadCompactReleases`, `e3.hiddenPanelsDetached`) | ✅ | ✅ |
| E2 | Hủy trên HostService thật, thanh 44, 2 cột / 300, dòng 40, pool ≤ 20, dòng cục bộ | ✅ | ✅ |
| E3 | `/`, `@sec` (283 agent), VIỆC TIẾP THEO, thẻ ảnh, F1, Alt+A / G / K | ✅ | ✅ |
| E4 | Warmup ≤ 60, N = 10 P50 ≤ 10 / P95 ≤ 16 ms | ❌ P95 21,42 | ✅ |
| E5 | RAM ≤ 45, CPU 0, `idleCompact` (mới), chuỗi Verify-Task15 → … → 03 PASS, zeroLiveCalls | ❌ 49,2 MB + chuỗi | ✅ |

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

1. **Chuỗi thẻ thu gọn 8/20** (bản vá RAM lớn nhất, −2,5 MB): đặc tả ghi "tối đa 20 mục"; nay hiện 8 mới nhất + `▲ n thẻ cũ hơn`, mở đủ 20 khi cần, Card Pool vẫn ≤ 20. Chấp nhận?
2. **Index cache** `idx_gemini_cache_saved` (Core, có từ TASK-11, ngoài phạm vi 16c): sửa tận gốc độ trễ cache hit tăng dần theo số lần Verify. Chấp nhận trong TASK-16c hay tách task riêng?
3. **Phông chữ quyết định 1**: đo được tốn ≈ 0,5–0,9 MB so với Segoe UI + Consolas. Giữ (khuyến nghị nếu lần 2 qua 45 MB) hay đổi?
4. **Ba điểm cũ còn mở**: VỪA DÙNG chỉ giữ trong bộ nhớ; task dọn control cũ đang ẩn; ngưỡng registry ≥ 100 (máy thật có đúng 283). Và trang NET của TASK-17 đặt ở dải MFD hay cột phải.
5. **E4 WAKE**: 3/10 lần đổi chiều cao 16–20 ms ở lần 1, chỉ trong Verify-Task16c (cùng bản build, Task15 / 16b đạt). Nếu lần 2 vẫn trượt riêng E4, Claude sẽ đo phần `WindowHeightMs` với chuỗi thẻ thu gọn trước khi sửa tiếp.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Verify-Task16c lần 2/3 ngay** *(khuyến nghị)* | Lệnh ở mục 2 (chạy đủ, không `-FastE5`). Nếu trượt, gửi `task16c/16b/15/12_verification.json` | Bản vá dựa trên số đo thật; độ trễ cache và WAKE chỉ kiểm được bằng Verify |
| B. Sau khi PASS và có tag `v0.16.5` | Làm tiếp TASK-17 (ADR-67) trên nhánh `claude/task-17-net-radar-voice-ptt`, rebase lên `v0.16.5` | Trang NET và nút mic gắn vào giao diện mới |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **Gate 16c lần 1/3 FAIL** (`99eb019`): E1–E3 ✅ (Hủy 7,5 ms, 283 agent, `@agent` giao đúng), **E4 ❌** P95 21,4 ms (3/10 lần đổi chiều cao 16–20 ms; cùng bản build Task15 và 16b đạt WAKE), **E5 ❌** RAM rảnh 49,2 MB và chuỗi hồi quy trượt (Task16b RAM 49,6 MB; Task12 / Task15 trượt độ trễ cache hit). 0 lượt gọi Gemini thật, CPU 0 %.
>
> Tôi không sửa mò mà đo trên máy Nhật Hòa bằng selftest mới `ui-footprint` (cửa sổ thật, lịch sử thật, không tính lượt cầu chì), so với chính cửa sổ v0.16.0 trên cùng dữ liệu:
> - Lúc ẩn: v0.16.0 39,3 MB; bản lần 1 44,0 MB; bỏ 20 thẻ hội thoại 40,8 MB; phông cũ 43,1 MB. → **20 thẻ ≈ 3,2 MB**, phông quyết định 1 ≈ 0,5–0,9 MB.
> - Bản vá: chuỗi thẻ hiện 8 thẻ mới nhất + `▲ n thẻ cũ hơn` (mở đủ 20 khi bấm / ↑ / mở câu cũ; về PASSIVE thì thu lại và giải phóng), chỉ vẽ sẵn 8 câu trả lời (lần 1 tôi lỡ vẽ sẵn cả 20), bảng ẩn rời cây giao diện. Đo lại: **42,1 MB** (+2,1 so với v0.16.0, từ +4,6).
> - Cache hit chậm dần: mỗi hit `SUM` cả bảng `gemini_response_cache` không index (có từ TASK-11; bảng dài thêm sau mỗi lần Verify). Đã thêm index phủ (10 000 dòng: 3,2 → 0,35 ms).
>
> Mời bạn quyết 5 điểm trong `gate16c_handoff.md` §3, trọng tâm: (1) chuỗi thẻ thu gọn 8/20, (2) index cache nằm trong TASK-16c hay tách task, (3) giữ phông Segoe UI Variable + Cascadia Mono (≈ 0,5–0,9 MB). Claude khuyến nghị cho chạy **Verify-Task16c lần 2/3** ngay với cả ba bản vá.
