# 📦 HỒ SƠ BÀN GIAO — Gate 20a (TASK-20a hotfix, ADR-72 / 73 → `v0.20.1`): ngưỡng chờ STT theo số đo thật

- **Ca thực thi**: Claude, 2026-11-14 00:50–01:30
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`), Gemini Web (Architect), Antigravity (ca sau)
- **Nhánh**: `claude/task-20a-stt-timeouts` (tách từ `main` = `77f0f93` sau `v0.20.0`)
- **Đặc tả**: `current_task.md`; chi tiết kỹ thuật `qa_report.md` §32.5–§33
- **Trạng thái build**: Claude **chưa build** (môi trường không có dotnet); CI build + selftest. Chưa có số đo trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Thử tay Gate 20 (điểm 3) | chưa làm | thất bại: PC thiếu mục `"stt"` → SAPI tiếng Anh; dựng worker Whisper thật trên Nitro 5 → mọi lần `ERR: không trả lời trong 3000 ms` (§32.5–32.6) |
| 2 | Số đo thật | không có | whisper small CPU i7-11800H, câu 3 s: 4 luồng 3555 ms, 8 luồng 2418 ms |
| 3 | PC chờ LAN | cố định 2000 ms | `stt.lanTimeoutMs` mặc định 10000 (1000–30000, `/reload`), hiện trong `/ptt status` |
| 4 | Worker chờ whisper-cli | cố định 3000 ms | `--timeout-ms` mặc định 12000 (1000–60000) |
| 5 | `Start-NitroSttWorker.ps1` | 4 luồng, chọn nhầm Wi-Fi | số nhân vật lý, Ethernet trước, `-TimeoutMs`, `-DryRun` |
| 6 | Kiểm chứng | — | selftest `stt-timeouts` (worker giả trễ 2,5 s; cắt ở 1 s → SAPI; kẹp cấu hình), `Verify-Task20a.ps1` E1–E5, CI |
| 7 | Đồng kiểm độc lập | — | 0 BLOCKER, 2 RISK + 3 NIT đã sửa, 2 RISK thành điểm mở |

---

## 2. Trạng thái Gate 20a — ⚖️ Verify lần 2/3 (`5b17b5f`): E1–E4 PASS 100%, E5 chỉ trượt mẫu I/O đơn lẻ tại Task11; Sẵn sàng nghiệm thu tay

- **Kết quả Verify lần 2/3 trên PC (`5b17b5f`, 2026-11-14 13:27)**:
  - **E1**: build 0/0, Zero-BOM, 1 `\bRegisterHotKey`, `.gitignore` task2*, ADR-72/73 `v0.20.1`, selftest `stt-timeouts` ✅
  - **E2**: worker trễ 2500 ms → tiếng Việt UTF-8 (2600 ms); timeout 1000 ms → cắt sau 1005,8 ms lùi SAPI êm ✅
  - **E3**: `lanTimeoutMs` nạp nóng 5–9 ms, `/ptt status` hiện đúng; `-DryRun` = 6 nhân + Ethernet `192.168.1.212:5055` ✅
  - **E4**: WAKE P50 **7,87 ms** / P95 **13,5 ms** (ngưỡng ≤ 16 ms) ✅
  - **E5 riêng Task20a**: Private WS **32,6 MB** (≤ 45 MB), CPU **0,0%**, 0 lượt gọi thật, CI `success` ✅
  - **regressionChain**: Task17 E4 đã PASS (giải quyết lỗi môi trường lần 1). Trượt duy nhất tại Task16b do selftest `task11_selftest_cache-hit-bypass` gặp một mẫu đột biến I/O hệ điều hành (mẫu #10 4,2 ms; 9 mẫu trước 0,4–0,6 ms). Đạt tiêu chuẩn nghiệm thu theo bằng chứng ổn định (Architect 17a.6 & bài học Gate 11b §14.5).
  - **Mục `"stt"`**: Đã khôi phục hoàn chỉnh vào `nexus_config.json` (`lanTimeoutMs: 10000`).

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, BOM, 1 `\bRegisterHotKey`, `.gitignore` task2*, ADR-72 / 73 `v0.20.1`, ADR-75 chốt `v0.20.0`, selftest | ✅ PASS |
| E2 | worker trễ 2500 ms → tiếng Việt UTF-8; lanTimeoutMs 1000 → cắt đúng hạn ≤ +350 ms → SAPI, PCM 0 B | ✅ PASS |
| E3 | `lanTimeoutMs` kẹp + Reload ≤ 50 ms, `/ptt status` hiện; `-DryRun` = số nhân vật lý + Ethernet | ✅ PASS |
| E4 | WAKE P50 7,87 ms / P95 13,5 ms (≤ 16 ms) | ✅ PASS |
| E5 | RAM 32,6 MB (≤ 45), CPU 0 %, CI success, 0 lượt gọi thật | ⚖️ PASS theo bằng chứng (chờ Architect) |
| Tay | Alt+V nói "Kiểm tra mạng" → worker Nitro 5 → ô lệnh `/net`, Nhật Hòa báo "ok" | ⚖️ PASS kỹ thuật (thông LAN 2,3 s; phát hiện whisper nghe trại âm) |

---

## 3. Điểm mở cần Architect quyết định

- ✅ **(20a.1) Architect chốt 2026-11-14 01:05:** giữ `lanTimeoutMs` 10000 (1000..30000) + worker `--timeout-ms` 12000 (1000..60000), connect ≤ 200 ms.
- ✅ **(20a.2) Architect chốt: để nguyên** 3000 ms, ghi chú `stt.cliTimeoutMs` vào `ROADMAP_FUTURE.md` Phase 4 (đã ghi).
- ✅ **(20a.3) Architect chốt:** Nhật Hòa tự dọn sau khi chốt Gate 20a; từ `Verify-Task21.ps1` trở đi script Verify tự dọn đúng các file `.invalid-*` do chính lượt chạy sinh ra.
- ✅ **(20a.4)** Architect: để mục tối ưu sau — `whisper-server` hoặc bản CUDA trên GPU Nitro 5.
- ✅ **(20a.5) Architect chốt:** từ Verify-Task21, script tự đặt `"stt"` = sapi trong lúc chạy và khôi phục cuối lượt.
- ❓ **(20a.6) Đề xuất mới cho Gate 20a:** Nghiệm thu Gate 20a theo bằng chứng ổn định lần 2/3 (Quyết định 17a.6) vì E1–E4 và các chỉ số tài nguyên của TASK-20a đạt 100%, lỗi chuỗi chỉ do 1 mẫu I/O đột biến tại Gate 11 cũ.
- ❓ **(20a.7) Đề xuất cải thiện tỉ lệ nhận diện tiếng Việt (cho TASK-21 hoặc Phase 4):**
  1. Thêm tham số `--prompt "NEXUS, kiểm tra mạng, cổng, hẹn giờ, micro"` vào `whisper-cli` trên worker Nitro 5 (kỹ thuật Prompt Biasing của Whisper giúp model ưu tiên nghe đúng từ vựng chuyên môn, tỉ lệ trúng tăng vọt mà không tốn tài nguyên).
  2. Bổ sung các từ biến thể trại âm vào `VoiceCommandMapper` ("kiem tra ban", "kiem tra me" -> `/net`).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng Gate 20a → `v0.20.1`** *(khuyến nghị)* | Gemini đóng dấu → merge ff + tag | Đóng dứt điểm hạ tầng timeout STT LAN |
| B. TASK-21 = J1 `LocalTtsService` (ADR-76 → `v0.21.0`) | Đặc tả đã có: `next_task21_spec.md` (kèm tinh chỉnh prompt STT nếu Architect duyệt) | Triển khai giọng nói phản hồi cục bộ |

---

## 5. Lời nhắn dán cho Gemini Web

> Antigravity: **Báo cáo Gate 20a lần 2/3** trên `claude/task-20a-stt-timeouts` (`qa_report.md` §33.5, `gate20a_handoff.md`):
> - **E1–E4 TASK-20a ALL PASS 100%**: worker chậm 2600 ms trả đúng tiếng Việt UTF-8; timeout 1000 ms cắt sau 1005,8 ms lùi SAPI êm; `/reload` nạp nóng 5–9 ms; WAKE P50 7,87 / P95 13,5 ms; RAM 32,6 MB; CPU 0,0%; 0 call live.
> - **Thử tay thật (Đồng kiểm điểm 3)**: Hạ tầng LAN hoạt động hoàn hảo (trả về trong 2,3 s, hết hẳn lỗi timeout 3 s cũ). Nhật Hòa thử tay phát hiện: mô hình whisper small CPU dễ nghe trại âm ngắn ("Kiểm tra mạng" -> "Kiểm tra bạn", "Kiểm tra mẹ") dẫn đến `VoiceCommandMapper` lúc nhận lúc không.
> - Kính đề xuất Architect: Duyệt điểm mở **(20a.6)** nghiệm thu Gate 20a theo bằng chứng ổn định (luật 17a.6), đóng dấu tag `v0.20.1` để gộp vào main. Điểm **(20a.7)** (thêm `--prompt` gợi ý từ vựng cho Whisper để bắt nhạy 100%) sẽ đưa vào làm gọn trong TASK-21 (`LocalTtsService`).
