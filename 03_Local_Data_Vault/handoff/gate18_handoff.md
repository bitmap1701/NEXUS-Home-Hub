# 📦 HỒ SƠ BÀN GIAO — Gate 18 (TASK-18, ADR-71 → `v0.18.0`): STT cục bộ cho `/ptt` & micro FIFINE K669

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-18-local-ptt-stt` (từ `main` = `v0.17.2` + đặc tả `5b42394`)
- **Chi tiết kỹ thuật**: `qa_report.md` §28
- **Trạng thái build**: Claude **chưa build**; CI Windows runner build + selftest `ptt-stt`. Chưa có số đo trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | STT của `/ptt` | `LocalVadTranscriptStub` (chỉ báo VAD) | `LocalSttService`: im lặng bỏ qua ngay; có giọng → SAPI en-US trong tiến trình con `McpTools --stt-worker`, thoát sau phiên |
| 2 | Lệnh | `/ptt test [s]`, `/ptt stop` | Thêm `/ptt`, `/ptt <giây>`, `/ptt status`; chữ nhận dạng điền vào ô lệnh |
| 3 | Lùi an toàn | — | Thiếu gói ngôn ngữ / worker / giả lập `NEXUS_STT_SIMULATE_MISSING` → thông báo, 0 exception |
| 4 | Kiểm thử | — | Selftest `ptt-stt` (CI), `Verify-Task18.ps1` |
| 5 | Sổ cái | ADR-70 đang triển khai | ADR-70 **Chốt `v0.17.2`**, ADR-71 đang triển khai, trống kế tiếp ADR-72; ROADMAP thêm TASK-18 |

---

## 2. Trạng thái Gate 18 — ✅ lần 1/3 ALL PASS (`83e2a53`, 17:51); chờ Architect đóng dấu `v0.18.0`

**Lần 1/3**: E2 im lặng 0,50 ms, "Hello world this is a test" 249 ms sau khi hết thu, +0,09 MB; E3 `/ptt status` 87,5 ms, UI tối đa 81,3 ms; E4 WAKE P50 9,14 / P95 11,18 ms; E5 RAM 32,6 MB, chuỗi 17b → 03 ALL PASS (16b 40,7 MB), CPU 0 %, 0 lượt gọi thật. Chi tiết `qa_report.md` §28.4.

```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-18-local-ptt-stt
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task18.ps1
```
`git status` phải `working tree clean`. Khoảng 60 phút (chuỗi Verify-Task17b → … → 03). FIFINE cắm sẵn; phòng yên tĩnh là được (E3 thu 1 s). Kết quả: `03_Local_Data_Vault/handoff/task18_verification.json`.

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, Zero-BOM, ADR-70 chốt `v0.17.2`, ADR-71, selftest `ptt-stt` | ⏳ |
| E2 | im lặng bỏ qua ≤ 5 ms; mẫu giọng SAPI → chữ ≤ 1500 ms, PCM 0 B, tăng ≤ 2,5 MB | ⏳ |
| E3 | `/ptt status` ≤ 300 ms, `/ptt test 1` không chặn UI, giả lập thiếu gói → lùi an toàn ≤ 300 ms | ⏳ |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms | ⏳ |
| E5 | RAM rảnh ≤ 45 (mục tiêu ≤ 41,5; `[SLA-WARN]` ≤ 50 khi GC ≤ 10,5), CPU 0 %, chuỗi 17b → 03 ALL PASS, 0 lượt gọi thật | ⏳ |

---

## 3. Điểm mở cần Architect quyết định

- **(18.1) Ngôn ngữ**: SAPI trên PC chỉ có bộ nhận dạng en-US (không có tiếng Việt). Nhận dạng tiếng Việt cần engine khác (ví dụ Whisper / Vosk cục bộ, mô hình vài chục đến vài trăm MB, chạy trong worker) — đề xuất để task sau.
- **(18.2) Đã xử lý trong ca**: worker khởi động + làm ấm khi bắt đầu thu (mô hình dictation nạp trong lúc người dùng nói) → CI 310 ms sau khi hết thu (trước 1635 ms). Chờ số trên PC.
- **(18.3) Worker LAN `192.168.1.22:5055`**: chưa làm (Nitro 5 chưa có dịch vụ).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng dấu `v0.18.0`** *(khuyến nghị)* | Nhật Hòa gộp `main` + tag sau khi Architect đóng dấu | ALL PASS lần 1/3 |
| B. STT tiếng Việt (18.1) | Engine cục bộ (Whisper / Vosk) trong cùng worker | SAPI chỉ có en-US |
| C. Dịch vụ LAN trên Nitro 5 (5055 / 5056) | Worker STT nặng chạy trên laptop | Giữ RAM PC thấp |

### Sau khi Architect đóng dấu (Nhật Hòa làm)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-18-local-ptt-stt
git push origin main
git tag -a v0.18.0 -m "Gate 18: TASK-18 local STT for /ptt - SAPI worker, VAD skip, command box fill (ADR-71)"
git push origin v0.18.0
```

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **Gate 18 lần 1/3 ALL PASS** (`83e2a53`, `qa_report.md` §28.4):
> - E1 ✅ build 0/0, BOM, ADR-70 chốt `v0.17.2`, ADR-71.
> - E2 ✅ im lặng bỏ qua 0,50 ms; mẫu giọng SAPI 2,99 s → "Hello world this is a test" đúng, **249 ms** sau khi hết thu (worker làm ấm trong lúc thu); PCM 0 B; tăng 0,09 MB; giả lập thiếu gói 0,02 ms.
> - E3 ✅ `/ptt status` 87,5 ms, `/ptt test 1` 15,4 ms, UI tối đa 81,3 ms trong phiên; FIFINE thật (phòng yên lặng) → VAD = False, bỏ qua bộ nhận dạng.
> - E4 ✅ WAKE P50 9,14 / P95 11,18 ms.
> - E5 ✅ RAM rảnh Task18 **32,6 MB** (mục tiêu ≤ 41,5), chuỗi 17b → 03 ALL PASS (16b 40,7), CPU 0 %, 0 lượt gọi thật, CI xanh.
>
> Mời bạn đóng dấu `v0.18.0` và quyết **(18.1)**: STT tiếng Việt (SAPI chỉ có en-US) làm task kế tiếp hay để sau?
