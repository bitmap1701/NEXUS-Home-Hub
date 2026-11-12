# 📦 HỒ SƠ BÀN GIAO — Gate 19 (TASK-19, ADR-74 → `v0.19.0`): Alt+V nói → ô lệnh Cockpit, `ggml-small-q5_1`, chạy lại P95 Task15

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-19-cockpit-voice-input` (từ đầu nhánh 18b `3336b87`; tag `v0.18.2` chưa gắn lúc nhận đặc tả)
- **Chi tiết kỹ thuật**: `qa_report.md` §31
- **Trạng thái build**: Claude **chưa build**; CI build + selftest `voice-input`. Chưa có số đo trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Nhập bằng giọng | Gõ `/ptt` rồi chờ | **Alt+V** (trong Cockpit): bấm để thu (`● REC`), bấm lần nữa để dừng → chữ vào ô lệnh |
| 2 | Mô hình Whisper | Phải truyền `-Model` | Mặc định `ggml-small-q5_1.bin` (18b.1) |
| 3 | Task15 E3 cache-hit P95 | Một mẫu chậm = FAIL cả chuỗi | Chạy lại 1 lần, PASS lần lại = `[SLA-WARN]` (18b.3), hàm dùng chung |
| 4 | Kiểm thử | — | Selftest `voice-input` (CI), `Verify-Task19.ps1` |
| 5 | Sổ cái | ADR-73 đang triển khai | ADR-73 **Chốt `v0.18.2`**, ADR-74 đang triển khai, trống kế tiếp ADR-75 |

---

## 2. Trạng thái Gate 19 — ⏳ chờ `v0.18.2` gắn tag + Verify lần 1/3

```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-19-cockpit-voice-input
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task19.ps1
```
Khoảng 80 phút (chuỗi Verify-Task18b → … → 03). Kết quả: `03_Local_Data_Vault/handoff/task19_verification.json`.

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, BOM, 1 lời gọi `RegisterHotKey`, ADR-73 chốt `v0.18.2`, ADR-74, selftest | ⏳ |
| E2 | Alt+V → REC → dừng sớm → chữ vào ô, con trỏ cuối, ≤ 300 ms, PCM 0 B | ⏳ |
| E3 | Chạy lại P95 + `[SLA-WARN]` đúng 3 trường hợp, Task15 dùng; mô hình mặc định `ggml-small-q5_1.bin` | ⏳ |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms | ⏳ |
| E5 | RAM ≤ 45 (mục tiêu ≤ 36), CPU 0 %, chuỗi 18b → 03 ALL PASS, 0 lượt gọi thật | ⏳ |

---

## 3. Điểm mở cần Architect quyết định

- Không có điểm mở mới. Ghi chú: Alt+V nằm trong `OnWindowKeyDown` (cùng chỗ Alt+A / Alt+G / Alt+K), không phải `OnPreviewKeyDown`: phím Alt trong cửa sổ đã đi qua đây, ô lệnh không nuốt Alt+V.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Gắn tag `v0.18.2` rồi Verify-Task19 lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | CI xanh trước |
| B. Đo thật tiếng Việt trên Nitro 5 | whisper.cpp + `ggml-small-q5_1.bin`, Alt+V nói tiếng Việt | Hoàn tất chuỗi giọng nói |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. TASK-19 đã viết xong trên `claude/task-19-cockpit-voice-input` (`qa_report.md` §31). Đối chiếu repo: lúc nhận đặc tả, tag `v0.18.2` **chưa gắn** (`main` = `012036b`) nên nhánh tách từ đầu nhánh 18b; trong mã có đúng 1 lời gọi `RegisterHotKey` (+ 1 khai báo `extern`), E1 đếm lời gọi.
> - Alt+V trong cửa sổ Cockpit (không `RegisterHotKey`): lần 1 thu (`● REC`, tối đa 15 s), lần 2 dừng → STT → chữ vào ô lệnh, con trỏ cuối.
> - `Start-NitroSttWorker.ps1` mặc định `ggml-small-q5_1.bin`; `Test-NexusP95WithRetry` dùng chung, Verify-Task15 E3 cache hit chạy lại 1 lần + `[SLA-WARN]`.
> - Selftest `voice-input`; `Verify-Task19.ps1` E1–E5.
