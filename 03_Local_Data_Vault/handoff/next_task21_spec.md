# TASK-21 (J1): Giọng Nói Phản Hồi Cục Bộ (`LocalTtsService` — LAN/CLI Piper & Fallback SAPI, 0 Token)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-14 00:24. **Chưa bắt đầu**: chờ Gate 20a (`v0.20.1`) qua đủ đồng kiểm 4 bước (gồm thử tay Alt+V tiếng Việt). Khi bắt đầu: chép nội dung này vào `current_task.md`, nhánh tách từ `main` sau tag `v0.20.1`.

- **ADR & Phiên bản đích**: `ADR-76` -> `v0.21.0`
- **Nhánh**: `claude/task-21-local-tts` (BẮT BUỘC tách từ `main` SAU KHI gộp fast-forward và gắn tag mới nhất; đặc tả gốc ghi `970e42a` / `v0.20.0`, nay là `v0.20.1` sau TASK-20a). Nếu Antigravity làm: dùng tiền tố nhánh theo luật của Antigravity trong `GEMINI.md`.
- **Phạm vi (Lean change)**:
  1. Xây dựng `LocalTtsService` đối xứng với `LocalSttService` (`ADR-72`), đọc mục `"tts"` trong `nexus_config.json` (hỗ trợ `/reload`): `{ "enabled": true, "provider": "auto|lan|cli|sapi", "lanEndpoint": "192.168.1.22:5056", "cliPath": "", "cliArgs": "", "voice": "vi-VN", "maxWords": 25 }`.
  2. Bộ lọc `SanitizeForSpeech(string text)` loại bỏ ký tự markdown/code block và cắt gọn `<= maxWords`; tuyến `lan` gửi UTF-8 qua TCP (`<= 200 ms` connect), tuyến `cli` gọi tiến trình ngoài (`piper` / stub), tuyến `sapi` dùng `System.Speech.Synthesis` (giải phóng sạch sau khi đọc hoặc khi `[MEM]`); tuyến `auto` thử `lan -> cli -> sapi` ngoài luồng UI (`0` exception khi thiếu thiết bị phát hoặc tắt worker).
  3. Thêm lệnh cục bộ `/tts [status|on|off|say <text>]` (`0 token`) và bổ sung khẩu lệnh vào `VoiceCommandMapper` (`"bật giọng nói"` -> `/tts on`, `"tắt giọng nói"` -> `/tts off`, `"trạng thái giọng nói"` -> `/tts status`).
- **Cổng nghiệm thu (`Verify-Task21.ps1` trên PC `bitmappp`)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0/0`; BOM = `0` byte; `\bRegisterHotKey\s*\(` = `1`; `.gitignore` khớp `task2*`; chuyển `ADR-73`/`74`/`75` sang `Chốt v0.18.2`/`v0.19.0`/`v0.20.0` và đăng ký `ADR-76`.
  - **E2**: `--selftest local-tts` xác nhận: (a) `SanitizeForSpeech` lọc sạch markdown & cắt gọn `<= maxWords` trong `<= 1,0 ms`; (b) `lan` loopback và `cli` stub nhận đúng câu tiếng Việt UTF-8 trong `<= 300 ms`; (c) khi `lan`/`cli` đóng, tự lùi về `sapi` (ghi ra memory/null stream lúc selftest để không phát tiếng ra loa) trong `<= 350 ms`, giải phóng bộ đệm về `0 B`.
  - **E3**: `/reload`, `/tts status|on|off|say` và các mẫu khẩu lệnh TTS mới trong `VoiceCommandMapper` chạy đúng trong `<= 50 ms`, không chặn luồng UI.
  - **E4**: `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms` (dùng `Test-NexusP95WithRetry`).
  - **E5**: `Private WS` rảnh `<= 45,0 MB` (mục tiêu `<= 36,5 MB`, dải `[SLA-WARN] <= 50,0 MB` khi `GC heap <= 10,5 MB`), `CPU rảnh = 0%`, chuỗi hồi quy `Verify-Task20a -> Verify-Task20 -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.

**Ghi chú của Claude cho người làm TASK-21** (từ bài học TASK-20a):
- Đo thật trước khi đặt ngưỡng: Piper / SAPI trên PC và trên Nitro 5 mất bao lâu cho câu 25 từ? Ngưỡng LAN / CLI của TTS nên đọc từ cấu hình như `stt.lanTimeoutMs`.
- Kiểm tra giọng `vi-VN` có thật trên PC không (SAPI desktop thường chỉ có tiếng Anh; xem lệnh khảo sát trong `ROADMAP_FUTURE.md` J1).
- Thử tay trên thiết bị thật là bắt buộc (đồng kiểm điểm 3): selftest với bộ giả không phát hiện được thiếu giọng / quá chậm.
- **Architect 20a.3 (2026-11-14):** `Verify-Task21.ps1` phải tự dọn các file `03_Local_Data_Vault/config/nexus_config.json.invalid-*` do chính lượt chạy sinh ra (`LastWriteTime -ge` thời điểm bắt đầu lượt Verify), không đụng file cũ hơn.
- **Architect 20a.5 (2026-11-14, duyệt):** `Verify-Task21.ps1` sao lưu `nexus_config.json`, ép `"stt"` = `{"provider":"sapi"}` suốt lượt chạy hồi quy (cô lập khỏi LAN / Nitro 5) và khôi phục nguyên trạng trong khối `finally`; kết hợp dọn `.invalid-*` mới sinh (20a.3).

