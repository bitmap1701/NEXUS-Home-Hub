# TASK-20a (Hotfix): Nới Ngưỡng Timeout STT LAN/Worker Theo Số Đo Thực Tế & Tự Động Tối Ưu Luồng Nitro 5 (`ADR-72` / `ADR-73` -> `v0.20.1`)

> Đặc tả của Gemini Web (Architect), Nhật Hòa chuyển 2026-11-14 00:49, sau khi thử tay Gate 20 bằng worker Whisper thật thất bại vì hết giờ chờ (`qa_report.md` §32.5–32.6). TASK-21 (J1 `LocalTtsService`, `ADR-76` → `v0.21.0`) đã phát đặc tả nhưng chờ TASK-20a + đồng kiểm điểm 3 của Gate 20.

- **ADR & Phiên bản đích**: cập nhật `ADR-72` & `ADR-73` -> `v0.20.1`
- **Nhánh**: `claude/task-20a-stt-timeouts` (tách từ `main` sau khi gộp `v0.20.0`)
- **Phạm vi (Lean change)**:
  1. `"stt"` trong `nexus_config.json` thêm `"lanTimeoutMs": 10000` (kẹp `1000..30000`, nạp nóng `/reload`, giữ `connectTimeout <= 200 ms`); `--stt-worker` thêm `--timeout-ms <int>` (mặc định `12000`, kẹp `1000..60000`).
  2. `Start-NitroSttWorker.ps1`: `-Threads` mặc định = số nhân vật lý (`NumberOfCores` = 8 trên i7-11800H); ưu tiên IPv4 của card `Ethernet` đang `Up` trước `Wi-Fi`.
  3. Selftest: worker giả trễ `2500 ms` vẫn trả đúng câu tiếng Việt UTF-8; worker trễ vượt `lanTimeoutMs` ngắn (`1000 ms`) → `LocalSttService` ngắt đúng hạn và lùi về SAPI (0 exception).
- **Cổng nghiệm thu (`Verify-Task20a.ps1` trên PC `bitmappp` + đồng kiểm 4 bước)**:
  - **E1**: build `-warnaserror` 0/0; BOM; `\bRegisterHotKey\s*\(` = 1; cập nhật `ADR-72` / `ADR-73` (`v0.20.1`).
  - **E2**: worker trễ 2500 ms nhận đúng tiếng Việt UTF-8; quá hạn `lanTimeoutMs` lùi SAPI đúng hạn (sai số ≤ +350 ms), PCM 0 B.
  - **E3**: `/reload` cập nhật nóng `lanTimeoutMs` (kẹp 1000..30000), `/ptt status` hiện `lanTimeoutMs`, script worker chọn đúng số nhân vật lý + ưu tiên Ethernet.
  - **E4**: WAKE 40 → 330 px P50 ≤ 10,0 / P95 ≤ 16,0 ms (`Test-NexusP95WithRetry`).
  - **E5**: Private WS rảnh ≤ 45,0 MB (mục tiêu ≤ 36,0), CPU rảnh 0 %, chuỗi `Verify-Task20 → … → 03` `-SkipLive` ALL PASS, `zeroLiveCalls`.
  - **Nghiệm thu tay (đồng kiểm điểm 3)**: Alt+V trên Cockpit thật, nói "Kiểm tra mạng" qua mic FIFINE K669 → worker Nitro 5 (`192.168.1.22:5055`, 8 luồng) → ô lệnh hiện `/net`, Nhật Hòa báo "ok".
