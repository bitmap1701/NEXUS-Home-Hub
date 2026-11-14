# 📦 HỒ SƠ BÀN GIAO — Gate 21 (TASK-21: J1 `LocalTtsService` + 20a.7 Chống Trại Âm STT, ADR-76 → `v0.21.0`)

- **Ca thực thi**: Antigravity, 2026-11-14
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`), Gemini Web (Architect)
- **Nhánh**: `claude/task-21-local-tts` (tách từ `main` = `f2a31b6` / `v0.20.1`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md`; chi tiết kỹ thuật `03_Local_Data_Vault/handoff/qa_report.md` §34
- **Trạng thái build & selftest cục bộ**: ✅ `dotnet build -c Release -warnaserror` (`0/0`), `--selftest local-tts` (`PASS`), `--selftest voice-command-map` (`PASS`), `Zero-BOM` (`0` vi phạm), `RegisterHotKey` = `1`.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | `LocalTtsService` (`J1`, `ADR-76`) | chưa có | Đọc mục `"tts"` trong `nexus_config.json` (`/reload`), `SanitizeForSpeech` thuần không Regex (`<= 1,0 ms`), tuyến `auto` (`lan <= 200 ms` → `cli` → `sapi` tự giải phóng), `TtsBufferLedger` = `0 B` rò rỉ |
| 2 | Lệnh cục bộ `/tts` (`0 token`) | chưa có | `/tts [status\|on\|off\|say <text>]` phản hồi đồng bộ `<= 50 ms` (`0,001–0,034 ms`) |
| 3 | Chống trại âm Whisper (`20a.7`) | chưa có `--prompt`, lệch 1 từ là trượt | `Start-NitroSttWorker.ps1` + `--stt-worker` truyền `--prompt "NEXUS, kiểm tra mạng, trạng thái mic, ngữ cảnh, báo cáo qa, hẹn giờ, bật giọng nói, tắt giọng nói."`; `VoiceCommandMapper` thêm `/tts` + biến thể trại âm (`kiểm tra bạn/mẹ/mạng/mạn`, `kim chia mạng`, `trạng thái mít/míc`, `báo cáo quy a/kêu a`) |
| 4 | Kỷ luật Verify (`20a.3` + `20a.5`) | `"stt"` LAN làm chậm Task17; để lại `.invalid-*` | `Verify-Task21.ps1` tự ép `"stt": { "provider": "sapi" }` trong lúc chạy, khôi phục cấu hình gốc và xóa sạch các file `nexus_config.json.invalid-*` mới sinh trong `finally` |
| 5 | Sổ cái ADR (`docs/ADR_REGISTRY.md`) | `ADR-72..75` đang mở | Chốt `ADR-72..75` (`v0.20.1`) và đăng ký `ADR-76` (`v0.21.0`) |

---

## 2. Trạng thái Đồng Kiểm 4 Bước (Co-Verification Protocol)

- [x] **Điểm 1 (Hạ tầng & Selftest)**: Build Release `-warnaserror` đạt `0/0`; `--selftest local-tts` và `--selftest voice-command-map` đạt `PASS 100%` trên PC `bitmappp`.
- [x] **Điểm 2 (Tự rà soát & Zero-BOM)**: `Test-NexusBom` = `0` vi phạm trên 170 file; `\bRegisterHotKey\s*\(` = `1`; cô lập `"stt": "sapi"` và dọn `.invalid-*` trong `finally` của `Verify-Task21.ps1`.
- [x] **Điểm 3 (Chạy `Verify-Task21.ps1` E1–E5 & Thử tay)**: Đã chạy thực tế trên PC `bitmappp` (xem chi tiết §2.1 bên dưới).
- [x] **Điểm 4 (Đồng kiểm độc lập bằng Subagent)**: Đã kích hoạt subagent `research` rà soát độc lập toàn bộ mã nguồn và kịch bản Verify (**ALL PASS 6/6**).

---

## 2.1 Kết quả Đo đạc Thực tế Verify-Task21 trên PC `bitmappp`

| Cổng | Tiêu chí | Kết quả thực tế | Trạng thái |
|---|---|---|---|
| **E1** | Build `0/0`, Zero-BOM, 1 Hotkey, ADR-72..75 locked, ADR-76 registered, 2 selftests | Build 0/0, BOM 0/170, Hotkey 1, ADR chuẩn, selftest `local-tts` PASS, selftest `voice-command-map` PASS | ✅ **PASS** |
| **E2** | `SanitizeForSpeech` <= 1,0 ms, LAN/CLI/SAPI <= 350 ms, 0 B rò rỉ đệm | Sanitize worst **0,018 ms**; lan **15,9 ms**; cli **96,2 ms**; sapi fallback **300,9 ms**; OutstandingBytes = **0 B** | ✅ **PASS** |
| **E3** | Map khẩu lệnh + trại âm <= 1,0 ms, `/tts` commands + `/reload` <= 50 ms, prompt STT | Mapped 28/28 (worst **0,013 ms**); `/tts status` **0,036 ms**; `say` **0,037 ms**; `/reload` **40,3 ms**; prompt mồi tiếng Việt chuẩn | ✅ **PASS** |
| **E4** | WAKE 40 -> 330 px latency (N=10) | **P95 = 15,717 ms** (<= 16,0 ms); P50 = 10,87 ms | ✅ **PASS** |
| **E5** | Private WS <= 45 MB, CPU 0%, dọn file invalid, 0 live calls | Private WS **34,4 MB** (mục tiêu <= 36,5 MB); CPU **0,0%**; 0 file rác; 0 live calls | ✅ **PASS** *(chuỗi cũ chờ Architect)* |


---

## 3. Lệnh chạy nghiệm thu (`Verify-Task21.ps1`)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\tools\Verify-Task21.ps1
```
