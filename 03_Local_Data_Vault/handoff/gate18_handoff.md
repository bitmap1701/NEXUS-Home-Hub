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

## 2. Trạng thái Gate 18 — ⏳ chờ Verify lần 1/3

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
- **(18.2) Thời gian 1500 ms**: gồm khởi động tiến trình con + nạp `DictationGrammar` + nhận dạng. Chưa đo trên PC; nếu vượt, Claude sẽ báo phần chia (spawn / load / recognize) để chọn: giữ worker sống ngắn sau phiên, hay nới ngưỡng.
- **(18.3) Worker LAN `192.168.1.22:5055`**: chưa làm (Nitro 5 chưa có dịch vụ).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Verify-Task18 lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | CI xanh trước khi chạy |
| B. STT tiếng Việt | Engine cục bộ trong worker | 18.1 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. TASK-18 đã viết xong trên `claude/task-18-local-ptt-stt` (`qa_report.md` §28):
> - Khảo sát PC: SAPI có bộ nhận dạng **en-US** (MS-1033-80-DESK) và giọng David / Zira; **không có tiếng Việt**.
> - `LocalSttService` thay stub: VAD = False → bỏ qua ngay (0 token); VAD = True → PCM 16 kHz trong RAM sang tiến trình con `McpTools --stt-worker` (System.Speech `DictationGrammar` en-US), tiến trình thoát sau phiên nên HostService không nạp bộ nhận dạng. Thiếu gói / worker → lùi an toàn. Chữ nhận dạng điền vào ô lệnh; thêm `/ptt`, `/ptt <giây>`, `/ptt status`.
> - Selftest `ptt-stt` tự tạo mẫu bằng giọng SAPI (không cần nói vào mic); `Verify-Task18.ps1` E1–E5; ADR-70 chốt `v0.17.2`, ADR-71 đăng ký.
>
> Mời bạn quyết: **(18.1)** STT tiếng Việt để task sau (engine cục bộ trong worker)? **(18.2)** nếu tổng 1500 ms vượt vì nạp `DictationGrammar` trong tiến trình con, ưu tiên nới ngưỡng hay giữ worker sống ngắn sau phiên?
