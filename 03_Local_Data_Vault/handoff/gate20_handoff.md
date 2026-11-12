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

## 2. Trạng thái Gate 20 — ⏳ chờ Verify lần 1/3 (CI xanh `152a357`: bảng 0,60 ms, Alt+V → `/net` 163,5 ms)

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
| E1 | build 0/0, BOM, 1 `\bRegisterHotKey`, ADR-74 chốt `v0.19.0`, ADR-75, selftest | ⏳ |
| E2 | ≥ 8 khẩu lệnh có dấu / không dấu → `/…`, câu tự do giữ nguyên, ≤ 1 ms / lượt, ≤ 5 ms tổng | ⏳ |
| E3 | Alt+V "Kiểm tra mạng." → ô lệnh `/net` ≤ 250 ms, PCM 0 B | ⏳ |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms | ⏳ |
| E5 | RAM ≤ 45 (mục tiêu ≤ 35), CPU 0 %, chuỗi 19 → 03 ALL PASS, 0 lượt gọi thật | ⏳ |

---

## 3. Điểm mở cần Architect quyết định

- **(20.1)** Ánh xạ chỉ điền lệnh vào ô (người dùng nhấn Enter), không tự chạy — giữ như vậy cho an toàn hay cho phép tự chạy các lệnh chỉ-đọc (`/net`, `/mic status`)?

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Gắn tag `v0.19.0` rồi Verify-Task20 lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | CI xanh trước |
| B. Đo thật tiếng Việt end-to-end | Nitro 5 + whisper.cpp `ggml-small-q5_1` + Alt+V nói khẩu lệnh | Toàn chuỗi giọng nói chưa đo với engine thật |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. TASK-20 đã viết xong trên `claude/task-20-voice-command-mapper` (`qa_report.md` §32). Lúc nhận đặc tả, tag `v0.19.0` chưa gắn nên nhánh tách từ đầu nhánh 19 (`fc658e7`). ROADMAP không có mục nào cần làm trước TASK-20.
> - `VoiceCommandMapper.TryMap`: bỏ dấu (đ → d), bỏ từ lịch sự, bảng cụm cố định + 2 mẫu có số (cổng, hẹn giờ; số bằng chữ 1–10, 15…60); câu dài / không khớp giữ nguyên; không regex.
> - Móc điền chữ chung của `/ptt` và Alt+V: khớp → ô lệnh nhận `/…` + dòng `[khẩu lệnh "…" → /…]`; không tự chạy.
> - Selftest `voice-command-map` (23 câu + Alt+V → `/net`), `Verify-Task20.ps1` E1–E5. CI `152a357`: 17 khẩu lệnh đúng, 6 câu tự do giữ nguyên, 0 sai, tổng **0,60 ms** (chậm nhất 0,57 ms); Alt+V "Kiểm tra mạng." → `/net` **163,5 ms**. Nhật Hòa đang chạy Verify-Task20 lần 1/3.
>
> Điểm mở **(20.1)**: giữ "chỉ điền, không tự chạy" hay cho tự chạy các lệnh chỉ-đọc (`/net`, `/mic status`)?
