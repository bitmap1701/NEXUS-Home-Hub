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

## 2. Trạng thái Gate 18a — ✅ ĐÃ ĐÓNG: `v0.18.1` (`main` + tag, 2026-11-12 ~18:35)

**Lần 1/3**: E2 LAN 11,5 ms / CLI 72,6 ms tiếng Việt đúng, fallback SAPI sau 216,8 ms; E3 `/ptt status` ≤ 7,1 ms; E4 WAKE P50 7,86 / P95 9,99 ms; E5 RAM 32,9 MB, chuỗi 18 → 03 ALL PASS (16b 39,45), CPU 0 %, 0 lượt gọi thật. Chi tiết `qa_report.md` §29.3.

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
| **A. Đóng dấu `v0.18.1`** *(khuyến nghị)* | Nhật Hòa gộp `main` + tag sau khi Architect đóng dấu | ALL PASS lần 1/3 |
| B. Worker Whisper tiếng Việt trên Nitro 5 (cổng 5055) | Dịch vụ theo hợp đồng LAN đã có | 18a.1: cầu nối sẵn, thiếu engine |

### Sau khi Architect đóng dấu (Nhật Hòa làm)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-18a-vi-stt-bridge
git push origin main
git tag -a v0.18.1 -m "Gate 18a: TASK-18a Vietnamese STT bridge - stt config, LAN / CLI, SAPI fallback (ADR-72)"
git push origin v0.18.1
```

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **Gate 18a lần 1/3 ALL PASS** (`177e6eb`, `qa_report.md` §29.3):
> - E1 ✅ build 0/0, BOM, ADR-71 chốt `v0.18.0`, ADR-72.
> - E2 ✅ LAN loopback **11,5 ms**, CLI **72,6 ms**, đúng câu tiếng Việt có dấu; `auto` cổng đóng + CLI thiếu → SAPI sau **216,8 ms** (≤ 250); PCM 0 B.
> - E3 ✅ `/reload` + `/ptt status` đúng tuyến (lan / auto / mặc định), lệnh ≤ 7,1 ms.
> - E4 ✅ WAKE P50 7,86 / P95 9,99 ms.
> - E5 ✅ RAM rảnh **32,9 MB**; chuỗi 18 → 03 ALL PASS (16b 39,45); CPU 0 %; 0 lượt gọi thật; CI xanh.
>
> Mời bạn đóng dấu `v0.18.1` và quyết **(18a.1)**: task kế tiếp làm worker Whisper tiếng Việt trên Nitro 5 (cổng 5055, theo hợp đồng LAN đã có)?
