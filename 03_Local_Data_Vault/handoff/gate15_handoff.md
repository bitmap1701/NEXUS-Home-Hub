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
  - Bản vá trước lần 3 (ca Claude 2026-11-11, theo chỉ thị Architect): `ColdFirstWake` chỉ báo cáo; chờ về 40 px + nghỉ 50 ms trước mỗi lần đánh thức; Task12 đo lại cặp HUD / `MEM` một lần. **Chưa build, chưa chạy** ở môi trường Claude. Chờ CI và **lần chạy 3/3 (lần cuối)**.

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

**Ca vá trước lần 3/3** (chi tiết `qa_report.md` §22.7):

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1a | Verify-Task15 E2 | Lần mở Cockpit đầu tiên sau khi khởi động lại rơi vào Warmup | `ColdFirstWake` ngay sau khi khởi động lại ở E2, ghi đủ phân rã, chỉ báo cáo |
| 2 | Verify-Task15 E4 | Thu gọn → nghỉ 300 ms → đánh thức | Thêm bước chờ cửa sổ về đúng 40 px + nghỉ tĩnh 50 ms ngoài đồng hồ; phép kiểm `wakeSettled40` |
| 2 | Đường nóng WAKE (C#) | — | Không đổi: giữa `Height`, `Activate()` và `Focus()` không có thao tác phụ đáng kể (Clamp 0,006 ms, Rest 0,013 ms) |
| 3b | Verify-Task12 E5 | Một cặp HUD / `MEM` | Lệch > 0,5 MB thì nghỉ 250 ms, đo lại một cặp; ngưỡng giữ ≤ 0,5 MB; ghi `hudAttempts` |

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

## 3. Điểm mở cần Architect quyết định

1. **Nhãn HUD không cũ.** Nhãn tính mới ngay trong lệnh `MEM`, cùng API EX2 với script. Lệch là do RAM dao động trong một vòng gọi pipe, nên việc đo lại sau 250 ms vẫn hợp lý. Ghi để Architect nắm, không cần quyết thêm.
2. **50 ms nghỉ tĩnh:** script vốn đã nghỉ 300 ms sau khi thu gọn, nên 50 ms chủ yếu bảo đảm điểm xuất phát 40 px chứ không chắc kéo P50 xuống. Nếu lần 3 trượt P50 thì dừng hẳn và mở task tối ưu đổi độ cao cửa sổ.
3. **Sau Gate 15:** mở task riêng cho `ColdFirstWake` (Activate lần đầu khoảng 55 ms) dựa trên số của lần 3?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Nhật Hòa / Claude Code chạy Verify-Task15 lần 3/3** *(khuyến nghị)* | Lệnh ở mục 2, sau khi CI xanh | Đã áp dụng đủ chỉ thị lần 3 |
| B. Nếu PASS: Nhật Hòa fast-forward `main`, gắn tag `v0.16.0` | Sau khi Gemini đóng dấu | Đóng Gate 15 |
| C. Task riêng: tối ưu lần mở Cockpit đầu tiên (`Activate` khoảng 55 ms) và đổi độ cao cửa sổ | Đo bên trong `Activate()` và `ApplyLevel` trước khi sửa | Lần mở đầu sau khi bật máy là trải nghiệm thật |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, đã áp dụng chỉ thị lần 3/3 trên nhánh `claude/task-15-active-window-ctx-safeguard`. Chỉ sửa script Verify, không đổi C#, không đổi ngưỡng. **Chưa build, chưa chạy** ở môi trường Claude; chờ CI và lần chạy cuối trên máy Nhật Hòa. Chi tiết: `qa_report.md` §22.7.
>
> - **(1a)** `ColdFirstWake` đo ngay sau `Restart-Host` ở E2, ghi đủ `LevelParts`, chỉ báo cáo (`coldFirstWakeGraded=false`). Warmup sau E3 vẫn ≤ 60 ms; N = 10 vẫn P50 ≤ 10,0 / P95 ≤ 16,0 ms.
> - **(2)** Mỗi lần đánh thức: chờ cửa sổ về đúng 40 px (đọc `UISTATE`), rồi nghỉ tĩnh 50 ms ngoài đồng hồ. Phép kiểm mới `wakeSettled40`.
>   - Đã rà đường nóng C#: giữa `Height = 330`, `Activate()` và `Focus()` chỉ có Clamp 0,006 ms và Rest 0,013 ms, nên không sửa C#.
>   - Lưu ý: script vốn đã nghỉ 300 ms sau khi thu gọn, nên 50 ms thêm chủ yếu bảo đảm điểm xuất phát chứ không chắc kéo P50 xuống.
> - **(3b)** Verify-Task12: lệch > 0,5 MB thì nghỉ 250 ms và đo lại một cặp HUD / `MEM`; ngưỡng giữ ≤ 0,5 MB; ghi mọi cặp vào `hudAttempts`.
>   - Đính chính: nhãn HUD **không cũ**. `GetMemoryJson()` tính Private WS mới ngay trong lệnh `MEM`, cùng API EX2 với script. Lệch là do RAM dao động trong một vòng gọi pipe.
>
> Bước tiếp: chạy `Verify-Task15.ps1` lần 3/3. Nếu PASS, xin bạn đóng dấu `v0.16.0`. Nếu trượt, dừng hẳn và gửi phân rã.
