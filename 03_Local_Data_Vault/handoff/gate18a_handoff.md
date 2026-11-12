# 📦 HỒ SƠ BÀN GIAO — Gate 18a (TASK-18a, ADR-72 → `v0.18.1`): cầu nối STT tiếng Việt (LAN / CLI) + fallback SAPI

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-18a-vi-stt-bridge` (từ đầu nhánh TASK-18 `2cb6e37`; tag `v0.18.0` chưa gắn lúc nhận đặc tả)
- **Chi tiết kỹ thuật**: `qa_report.md` §29
- **Trạng thái build**: Claude **chưa build**; CI build + selftest. Chưa có số đo trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Cấu hình STT | Cố định SAPI en-US | `"stt"` tùy chọn trong `nexus_config.json`, `/reload` áp dụng ngay |
| 2 | Bộ nhận dạng | SAPI worker | Tuyến `auto` lan → cli → SAPI; `lan` (TCP WAV → UTF-8), `cli` (stdout UTF-8); lỗi → bước kế tiếp, 0 exception |
| 3 | `/ptt status`, `/reload` | Chỉ SAPI | Hiện tuyến đang dùng (vd `auto: lan 192.168.1.22:5055 → sapi en-US · vi-VN`) |
| 4 | Kiểm thử | `ptt-stt` a–c | Thêm d (LAN loopback tiếng Việt), e (CLI giả lập), f (fallback SAPI); `Verify-Task18a.ps1` |
| 5 | Sổ cái | ADR-71 đang triển khai | ADR-71 **Chốt `v0.18.0`**, ADR-72 đang triển khai, trống kế tiếp ADR-73 |

---

## 2. Trạng thái Gate 18a — ⏳ chờ Verify lần 1/3 (CI xanh `65cd340`: LAN 11 ms, CLI 107 ms, fallback SAPI sau 207 ms)

```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-18a-vi-stt-bridge
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task18a.ps1
```
Khoảng 70 phút (chuỗi Verify-Task18 → … → 03). Script ghi tạm "stt" vào `nexus_config.json` rồi trả lại đúng file cũ. Kết quả: `03_Local_Data_Vault/handoff/task18a_verification.json`.

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, BOM, ADR-71 chốt `v0.18.0`, ADR-72, selftest | ⏳ |
| E2 | lan loopback tiếng Việt ≤ 300 ms; cli ≤ 800 ms; fallback SAPI ≤ 250 ms, PCM 0 B | ⏳ |
| E3 | `/reload` + `/ptt status` đúng tuyến, lệnh ≤ 50 ms | ⏳ |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms | ⏳ |
| E5 | RAM ≤ 45 (mục tiêu ≤ 40; `[SLA-WARN]` ≤ 50 khi GC ≤ 10,5), CPU 0 %, chuỗi 18 → 03 ALL PASS, 0 lượt gọi thật | ⏳ |

---

## 3. Điểm mở cần Architect quyết định

- **(18a.1) Engine tiếng Việt thật**: cầu nối đã sẵn (hợp đồng LAN: WAV vào → UTF-8 ra; CLI: `{wav}` / stdin → stdout). Chưa có engine nào trên PC / Nitro 5. Đề xuất task sau: Whisper.cpp (mô hình `small`/`base` đa ngôn ngữ) chạy làm worker trên Nitro 5 cổng 5055, hoặc CLI trên PC.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Gắn tag `v0.18.0` rồi Verify-Task18a lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | CI xanh trước |
| B. Worker Whisper trên Nitro 5 | Dịch vụ 5055 theo hợp đồng LAN | 18a.1 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. TASK-18a đã viết xong trên `claude/task-18a-vi-stt-bridge` (`qa_report.md` §29). Đối chiếu repo: lúc nhận đặc tả, tag `v0.18.0` **chưa gắn** (`main` = `5b42394`), nên nhánh tách từ đầu nhánh 18 (`2cb6e37`).
> - `"stt"` tùy chọn trong `nexus_config.json` (đọc mỗi phiên, `/reload`); tuyến `auto` = lan → cli → SAPI (bỏ qua cái chưa cấu hình), `lan` / `cli` hỏng → SAPI, 0 exception. Worker SAPI vẫn làm ấm từ lúc bắt đầu thu nên fallback nhanh.
> - Hợp đồng LAN: TCP, gửi một WAV 16 kHz mono, đóng chiều gửi, đọc UTF-8 đến hết. CLI: `{wav}` hoặc stdin, `{lang}`, stdout UTF-8.
> - Selftest `ptt-stt` thêm LAN loopback trả câu tiếng Việt có dấu, CLI giả lập, fallback SAPI khi cổng đóng; `Verify-Task18a.ps1` E1–E5.
>
> - CI `65cd340` (runner có SAPI): LAN 11 ms và CLI 107 ms trả đúng câu tiếng Việt có dấu; `auto` với cổng đóng + CLI thiếu → SAPI sau 207 ms (≤ 250; trên Windows cổng loopback đóng chạm mốc 200 ms chứ không bị từ chối ngay), ra "Hello world this is a test".
>
> Điểm mở **(18a.1)**: engine tiếng Việt thật (đề xuất Whisper.cpp làm worker LAN trên Nitro 5) để task sau?
