# 📦 HỒ SƠ BÀN GIAO — Gate 15 (TASK-15: `/ctx` Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist, ADR-62)

- **Ca thực thi**:
  - Claude (viết code): 2026-11-10 khuya.
  - Claude Code trên máy Nhật Hòa, lần chạy 1: 2026-11-10 khuya.
  - **Claude (vá theo chỉ thị Architect): 2026-11-11.**
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-15-active-window-ctx-safeguard`**, tách từ `main` @`939da6b` (`v0.15.5`)
- **Đặc tả**: `current_task.md` (TASK-15). Chi tiết kỹ thuật: **`qa_report.md` §22**. Bản vá ca này: **§22.5**
- **Trạng thái build**:
  - Lần 1 (`f997075`): E1–E3 PASS; E4 trượt WAKE P95; E5 trượt chuỗi hồi quy.
  - Lần 2 (`cb946a9`, CI xanh), Nhật Hòa chạy trên máy: **E1–E3 PASS; E4 trượt Warmup 62,9 ms (≤ 60); E5 trượt vì Verify-Task12 `hudWithinHalfMb`**.
  - Dừng theo cầu chì: **còn 1 lần thử**.

---

## 1. Ca này đã làm gì

Architect duyệt cả 7 điểm.
- **Điểm 1–5:** giữ nguyên thiết kế `/ctx` ở `f997075`.
- **Điểm 6–7:** vá như bảng dưới.

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 7a | `Verify-Task16b.ps1` E3 | So cứng `TaskId -eq 'TASK-16b'`, nên luôn trượt khi chạy hồi quy dưới task khác | Đọc `TaskId` từ `current_task.md` (cùng quy tắc với `AllianceStatus.FirstToken`), ghi `expectedTaskId` vào JSON |
| 7b | `-SkipLive` trong chuỗi hồi quy | Dừng ở Task11; Task10/09/07 vẫn gọi Gemini thật (hết giờ 25 s ở lần 1) | Truyền `-SkipLive` qua Task11 → 10 → 09 → 08 → 07. Task09/08 có thêm tham số. Task07/09/10/11 ghi `skipLive` và `liveCallAttempted` |
| 6 | Verify-Task15 E4 | 10 mẫu, không phân rã; mẫu đầu sau E3 44,17 ms | **1 Warmup ≤ 60 ms** (ghi kèm phân rã), rồi **N = 10** mẫu chấm, ngưỡng giữ nguyên **P50 ≤ 10,0 / P95 ≤ 16,0 ms**. Mỗi mẫu có `LevelParts` (`HwndCaptureMs`, `WindowHeightMs`, `ActivateMs`, `FocusMs`, …) |
| 7b | Verify-Task15 E5 | Không kiểm số lượt gọi thật | Thêm phép kiểm `zeroLiveCalls`: Task07/09/10/11 chạy trong chuỗi phải báo `skipLive=true` và `liveCallAttempted=false` |
| 7c | Hồ sơ | Gate 16b ghi "`-SkipLive`, 0 token" (sai) | Đính chính trong `qa_report.md` §22.5, dòng đầu `gate16b_handoff.md` và hồ sơ này |

Không đụng `main` hay tag, không thao tác Cấp Đỏ, không đổi ngưỡng, không gọi API trả phí.

**Đính chính "0 token".**
- Ở Gate 16b (lần 2: #7664, #7699; lần 3: #8311, #8346) và Gate 15 lần 1 (#9007, #9011, #9046), chuỗi hồi quy đã gọi Gemini thật, vì `Verify-Task11.ps1:347` không truyền `-SkipLive` xuống Task10.
- Câu "0 token" là của Claude. Khóa free tier nên không tốn tiền, nhưng có tốn quota.
- Đọc code, chỉ 4 chỗ gọi Gemini thật: Task07 `ask-live`, Task09 `live-e2e`, Task10 `live-e2e10`, Task11 `live-e2e11`.
- Task08 và Task06 → 03 không gọi Gemini thật. Vì vậy `-SkipLive` dừng ở Task07, không thêm tham số không làm gì cho Task06 → 03.

---

## 2. Trạng thái Gate 15 — ❌ chưa PASS sau lần 2/3: E1–E3 PASS; E4 trượt Warmup; E5 trượt vì Task12. Dừng theo cầu chì, chờ Architect quyết trước lần 3

| # | Bằng chứng | Lần 1 (`f997075`) | Lần 2 (`cb946a9`) |
|---|------------|-------------------|-------------------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ctx-safeguard` | ✅ | ✅ |
| E2 | 10 + 10 mẫu bị chặn 100 %, P95 ≤ 0,5 ms, không lọt tiêu đề | ✅ P95 0,008 / 0,013 ms | ✅ |
| E3 | `/ctx` cửa sổ thường; 25 cache hit P95 ≤ 3,0 ms; MCP ấm ≤ 2,0 ms | ✅ 2,17 / 0,085 ms | ✅ |
| E4 | Warmup ≤ 60 ms; N = 10 P50 ≤ 10,0 / P95 ≤ 16,0 ms; đủ phân rã | ❌ P95 28,1 (mẫu đầu 44,17) | ❌ **Warmup 62,855 ms** (Activate **54,566**); N = 10: P50 **9,628** ✅ / P95 **13,237** ✅ |
| E5 | RAM ≤ 45 MB; CPU 0 %; chuỗi Task16b PASS; 0 lượt gọi thật; git | ❌ chuỗi gãy vì 2 lỗi script | ❌ RAM **37,3 MB** ✅, CPU **0 %** ✅, **0 lượt gọi thật** ✅, git ✅; chuỗi trượt vì **Task12 E5 `hudWithinHalfMb`** (nhãn HUD 33,4 MB, số đọc 34,8 / 34,1 MB, lệch 1 > 0,5) |

**Phát hiện chính (chi tiết ở `qa_report.md` §22.6):**
1. **Warmup = lần mở Cockpit đầu tiên của tiến trình HostService vừa khởi động.**
   - `Verify-Task15.ps1:247` khởi động lại HostService ở E2, và E2–E3 không gửi `COMMANDMODE` nào.
   - 54,6 trong 62,9 ms nằm ở `Activate()`. Ở 10 mẫu sau, `Activate` dưới 0,1 ms.
   - Luồng WAKE không đổi từ Gate 16b, nên đây không phải hồi quy do TASK-15. `Verify-Task16b` đã đánh thức ở E3 trước E4, nên chưa từng đo lần đầu này.
2. **Mẫu chấm chậm nhất ở đổi độ cao cửa sổ** (5,5–11 ms). P50 9,63 ms chỉ còn cách ngưỡng 10 ms là 0,37 ms.
3. **Task12 `hudWithinHalfMb`** là phép kiểm nhãn HUD của Gate 12, vốn dễ lệch (đã sửa một lần ở `8477cd2`). Nhãn **thấp hơn** số đọc thực tế; RAM vẫn trong ngân sách. Không liên quan code TASK-15.
4. **Bản vá 7a/7b có tác dụng**: Task16, Task13, Task13a và chuỗi Full (Task14 → 03) PASS, 0 lượt gọi Gemini thật.

### Lệnh chạy lần 3/3 (chỉ sau khi Architect quyết mục 3)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-15-active-window-ctx-safeguard
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task15.ps1
```
- Windows PowerShell 5.1. Trong lúc chạy không đụng chuột, không copy/paste, không chạy git.

---

## 3. Điểm mở cần Architect quyết định (trước lần thử cuối)

1. **E4 Warmup.** Mẫu này đang đo chi phí kích hoạt lần đầu trong mỗi lần chạy HostService (Activate 54,6 ms), không phải "lần đánh thức sau `/ctx`". Chọn một:
   - **(a) Tách thành hai phép đo** *(Claude khuyến nghị)*:
     - "lần đánh thức đầu sau khởi động" (`ColdFirstWake`): đo ngay sau khi khởi động lại HostService ở E2, ghi kèm phân rã, **chỉ báo cáo**; Architect đặt ngưỡng sau khi có số của vài lần khởi động;
     - Warmup sau E3 giữ nguyên ≤ 60 ms; N = 10 giữ nguyên.
     - Không giấu số: 54,6 ms vẫn nằm trong JSON và báo cáo.
   - **(b) Giữ nguyên phép đo, Architect đặt lại ngưỡng Warmup** dựa trên số thật (lần 1: 44,2 ms; lần 2: 62,9 ms).
   - **(c) Coi đây là chi phí thật cần sửa trong code** (làm ấm luồng kích hoạt khi khởi động). Cần đo riêng xem bên trong `Activate()` chậm ở đâu trước khi sửa, nên Claude đề xuất để thành một task riêng sau TASK-15.
2. **Task12 `hudWithinHalfMb`** (phép kiểm Gate 12, không thuộc TASK-15). Chọn một:
   - **(a) Giữ nguyên, chạy lại ở lần 3** *(Claude khuyến nghị)*; nếu trượt lần nữa thì mở task riêng để sửa cách HUD lấy số RAM;
   - **(b) Architect cho xem lại phép kiểm ngay** (nhãn thấp hơn số đọc 0,7–1 MB) trước lần 3.
3. **P50 sát ngưỡng** (9,63 / 10 ms): chấp nhận rủi ro ở lần 3, hay cần tối ưu đổi độ cao cửa sổ trước?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Architect quyết mục 3, ca Claude áp dụng, rồi chạy lần 3/3** *(khuyến nghị)* | Theo 1(a) và 2(a): chỉ sửa Verify-Task15 E4 (thêm `ColdFirstWake` chỉ báo cáo), không đổi code, không đổi ngưỡng đã chốt | Chỉ còn 1 lần thử; cần quyết trước để lần thử cuối có nghĩa |
| B. Task riêng sau TASK-15: tối ưu lần mở Cockpit đầu tiên (`Activate` 54,6 ms) và đổi độ cao cửa sổ | Đo bên trong `Activate()` và `ApplyLevel`, rồi mới sửa | Lần mở đầu sau khi bật máy là trải nghiệm thật của người dùng |
| C. Phase 4 (giọng nói, PTT FIFINE K669, `/net`) | Theo ROADMAP | Sau khi Gate 15 đóng (`v0.16.0`) |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 15 lần 2/3 (`cb946a9`, CI xanh) chưa PASS**. Dừng theo cầu chì, còn 1 lần thử. Chi tiết: `qa_report.md` §22.6, `gate15_handoff.md` mục 2–3.
>
> - **E1–E3** ✅.
> - **E4** ❌: Warmup **62,855 ms** (ngưỡng ≤ 60). Phân rã: `Activate` **54,566**, đổi độ cao 5,96, focus 1,57, lấy HWND 0,74 ms. 10 mẫu chấm: P50 **9,628** ✅ / P95 **13,237** ✅. Ở 10 mẫu, `Activate` < 0,1 ms; phần chậm nhất là đổi độ cao 5,5–11 ms.
> - **E5** ❌: RAM 37,3 MB ✅, CPU 0 % ✅, **0 lượt gọi Gemini thật** (task07/09/10/11) ✅, git ✅. Chuỗi trượt duy nhất ở **Verify-Task12 E5 `hudWithinHalfMb`**: nhãn HUD 33,4 MB so với số đọc trước/sau `MEM` là 34,8 / 34,1 MB, lệch 1 MB > 0,5. Đây là phép kiểm của Gate 12, không liên quan TASK-15.
> - Bản vá 7a/7b có tác dụng: Task16, Task13, Task13a và chuỗi Full (Task14 → 03) đều PASS.
>
> **Phát hiện:** Warmup thực chất là **lần mở Cockpit đầu tiên của HostService vừa khởi động lại**. Verify-Task15 khởi động lại HostService ở E2, và E2–E3 không có lần đánh thức nào. Luồng WAKE không đổi từ Gate 16b, nên đây không phải hồi quy do TASK-15. `Verify-Task16b` chưa từng đo lần đầu này vì đã đánh thức ở E3.
>
> Cần bạn quyết trước lần thử cuối:
> 1. **E4 Warmup.**
>    - (a) *(Claude khuyến nghị)* Tách phép đo: `ColdFirstWake` ngay sau khi khởi động lại ở E2, ghi kèm phân rã, chỉ báo cáo. Warmup sau E3 giữ ≤ 60 ms, N = 10 giữ nguyên.
>    - (b) Giữ phép đo, đặt lại ngưỡng Warmup theo số thật (44,2 / 62,9 ms).
>    - (c) Coi là chi phí cần sửa code, để thành task riêng sau khi đo bên trong `Activate()`.
> 2. **Task12 `hudWithinHalfMb`.**
>    - (a) *(Claude khuyến nghị)* Giữ nguyên, chạy lại ở lần 3; nếu trượt nữa thì mở task riêng.
>    - (b) Xem lại phép kiểm ngay.
> 3. **P50 9,63 ms sát ngưỡng 10:** chấp nhận rủi ro ở lần 3, hay tối ưu đổi độ cao cửa sổ trước?
