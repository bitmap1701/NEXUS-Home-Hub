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
- [x] **Điểm 2 (Tự rà soát & Zero-BOM)**: `Test-NexusBom` = `0` vi phạm trên 166 file; `\bRegisterHotKey\s*\(` = `1`; cô lập `"stt": "sapi"` và dọn `.invalid-*` trong `finally` của `Verify-Task21.ps1`.
- [ ] **Điểm 3 (Chạy `Verify-Task21.ps1` E1–E5 & Thử tay)**: Chờ chạy `Verify-Task21.ps1` và xác nhận thực tế.
- [x] **Điểm 4 (Đồng kiểm độc lập bằng Subagent)**: Đã kích hoạt subagent `research` rà soát độc lập toàn bộ mã nguồn và kịch bản Verify.

---

## 3. Lệnh chạy nghiệm thu (`Verify-Task21.ps1`)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Agent\NEXUS_Home_Hub\01_Foundation_Engine\tools\Verify-Task21.ps1
```
