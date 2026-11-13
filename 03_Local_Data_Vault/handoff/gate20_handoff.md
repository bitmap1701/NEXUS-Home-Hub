# 📦 HỒ SƠ BÀN GIAO — Gate 20 (TASK-20, ADR-75 → `v0.20.0`): `VoiceCommandMapper` — khẩu lệnh tiếng Việt → lệnh `/` cục bộ

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-20-voice-command-mapper` (từ đầu nhánh 19 `fc658e7`; tag `v0.19.0` chưa gắn lúc nhận đặc tả)
- **Chi tiết kỹ thuật**: `qa_report.md` §32
- **Trạng thái build**: Claude **chưa build**; CI build + selftest. Chưa có số đo trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Alt+V / `/ptt` | Câu nói vào ô lệnh nguyên văn | Khẩu lệnh ("kiểm tra mạng", "kiểm tra cổng 8080", "hẹn giờ 5 phút"…) → lệnh `/` tương ứng + dòng gợi ý; câu hỏi tự do giữ nguyên |
| 2 | Bộ ánh xạ | — | `VoiceCommandMapper.TryMap` (hàm thuần, bỏ dấu, không regex) |
| 3 | Kiểm thử | `voice-input` | `VoiceFlow` dùng chung; selftest `voice-command-map` (bảng 23 câu + Alt+V → `/net`); `Verify-Task20.ps1` |
| 4 | Sổ cái | ADR-74 đang triển khai | ADR-74 **Chốt `v0.19.0`**, ADR-75 đang triển khai, trống kế tiếp ADR-76 |

---

## 2. Trạng thái Gate 20 — ❌ Verify lần 1/3 FAIL (`2e8dc38`, 2026-11-13) → đã vá script, chờ Verify lần 2/3

Nguyên nhân lần 1/3 đều nằm ở **script / cấu hình của Claude**, không ở mã TASK-20:
- **E5:** `.gitignore` chỉ bỏ qua `task0*` / `task1*_selftest_*.json` → file `task20_selftest_voice-command-map.json` (E1 vừa ghi) làm `git status` bẩn → mọi Gate trong chuỗi 19 → 03 FAIL `gitUnchanged` (các bước khác của chuỗi đều PASS; RAM 20 = 33,1 MB, CPU 0 %, 0 lượt gọi thật). Vá: thêm `task2*_selftest_*.json` và `task2*_verification.json`.
- **E4:** một mẫu WAKE 25,3 ms → P95 19,5 > 16 ms (P50 9,5 PASS). Verify-Task20 E4 chưa dùng luật retry chung 18b.3 → nay dùng `Test-NexusP95WithRetry` (vượt lần 1, đo lại 10 mẫu trong ngưỡng = PASS + [SLA-WARN]); chờ Architect xác nhận (20.2).

```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-20-voice-command-mapper
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task20.ps1
```
Khoảng 85 phút (chuỗi Verify-Task19 → … → 03). Kết quả: `03_Local_Data_Vault/handoff/task20_verification.json`.

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, BOM, 1 `\bRegisterHotKey`, ADR-74 chốt `v0.19.0`, ADR-75, selftest | ✅ lần 1 |
| E2 | ≥ 8 khẩu lệnh có dấu / không dấu → `/…`, câu tự do giữ nguyên, ≤ 1 ms / lượt, ≤ 5 ms tổng | ✅ lần 1 (tổng 0,039 ms) |
| E3 | Alt+V "Kiểm tra mạng." → ô lệnh `/net` ≤ 250 ms, PCM 0 B | ✅ lần 1 (184,9 ms) |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms (retry 18b.3) | ❌ lần 1 (P95 19,5) → ⏳ lần 2 |
| E5 | RAM ≤ 45 (mục tiêu ≤ 35), CPU 0 %, chuỗi 19 → 03 ALL PASS, 0 lượt gọi thật | ❌ lần 1 (chỉ `gitUnchanged`) → ⏳ lần 2 |

---

## 3. Điểm mở cần Architect quyết định

- **(20.1)** Ánh xạ chỉ điền lệnh vào ô (người dùng nhấn Enter), không tự chạy — giữ như vậy cho an toàn hay cho phép tự chạy các lệnh chỉ-đọc (`/net`, `/mic status`)?
- **(20.2)** Verify-Task20 E4 WAKE nay dùng luật retry chung 18b.3 (`Test-NexusP95WithRetry`: P95 vượt 16 ms lần đầu → đo lại 10 mẫu, trong ngưỡng = PASS + [SLA-WARN]; mẫu đo lại cũng phải `focused`). Xác nhận áp dụng cho E4 của Gate 20?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Gắn tag `v0.19.0` rồi Verify-Task20 lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | CI xanh trước |
| B. Đo thật tiếng Việt end-to-end | Nitro 5 + whisper.cpp `ggml-small-q5_1` + Alt+V nói khẩu lệnh | Toàn chuỗi giọng nói chưa đo với engine thật |

---

## 5. Lời nhắn dán cho Gemini Web

> Claude: Verify-Task20 **lần 1/3 FAIL** (`2e8dc38`) do script / cấu hình của Claude, mã TASK-20 PASS (E1–E3: bảng 0,039 ms, Alt+V → `/net` 184,9 ms; RAM 33,1 MB, CPU 0 %, 0 lượt gọi thật).
> - E5: `.gitignore` thiếu mẫu `task2*` → file selftest của Task20 làm git bẩn → cả chuỗi 19 → 03 FAIL `gitUnchanged`. Đã vá.
> - E4: một mẫu WAKE 25,3 ms → P95 19,5 > 16 (P50 9,5). E4 nay dùng luật retry chung 18b.3 (`Test-NexusP95WithRetry`, mẫu đo lại cũng phải `focused`).
> Soát độc lập bản vá: 0 BLOCKER, 2 RISK đã sửa. Chờ CI rồi Nhật Hòa chạy lần 2/3. Mời quyết **(20.2)** áp retry 18b.3 cho E4 và **(20.1)** (chỉ điền hay tự chạy lệnh chỉ-đọc). `qa_report.md` §32.3.
