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

## 2. Trạng thái Gate 20a — ❌ Verify lần 1/3 FAIL (`c666670`, 2026-11-14 01:31): E1–E4 PASS, E5 chỉ hỏng `regressionChain`

**Nguyên nhân (môi trường, không phải mã TASK-20a):** trong chuỗi, Verify-Task17 E4 `/ptt test` thật trên mic FIFINE hỏng `pttLive` / `pttStopEarly` (pttMs 5466, stopMs 3692) vì `nexus_config.json` trên PC nay có `"stt"` → `192.168.1.22:5055` (worker Nitro 5 đang chạy bản cũ, whisper 3 s rồi `ERR`) → mỗi lần thu đi LAN trước rồi mới SAPI. Các Verify cũ chưa cô lập cấu hình `"stt"` của người dùng.
**Cách chạy lại (lần 2/3):** trước Verify: laptop Ctrl+C dừng worker **và** trên PC tạm bỏ mục `"stt"` (khôi phục `nexus_config.json.bak` đã sao lưu trước khi thêm, hoặc xóa mục `"stt"`), chạy Verify-Task20a, xong thì thêm lại `"stt"` (lệnh ở `qa_report.md` §32.6 / tin hướng dẫn) để thử tay. **Architect 2026-11-14 01:34:** lần 1/3 do môi trường, **không tính vào cầu chì**; chạy lại theo cách trên. ✅ **(20a.5) duyệt:** từ Verify-Task21, script Verify tạm đặt `"stt": {"provider":"sapi"}` trong lúc chạy và khôi phục cuối lượt.

**Trên PC (sau khi Claude / Antigravity báo CI xanh):**
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-20a-stt-timeouts
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task20a.ps1
```
Khoảng 95 phút (chuỗi Verify-Task20 → 19 → … → 03). Kết quả: `03_Local_Data_Vault/handoff/task20a_verification.json`.

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, BOM, 1 `\bRegisterHotKey`, `.gitignore` task2*, ADR-72 / 73 `v0.20.1`, ADR-75 chốt `v0.20.0`, selftest | ⏳ |
| E2 | worker trễ 2500 ms → tiếng Việt UTF-8 (lanTimeoutMs 10000); lanTimeoutMs 1000 → cắt đúng hạn ≤ +350 ms → SAPI, PCM 0 B | ⏳ |
| E3 | `lanTimeoutMs` kẹp + Reload ≤ 50 ms, `/ptt status` hiện; `-DryRun` = số nhân vật lý + Ethernet | ⏳ |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms (retry 18b.3) | ⏳ |
| E5 | RAM ≤ 45 (mục tiêu ≤ 36), CPU 0 %, chuỗi 20 → 03 ALL PASS, 0 lượt gọi thật | ⏳ |
| Tay | Alt+V nói "Kiểm tra mạng" → worker Nitro 5 → ô lệnh `/net`, Nhật Hòa báo "ok" | ⏳ |

**Nghiệm thu tay (sau Verify PASS):**
1. Laptop: `cd C:\NEXUS_Home_Hub; git fetch origin; git checkout claude/task-20a-stt-timeouts; git pull; dotnet build .\01_Foundation_Engine\NEXUSHome.McpTools -c Release`
2. Laptop: `powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Start-NitroSttWorker.ps1 -WhisperExe C:\whisper\Release\whisper-cli.exe -Model C:\whisper\ggml-small-q5_1.bin` → phải in `(ethernet, 8 threads)` và `listening on 192.168.1.22:5055 … timeout 12000 ms`.
3. PC: Cockpit `/reload`, `/ptt status` (thấy `lanTimeoutMs 10000`), rồi Alt+V nói "kiểm tra mạng" → ô lệnh `/net`.

---

## 3. Điểm mở cần Architect quyết định

- ✅ **(20a.1) Architect chốt 2026-11-14 01:05:** giữ `lanTimeoutMs` 10000 (1000..30000) + worker `--timeout-ms` 12000 (1000..60000), connect ≤ 200 ms. Nội dung gốc: worker chờ whisper 12000 ms nhưng PC chỉ chờ 10000 ms: một lần nhận dạng 10–12 s làm PC lùi SAPI trong khi worker vẫn giữ khóa một-lần-một → yêu cầu kế có thể nhận `ERR: busy` (chờ ≤ 5 s). Đề xuất: giữ (câu Alt+V thường ≤ 5 s) hoặc đặt worker mặc định 9000 ms (< lanTimeoutMs).
- ✅ **(20a.2) Architect chốt: để nguyên** 3000 ms, ghi chú `stt.cliTimeoutMs` vào `ROADMAP_FUTURE.md` Phase 4 (đã ghi). Nội dung gốc: Đường `cli` chạy whisper ngay trên PC vẫn cố định 3000 ms (ngoài đặc tả). Có cần `stt.cliTimeoutMs` không?
- ✅ **(20a.3) Architect chốt:** Nhật Hòa tự dọn sau khi chốt Gate 20a (xem trước `Get-ChildItem .\03_Local_Data_Vault\config\nexus_config.json.invalid-*`, rồi `Remove-Item`); từ `Verify-Task21.ps1` trở đi script Verify tự dọn đúng các file `.invalid-*` do chính lượt chạy sinh ra (`LastWriteTime -ge` thời điểm bắt đầu). Nội dung gốc: Dọn ~150 file `nexus_config.json.invalid-*` trong `03_Local_Data_Vault/config` do các lượt Verify sinh ra (xóa file = Cấp Đỏ, Nhật Hòa tự làm hoặc cho phép).
- ✅ **(20a.4)** Architect: để mục tối ưu sau — `whisper-server` (giữ mô hình trong RAM, không nạp lại mỗi lần) hoặc bản CUDA trên GPU Nitro 5.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng Gate 20a → `v0.20.1`** *(khuyến nghị)* | CI xanh → Verify-Task20a → thử tay Alt+V tiếng Việt "ok" → Gemini đóng dấu → merge ff + tag | Đồng thời đóng điểm 3 của Gate 20 |
| B. TASK-21 = J1 `LocalTtsService` (ADR-76 → `v0.21.0`) | Đặc tả đã có: `next_task21_spec.md` | Chỉ bắt đầu sau A, nhánh tách từ `main` |

---

## 5. Lời nhắn dán cho Gemini Web

> Claude: **TASK-20a xong mã** trên `claude/task-20a-stt-timeouts` (`qa_report.md` §33, `gate20a_handoff.md`): `stt.lanTimeoutMs` (10000, kẹp 1000–30000, `/reload`, hiện trong `/ptt status`), worker `--timeout-ms` (12000), `Start-NitroSttWorker.ps1` = số nhân vật lý + Ethernet trước + `-DryRun`; selftest `stt-timeouts` + `Verify-Task20a.ps1`. Soát độc lập: 0 BLOCKER, RISK 1–2 đã sửa. Chờ CI xanh → Nhật Hòa chạy Verify lần 1/3 → thử tay Alt+V tiếng Việt.
> Mời quyết: **(20a.1)** worker 12000 > PC 10000 (giữ hay hạ worker về 9000); **(20a.2)** có cần `stt.cliTimeoutMs` cho whisper chạy trên PC; **(20a.3)** cho dọn file `nexus_config.json.invalid-*`.
> Ca sau có thể do **Antigravity** làm theo `docs/AGENT_PLAYBOOK.md` (quy trình đồng kiểm, mẹo tiết kiệm token, bẫy đã gặp).
