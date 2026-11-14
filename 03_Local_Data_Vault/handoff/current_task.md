# TASK-21 (J1 + 20a.7): Giọng Nói Phản Hồi Cục Bộ (`LocalTtsService`) & Chống Trại Âm Khẩu Lệnh Tiếng Việt
- **ADR & Phiên bản đích**: `ADR-76` (kèm cập nhật phụ lục `ADR-73`/`ADR-75`) -> `v0.21.0`
- **Nhánh**: `claude/task-21-local-tts` (tách từ `main` = `f2a31b6` sau khi gắn tag `v0.20.1`)
- **Phạm vi (Lean change)**:
  1. **`LocalTtsService` (`J1`)**: Đọc mục `"tts"` trong `nexus_config.json` (hỗ trợ `/reload`): `{ "enabled": true, "provider": "auto|lan|cli|sapi", "lanEndpoint": "192.168.1.22:5056", "cliPath": "", "cliArgs": "", "voice": "vi-VN", "maxWords": 25 }`. Hàm `SanitizeForSpeech` lọc sạch markdown/code và cắt `<= maxWords`; tuyến `auto` thử `lan` (`<= 200 ms` connect) -> `cli` -> `sapi` (`System.Speech.Synthesis`, giải phóng sạch sau phát hoặc khi `[MEM]`). Thêm lệnh `/tts [status|on|off|say <text>]` (`0 token`).
  2. **Chống trại âm STT (`20a.7`)**:
     - `Start-NitroSttWorker.ps1` và `--stt-worker` truyền mặc định `--prompt "NEXUS, kiểm tra mạng, trạng thái mic, ngữ cảnh, báo cáo qa, hẹn giờ, bật giọng nói, tắt giọng nói."` vào `whisper-cli`.
     - Mở rộng `VoiceCommandMapper`: bổ sung nhóm khẩu lệnh `/tts` và bảng khớp mềm/trại âm thực tế của Whisper `small-q5_1` (ví dụ: `"kiểm tra bạn|mẹ|mạng|mạn"` -> `/net`, `"trạng thái mít|míc"` -> `/mic status`, `"báo cáo quy a|kêu a"` -> `/qa`).
  3. **Kỷ luật Verify (`20a.3` + `20a.5`)**: `Verify-Task21.ps1` tự sao lưu `nexus_config.json`, ép `"stt": { "provider": "sapi" }` trong suốt lúc chạy và khôi phục kèm dọn sạch các file `nexus_config.json.invalid-*` mới sinh trong khối `finally`.
- **Cổng nghiệm thu (`Verify-Task21.ps1` trên PC `bitmappp` + Đồng kiểm 4 bước)**:
  - **E1**: `dotnet build -c Release -warnaserror` = `0/0`; BOM = `0` byte; `\bRegisterHotKey\s*\(` = `1`; chốt `ADR-72..75` (`v0.20.1`) và đăng ký `ADR-76`.
  - **E2**: `--selftest local-tts` xác nhận `SanitizeForSpeech` (`<= 1,0 ms`), `lan` loopback, `cli` stub và fallback `sapi` (xuất memory/null stream lúc test để không kêu ra loa) hoàn tất `<= 350 ms`, `0 B` rò rỉ.
  - **E3**: `--selftest voice-command-map` xác nhận cả khẩu lệnh `/tts` mới lẫn các mẫu trại âm (`"kiểm tra bạn"`, `"kiểm tra mẹ"`, ...) ánh xạ đúng 100% trong `<= 1,0 ms`; `/tts status|on|off|say` và `/reload` phản hồi `<= 50 ms`.
  - **E4**: `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms` (`Test-NexusP95WithRetry`).
  - **E5**: `Private WS` rảnh `<= 45,0 MB` (mục tiêu `<= 36,5 MB`), `CPU rảnh = 0%`, không để lại file `.invalid-*` mới, chuỗi hồi quy `Verify-Task20a -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
