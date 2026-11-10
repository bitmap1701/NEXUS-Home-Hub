# 📦 HỒ SƠ BÀN GIAO — Gate 15 (TASK-15: `/ctx` Active Window Context + Anti-Cheat Safe Guard + Sensitive Window Denylist, ADR-62)

- **Ca thực thi**:
  - Claude (viết code): 2026-11-10 khuya.
  - Claude Code trên máy Nhật Hòa, lần chạy 1: 2026-11-10 khuya.
  - **Claude (vá theo chỉ thị Architect): 2026-11-11.**
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Claude Code trên máy Nhật Hòa (chạy Verify), Antigravity
- **Nhánh**: **`claude/task-15-active-window-ctx-safeguard`**, tách từ `main` @`939da6b` (`v0.15.5`)
- **Đặc tả**: `current_task.md` (TASK-15). Chi tiết kỹ thuật: **`qa_report.md` §22**. Bản vá ca này: **§22.5**
- **Trạng thái build**:
  - Lần chạy 1 (`f997075`): E1–E3 PASS; E4 trượt WAKE P95; E5 trượt chuỗi hồi quy.
  - Bản vá ca này: **chưa build, chưa chạy** trong môi trường của Claude (không có `dotnet`/PowerShell). Chỉ sửa script Verify, không đổi code C#. Chờ CI và **lần chạy 2/3** trên máy Nhật Hòa.

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

## 2. Trạng thái Gate 15 — ⏳ chưa đủ bằng chứng: chờ lần chạy 2/3 trên máy Nhật Hòa (lần 1: E1–E3 PASS, E4/E5 trượt)

| # | Bằng chứng | Lần 1 (`f997075`) | Lần 2 |
|---|------------|-------------------|-------|
| E1 | build `-warnaserror` 0/0, Zero-BOM, selftest `ctx-safeguard` | ✅ build 0/0, Zero-BOM 123 file, selftest pass | ⏳ |
| E2 | 10 + 10 mẫu bị chặn 100 %, P95 ≤ 0,5 ms, 0 token, không lọt tiêu đề vào SQLite / WAL / `latest_dispatch.json` | ✅ P95 0,008 / 0,013 ms; `vgc` đang chạy → `anticheat-running`; không lọt | ⏳ |
| E3 | `/ctx` trên cửa sổ thường; 25 cache hit P95 ≤ 3,0 ms; MCP ấm ≤ 2,0 ms | ✅ lần quét đầu 9,0 ms; 25 cache hit P95 2,17 ms; MCP ấm P95 0,085 ms | ⏳ |
| E4 | **Warmup ≤ 60 ms** + phân rã; **N = 10: P50 ≤ 10,0 / P95 ≤ 16,0 ms**; mỗi mẫu đủ `LevelParts`; lịch sử không có dòng `/ctx` test | ❌ P50 7,57 / P95 28,1 ms (mẫu đầu 44,17; chưa có Warmup, chưa có phân rã) | ⏳ |
| E5 | Private WS lắng ≤ 45,0 MB; CPU rảnh 0,0 %; chuỗi Verify-Task16b (→ Task16 … Task03) PASS; **0 lượt gọi Gemini thật** (`zeroLiveCalls`); git không đổi | ❌ 38,1 MB ✅, 0 % ✅, chuỗi trượt vì 2 lỗi script | ⏳ |

### Lệnh chạy (Claude Code trên máy Nhật Hòa) — lần 2/3
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-15-active-window-ctx-safeguard
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task15.ps1
```
- Chạy bằng Windows PowerShell 5.1 (STA) để kiểm được clipboard.
- Trong lúc chạy: không chạy lệnh git, không bấm vào HUD, không copy/paste, để yên chuột.
- E5 chạy cả chuỗi Task16 → … → Task03 (lần 1 mất khoảng 12 phút).
  - Giờ chuỗi không còn gọi Gemini thật, nên không còn chờ mạng.
  - `-FastE5` chỉ để thử nhanh, không dùng để nghiệm thu.
- Cần xem trong kết quả:
  - E4: dòng `Warmup … [hwnd | height | activate | focus]` và 10 dòng `WAKE n`.
  - E5: dòng `live calls in chain: 0 (audited task07,task09,task10,task11; missing [])`.
- Nếu máy đang chạy tiến trình chống gian lận (ví dụ `vgc`), phép thử cửa sổ thật báo `anticheat-running`. Đó là hành vi đúng.
- Cầu chì: đây là lần thử **2/3**. Nếu trượt, dừng và báo kèm phân rã, không tự vá.

---

## 3. Điểm mở cần Architect quyết định

1. **Tên trường phân rã.** `HwndCaptureMs` lấy từ `BeforeLevelMs` có sẵn của Cockpit. Trường này đo từ mốc phím tắt tới trước khi đổi độ cao, gồm lưu `GetForegroundWindow`, đổi chế độ HUD và tắt click-through, chứ không chỉ riêng việc lấy HWND. Ca này không đổi code Cockpit cho khớp tên. Chấp nhận cách ánh xạ này?
2. **Chuỗi `-SkipLive`** dừng ở Task07, tầng cuối cùng có lượt gọi thật. Task06 → 03 không thêm tham số rỗng (Minimal Change). Phép kiểm `zeroLiveCalls` ở E5 chứng minh 0 lượt gọi thật. Đồng ý?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Claude Code chạy Verify-Task15 lần 2/3** *(khuyến nghị)* | Lệnh ở mục 2, sau khi CI xanh | Bản vá chỉ chạm script; cần số đo thật cho E4 (Warmup + phân rã) và E5 (0 lượt gọi thật) |
| B. Nếu E4 lần 2 vẫn trượt | Dừng, gửi phân rã cho Architect quyết sửa code ở khâu chậm | Cầu chì còn 1 lần thử; cần biết khâu chậm trước khi sửa |
| C. Phase 4 (giọng nói, PTT FIFINE K669, `/net`) | Theo ROADMAP | Sau khi Gate 15 đóng (`v0.16.0`) |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, đã vá Gate 15 theo chỉ thị lần 2/3, nhánh `claude/task-15-active-window-ctx-safeguard`. Chỉ sửa script Verify, không đổi code C#, không đổi ngưỡng. **Chưa build, chưa chạy** trong môi trường của Claude; chờ CI và Claude Code chạy lần 2 trên máy Nhật Hòa. Chi tiết: `qa_report.md` §22.5.
>
> - **(7a)** `Verify-Task16b.ps1`: đọc `TaskId` từ `current_task.md` theo đúng quy tắc của `AllianceStatus.FirstToken`, không còn so cứng `TASK-16b`.
> - **(7b)** `-SkipLive` truyền xuyên Task11 → 10 → 09 → 08 → 07 (Task09/08 có thêm tham số). Task07/09/10/11 ghi `skipLive` / `liveCallAttempted`. Verify-Task15 E5 có phép kiểm mới `zeroLiveCalls`, yêu cầu 0 lượt gọi thật trong chuỗi.
>   - Lưu ý: chuỗi thật có Task07 (08 → 07 → 06). Chỉ Task07/09/10/11 gọi Gemini thật; Task08 và Task06 → 03 không gọi.
>   - Nên không thêm tham số rỗng cho Task06 → 03.
> - **(6)** Verify-Task15 E4: 1 Warmup ngay sau E3 (≤ 60 ms, ghi kèm phân rã), rồi N = 10 mẫu với P50 ≤ 10,0 / P95 ≤ 16,0 ms. Mỗi mẫu có `LevelParts`: `HwndCaptureMs`, `WindowHeightMs`, `ActivateMs`, `FocusMs`.
> - **(7c)** Đính chính: chuỗi hồi quy Gate 16b lần 2–3 và Gate 15 lần 1 đã gọi Gemini thật, nên câu "0 token" của Gate 16b (do Claude viết) là sai. Free tier nên không tốn tiền, nhưng có tốn quota. Đã ghi trong §22.5, `gate15_handoff.md` và dòng đầu `gate16b_handoff.md`.
>
> Cần bạn xác nhận 2 điểm:
> - (1) `HwndCaptureMs` lấy từ `BeforeLevelMs` có sẵn: đo từ mốc phím tắt tới trước khi đổi độ cao, gồm lưu foreground HWND, chế độ HUD và click-through. Không đổi code Cockpit.
> - (2) `-SkipLive` dừng ở Task07, và `zeroLiveCalls` là bằng chứng cho 0 lượt gọi thật.
>
> Bước tiếp: Claude Code chạy `Verify-Task15.ps1` lần 2/3. Nếu E4 vẫn trượt thì dừng và gửi phân rã để bạn quyết khâu cần sửa.
