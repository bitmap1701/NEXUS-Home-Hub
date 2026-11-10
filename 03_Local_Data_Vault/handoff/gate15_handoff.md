# 📦 HỒ SƠ BÀN GIAO — Gate 15 (TASK-15: `/ctx` Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist, ADR-62)

- **Ca thực thi**:
  - Claude (viết code): 2026-11-10 khuya.
  - Claude Code / Nhật Hòa chạy Verify: lần 1 (2026-11-10), lần 2 và lần 3 (2026-11-11).
  - Claude vá script giữa các lần chạy theo chỉ thị Architect.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa, Antigravity
- **Nhánh**: **`claude/task-15-active-window-ctx-safeguard`** @`1e1d6cf`, tách từ `main` @`939da6b` (`v0.15.5`)
- **Đặc tả**: `current_task.md` (TASK-15). Chi tiết kỹ thuật: **`qa_report.md` §22** (lần 1: §22.4; vá: §22.5 và §22.7; lần 2: §22.6; lần 3: §22.8)
- **Trạng thái build**: **đã build và chạy Verify trên máy Nhật Hòa — lần 3/3 (`1e1d6cf`) E1–E5 ALL PASS** (`allPassed=True`). CI xanh. Chờ Architect đóng dấu `v0.16.0`.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Ngữ cảnh cửa sổ | Không có | `/ctx status` (0 token), `/ctx <câu hỏi>` (app + tiêu đề + chữ vừa copy, khối ≤ 1 200 ký tự), cache vân tay `CTX_V1` |
| 2 | Khiên chống gian lận | Chỉ Combat Shield của `/snap` | Tên tiến trình cửa sổ đích (quyền truy vấn tối thiểu) + Toolhelp32 danh sách đang chạy (dùng lại 2 s); không đọc được tên thì chặn |
| 3 | Khiên cửa sổ nhạy cảm | Privacy Guard của `/snap` | Denylist trên tên tiến trình + tiêu đề; chỉ báo loại, tiêu đề không lưu ở đâu |
| 4 | MCP | — | `get_active_window_context` (JSON, 0 token) |
| 5 | Chuỗi hồi quy | So cứng `TASK-16b`; `-SkipLive` dừng ở Task11, nên vẫn gọi Gemini thật | Đọc `TaskId` động; `-SkipLive` đi tới Task07; E5 kiểm `zeroLiveCalls` |
| 6 | Đo WAKE | 10 mẫu, không phân rã | `ColdFirstWake` (chỉ báo cáo) + Warmup ≤ 60 ms + N = 10, có phân rã `LevelParts`; chờ về 40 px + nghỉ 50 ms trước mỗi lần đánh thức |
| 7 | Task12 HUD | Đo một cặp HUD / `MEM` | Lệch > 0,5 MB thì đo lại một cặp sau 250 ms (ngưỡng giữ nguyên) |

Không đổi luồng WAKE C# và không đổi ngưỡng nào. Không đụng `main` hay tag. Không thao tác Cấp Đỏ. Không tốn phí.

---

## 2. Trạng thái Gate 15 — ✅ PASS (lần 3/3, `1e1d6cf`, máy Nhật Hòa)

| # | Bằng chứng | Lần 1 (`f997075`) | Lần 2 (`cb946a9`) | Lần 3 (`1e1d6cf`) |
|---|------------|---|---|---|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ctx-safeguard` | ✅ | ✅ | ✅ |
| E2 | 10 + 10 mẫu bị chặn, P95 ≤ 0,5 ms, không lọt tiêu đề | ✅ 0,008 / 0,013 ms | ✅ | ✅ |
| E3 | `/ctx` thật; 25 cache hit P95 ≤ 3 ms; MCP ấm ≤ 2 ms | ✅ 2,17 / 0,085 ms | ✅ | ✅ |
| E4 | Warmup ≤ 60 ms; N = 10 P50 ≤ 10 / P95 ≤ 16 ms (lần 3 thêm `ColdFirstWake` chỉ báo cáo và `wakeSettled40`) | ❌ P95 28,1 | ❌ Warmup 62,9 (Activate 54,6); N = 10 9,63 / 13,24 | ✅ (số chi tiết trong `task15_verification.json`) |
| E5 | RAM ≤ 45 MB, CPU 0 %, chuỗi Task16b PASS, 0 lượt gọi thật, git | ❌ 2 lỗi script | ❌ Task12 `hudWithinHalfMb` | ✅ **39,2 MB**, **0 %**, chuỗi PASS (686 s), **0 lượt gọi thật**, git ✅, CI success |

### Lệnh chạy (để kiểm lại)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout claude/task-15-active-window-ctx-safeguard
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task15.ps1
```

### Sau khi Architect đóng dấu (Nhật Hòa làm, Claude không push được `main` hay tag)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-15-active-window-ctx-safeguard
git push origin main
git tag -a v0.16.0 -m "Gate 15: TASK-15 /ctx Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist (ADR-62)"
git push origin v0.16.0
```
Sau đó ca Claude chuyển ADR-62 từ "Đang triển khai" sang "Chốt v0.16.0".

---

## 3. Điểm mở cần Architect quyết định

1. **Đóng dấu Gate 15 → `v0.16.0`.**
2. **`ColdFirstWake`.** Lần mở Cockpit đầu tiên sau khi khởi động HostService tốn khoảng 55 ms ở `Activate()` (số lần 2). Chi phí này có từ trước TASK-15 nhưng chưa từng được đo. Có mở task riêng để đo bên trong `Activate()` rồi tối ưu không?
3. **Đổi độ cao cửa sổ** (5,5–11 ms mỗi lần đánh thức) là phần chậm nhất của WAKE. Lần 2, P50 lên 9,63 ms, sát ngưỡng. Có gộp vào task trên không?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng dấu `v0.16.0`, fast-forward `main`, rồi giao Phase 4** *(khuyến nghị)* | Theo ROADMAP: giọng nói, PTT FIFINE K669, `/net` | Gate 15 đã PASS đủ bằng chứng |
| B. Task nhỏ trước Phase 4: tối ưu `ColdFirstWake` và đổi độ cao cửa sổ | Đo bên trong `Activate()` và `ApplyLevel`, rồi mới sửa | Lần mở đầu sau khi bật máy là trải nghiệm thật của người dùng; P50 đã sát ngưỡng |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, đây là bản cập nhật tiến độ NEXUS Home Hub (repo `bitmap1701/NEXUS-Home-Hub`) **kể từ khi bạn đóng dấu Gate 16 (`v0.15.0`)**. Trong lúc bạn hết token, một phiên Gemini khác giữ vai Architect và đã duyệt các bước dưới đây.
>
> **1. Gate 16b — TASK-16b (ADR-66, Cockpit UI v2 Phase 2): ✅ đã đóng `v0.15.5`** (`main` @`939da6b`)
> - Đã làm:
>   - nền đặc với chrome cố định chiều cao (cửa sổ cắt còn 40 px);
>   - markdown nhẹ;
>   - lịch sử hỏi–đáp: dựng sẵn và lưu đệm, không hiện dòng test;
>   - chế độ lười: Alt+Enter chép bước tiếp theo của liên minh, Alt+A bật bảng liên minh, phím số chép code;
>   - bảng liên minh đọc `current_task.md` + `gate<N>_handoff.md`;
>   - huy hiệu "xong ca" qua MCP `notify_shift_complete`.
> - Lần chạy 3 ALL PASS: WAKE P50 8,58 / P95 11,24 ms; xem lịch sử P95 1,50 ms; RAM 37,7 MB; CPU 0 %.
>
> **2. Gate 15 — TASK-15 (ADR-62, `/ctx` Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist): ✅ ALL PASS ở lần 3/3, chờ bạn đóng dấu `v0.16.0`**
>
> Nhánh `claude/task-15-active-window-ctx-safeguard` @`1e1d6cf`, tách từ `main` @`939da6b`. CI xanh.
>
> - **Đã làm** (code ở `f997075`, không đổi qua 3 lần chạy):
>   - `/ctx status` (0 token) và `/ctx <câu hỏi>`: gửi kèm tên app + tiêu đề + chữ vừa copy (≤ 1 000 ký tự, khối ≤ 1 200), cache theo vân tay `CTX_V1`.
>   - MCP `get_active_window_context`.
>   - Khiên chống gian lận: tên tiến trình cửa sổ đích + danh sách tiến trình đang chạy (dùng lại 2 s). Không đọc được tên thì chặn.
>   - Khiên cửa sổ nhạy cảm: denylist tên tiến trình + tiêu đề; chỉ báo loại, tiêu đề không lưu ở đâu.
>   - Luồng WAKE không đổi.
> - **Lần 1** (`f997075`): E1–E3 PASS. E4 P95 28,1 ms (một mẫu 44,2 ms). E5 gãy vì 2 lỗi script hồi quy.
>   - Architect duyệt:
>     - giữ thiết kế `/ctx`;
>     - Verify-Task16b đọc `TaskId` động thay vì so cứng `TASK-16b`;
>     - truyền `-SkipLive` xuyên chuỗi hồi quy;
>     - E4 thêm Warmup ≤ 60 ms và phân rã WAKE.
> - **Lần 2** (`cb946a9`): E1–E3 PASS.
>   - E4: N = 10 đạt (P50 9,63 / P95 13,24 ms), nhưng Warmup 62,9 ms, trong đó `Activate()` chiếm 54,6 ms.
>   - E5: trượt vì phép kiểm cũ của Task12 `hudWithinHalfMb` (HUD 33,4 MB, số đọc trước/sau 34,8 / 34,1 MB); 0 lượt gọi Gemini thật.
>   - Phát hiện: Warmup thực chất là lần mở Cockpit đầu tiên sau khi HostService khởi động lại.
>   - Architect chọn:
>     - tách thành `ColdFirstWake`, chỉ báo cáo;
>     - chờ cửa sổ về 40 px + nghỉ 50 ms trước mỗi lần đánh thức;
>     - Task12 đo lại cặp HUD / `MEM` sau 250 ms, ngưỡng giữ 0,5 MB.
> - **Lần 3** (`1e1d6cf`): **ALL PASS** (`allPassed=True`).
>   - E5: RAM lắng 39,2 MB (HUD 38,1), CPU 0 %.
>   - Chuỗi Verify-Task16b (Task16 → 13 → 12 → 13a → `-FullRegression -SkipLive` Task14 … 03) PASS trong 686 s.
>   - **0 lượt gọi Gemini thật**, git không đổi, CI success.
>   - Số chi tiết E1–E4 nằm trong `03_Local_Data_Vault/handoff/task15_verification.json`.
>
> **3. Đính chính và phát hiện**
> - Hồ sơ Gate 16b ghi chuỗi hồi quy "`-SkipLive`, 0 token". Câu đó **sai**: Gate 16b lần 2–3 và Gate 15 lần 1 đã gọi Gemini thật, vì `Verify-Task11` không truyền `-SkipLive` xuống. Free tier nên không tốn tiền, nhưng có tốn quota. Đã sửa: chỉ Task07/09/10/11 có lượt gọi thật, nay đều được bỏ qua khi có `-SkipLive`, và E5 kiểm `zeroLiveCalls`.
> - Lần mở Cockpit đầu tiên sau khi khởi động HostService tốn khoảng 55 ms ở `Activate()`. Chi phí này có từ trước TASK-15 nhưng chưa từng được đo; nay được ghi là `ColdFirstWake`.
> - Nhãn RAM trên HUD không bị cũ: nó được tính ngay trong lệnh `MEM`, cùng API EX2 với script. Lệch là do RAM tự dao động.
> - Suốt quá trình: không thao tác Cấp Đỏ, không tốn phí, không đổi ngưỡng.
>
> **4. Cần bạn**
> 1. Đóng dấu Gate 15 → `v0.16.0`. Sau đó Nhật Hòa fast-forward `main`, gắn tag; ca Claude chuyển ADR-62 sang "Chốt v0.16.0".
> 2. Quyết có mở task riêng tối ưu `ColdFirstWake` (`Activate` khoảng 55 ms) và đổi độ cao cửa sổ (5,5–11 ms mỗi lần, P50 lần 2 sát ngưỡng) trước Phase 4 không.
> 3. Giao task tiếp theo: theo ROADMAP là Phase 4 (giọng nói, PTT FIFINE K669, `/net`).
>
> Ghi chú tồn đọng: mục 9 (gọi Claude từ Cockpit) vẫn tạm hoãn, chỉ làm khi Nhật Hòa yêu cầu.
