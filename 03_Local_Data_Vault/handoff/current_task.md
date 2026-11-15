# 📋 ĐẶC TẢ TASK-22: THỰC THI KHẨU LỆNH RẢNH TAY PHÂN TẦNG (SAFE HANDS-FREE AUTO-RUN) & KHỚP MỜ NGỮ ÂM TIẾNG VIỆT

- **Mã Task**: `TASK-22` (`J2 + 20a.7` mở rộng)
- **ADR & Phiên bản đích**: `ADR-77` (cập nhật phụ lục `ADR-75`) -> `v0.22.0`
- **Nhánh thực thi**: `claude/task-22-safe-handsfree-autorun` (tách từ `main` @ `v0.21.0` / `7b9f4fd`)
- **Đơn vị thực thi**: Antigravity (Local IDE & OS Executor)
- **Đơn vị thẩm định & nghiệm thu**: Gemini Web (Architect & Reality Checker) + Nhật Hòa

---

## 1. Mục tiêu & Phạm vi (Lean Change)

1. **Khớp mờ ngữ âm có rào chắn (`VoiceCommandMapper` <= 0,1 ms, 0 Regex, 0 Heap Alloc)**:
   - Thêm các biến thể dị âm từ thực tế Whisper `small-q5_1`: `"kiem cho me"`, `"kiem cho may"`, `"kiem cho mang"`, `"kiem tra may"`, `"kiem cho ban"` -> `/net`.
   - Bổ sung hàm khớp mờ ngữ âm `TryFuzzyMatch`:
     - Rào chắn an toàn: Chỉ xét chuỗi ngắn (`<= 28` ký tự sau khi chuẩn hóa), có tiền tố bắt đầu bằng từ khóa khẩu lệnh hợp lệ (`"kiem "`, `"trang thai "`, `"bao cao "`, `"hen gio "`, `"bat giong "`, `"tat giong "`, `"ngu canh"`).
     - Thuật toán Levenshtein distance chạy hoàn toàn trên ngăn xếp (`stackalloc int`), 0 cấp phát heap, giới hạn khoảng cách sửa đổi `<= 2`.
     - Tuyệt đối giữ nguyên câu hội thoại tự do dài hoặc khác tiền tố (`False Positive = 0`).
2. **Phân tầng An toàn (`CommandSafetyTier`) & Tự chạy Rảnh tay (`Safe Hands-Free Auto-Run`)**:
   - `VoiceCommandMapper.TryMap` trả về `VoiceCommandMatch` kèm `CommandSafetyTier`:
     - `SafeReadOnly`: `/net`, `/net port <N>`, `/mic status`, `/ctx`, `/qa`, `/tts status`, `/tts on`, `/tts off`, `/ptt status`, `/timer <N>m`.
     - `ConfirmRequired`: câu hội thoại tự do hoặc lệnh có tác động thay đổi hệ thống.
   - Mở rộng mục `"stt"` trong `nexus_config.json`: thêm `"autoRunSafeCommands": true` (mặc định `true`), hỗ trợ `/reload`, hiển thị trong `/ptt status`, bật/tắt qua `/ptt autorun on|off`.
   - Khi `Alt+V` nhận diện ra lệnh `SafeReadOnly` và `autoRunSafeCommands == true`: Cockpit tự động thực thi ngay lệnh cục bộ đó mà không cần nhấn `Enter`, xóa sạch `InputBox.Text`, đồng thời nếu `LocalTtsService` đang bật (`Enabled == true`) thì gọi đọc tóm tắt kết quả (`SanitizeForSpeech <= maxWords`) ở luồng nền. Nếu là `ConfirmRequired` hoặc `autoRunSafeCommands == false`: giữ nguyên hành vi điền vào `InputBox.Text` chờ nhấn `Enter`.
3. **Kỷ luật Verify Gate 22**:
   - Chốt `ADR-76` (`v0.21.0`) và đăng ký `ADR-77` (`v0.22.0`) trong `docs/ADR_REGISTRY.md`.
   - Script `Verify-Task22.ps1` tự sao lưu `nexus_config.json`, ép `"stt": { "provider": "sapi" }` trong lúc chạy, tự dọn sạch các file `nexus_config.json.invalid-*` mới sinh trong khối `finally`.

---

## 2. Tiêu chí Nghiệm thu (E1–E5)

- **E1**: `dotnet build -c Release -warnaserror` = `0/0`; Zero-BOM = `100%`; `\bRegisterHotKey\s*\(` = `1`; `ADR-76` locked, `ADR-77` registered.
- **E2**: `--selftest voice-command-map` xác nhận:
  - Các mẫu trại âm (`"Kiếm cho mẹ."`, `"Kiếm cho mày."`, `"Kiểm tra máy?"`) và các mẫu lệch 1–2 ký tự khớp chuẩn 100% kèm `SafetyTier = SafeReadOnly`.
  - `>= 10` câu hội thoại tự do (ví dụ `"Kiếm cho tôi tài liệu C# trên mạng"`, `"Mẹ ơi kiểm tra bài giúp con"`) không bị bắt nhầm (`False Positive = 0`).
  - Toàn bộ bảng chạy trong `<= 1,0 ms` (từng mẫu `<= 0,1 ms`).
- **E3**: `--selftest handsfree-autorun` xác nhận:
  - Khi `autoRunSafeCommands = true`, khẩu lệnh `"Kiếm cho mẹ."` tự động kích hoạt `/net` trong `<= 250 ms`, `InputBox.Text` rỗng, phát TTS tóm tắt kết quả (ghi memory stream lúc test).
  - Khi `autoRunSafeCommands = false` hoặc câu hỏi tự do, chỉ điền vào `InputBox.Text` và không tự thực thi.
  - `/ptt autorun on|off` và `/reload` phản hồi `<= 50 ms`.
- **E4**: `WAKE 40 -> 330 px` đạt `P50 <= 10,0 ms`, `P95 <= 16,0 ms`.
- **E5**: `Private WS` rảnh `<= 45,0 MB`, `CPU rảnh = 0%`, không sót `.invalid-*`, chuỗi hồi quy `Verify-Task21 -> ... -> 03` `-SkipLive` đạt `ALL PASS`, `zeroLiveCalls = True`.
- **Điểm 3 Đồng kiểm thực tế**: Nhật Hòa bấm `Alt+V` nói *"Kiểm tra mạng"* (hoặc *"Kiếm cho mẹ"*) -> Cockpit tự chạy ngay `/net` không cần bấm Enter và đọc kết quả ra loa.
