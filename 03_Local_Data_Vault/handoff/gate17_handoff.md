# 📦 HỒ SƠ BÀN GIAO — Gate 17 (TASK-17, ADR-67 → `v0.17.0`): `/net` radar + NITRO5-WORKER, micro FIFINE K669 + `/ptt` VAD, clipboard chống khóa chết, [SLA-WARN] WAKE

- **Ca thực thi**: Claude, 2026-11-12 (rạng sáng)
- **Gửi tới**: Claude Code / Nhật Hòa (chạy Verify trên PC `bitmappp`), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-17-net-radar-voice-ptt` — gộp `main` = `v0.16.5` @`2ca96d4` vào nhánh (`8470fa3`, merge thường). Đặc tả: `current_task.md` mục 0 (quyết định của Gemini 2026-11-12) + phần gốc `5e07d39`
- **Chi tiết kỹ thuật**: `qa_report.md` §25
- **Trạng thái build**: Claude **chưa build** trên máy mình (không có .NET SDK); CI Windows runner **xanh ở `6896356`**: build `-warnaserror`, selftest `net-ptt` PASS 29/29 (clipboard với chủ bị treo trả về sau 199,8 ms). Chưa có số đo nào trên máy Nhật Hòa.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Sổ cái | ADR-68 "Dự kiến", ADR-67 "tạm dừng" | ADR-68 **Chốt `v0.16.5`**; ADR-67 đang triển khai (`v0.17.0`); ROADMAP thêm TASK-17 |
| 2 | Đặc tả | Bản `5e07d39` nói LEFT / RIGHT MFD, vòng Alt+Q | Mục 0 chép quyết định 3.6–3.9 của Gemini cho giao diện 2 cột |
| 3 | `/net` | Không có | `/net` = thẻ Radar trong chuỗi hội thoại; `/net port <p>`, `/net ping <host>` = dòng cục bộ; 0 token |
| 4 | NITRO5-WORKER | Chỉ có trong tài liệu | Ping `.22` / `.192` cắt ở 200 ms, không trả lời = `OFFLINE (AWAY)`; dòng tĩnh `⚡ NITRO5 (.22)` dưới Hẹn giờ |
| 5 | `Alt+Q` | Xoay trang LEFT MFD cũ (dải trên chuỗi thẻ) | Lật khung trên cột phải `THẺ ĐÃ GHIM` ↔ `MẠNG // /NET`; không thêm phím toàn cục |
| 6 | MCP | — | `get_network_radar` (Cấp Xanh, `portFilter`) |
| 7 | Micro | Ô F1 "3. Mic 16kHz VAD" không làm gì | `PreferredMicSelector` (FIFINE trước, iVCam / DroidCam / Steam Streaming / Stereo Mix / Virtual Audio bị bỏ qua), `/mic`, `/mic list`, `/mic select`; ô F1 thu âm thật tối đa 30 s |
| 8 | `/ptt` | — | `/ptt test [s]`, `/ptt stop`: waveIn 16 kHz / 16-bit / mono, VAD năng lượng + ZCR, bộ đệm về 0 byte, STT stub 0 token |
| 9 | Clipboard (3.7) | `EmptyClipboard` trên luồng UI: chủ clipboard treo → Cockpit treo | Ghi trên luồng STA ngắn hạn, UI chờ tối đa 200 ms |
| 10 | WAKE (3.9) | Ngưỡng cứng P50 10 / P95 16 ms | Dải [SLA-WARN] VSync trong Verify-Task15 / 16b / 16c: P50 ≤ 13,47, P95 ≤ 22,94 ms (đề xuất, chờ xác nhận) |
| 11 | Kiểm thử | — | Selftest `net-ptt` (CI), `Verify-Task17.ps1` (E1–E5, chuỗi từ Verify-Task16c) |

---

## 2. Trạng thái Gate 17 — ✅ ALL PASS ở lần 2/3 (`1a6c2b0`), chờ Architect đóng dấu `v0.17.0`

**Lần 2/3** (02:59–03:14, cây sạch, CI xanh): E1–E5 ✅. `/net port` LISTENING 1,93 ms, MCP 0,88 ms; NITRO5 ONLINE `.22` 2 ms; FIFINE được chọn; `/ptt` VAD = True, bộ đệm 0 B; clipboard 209,7 ms; RAM 43,7 MB, CPU 0 %; chuỗi 16c → 03 PASS, 0 lượt gọi Gemini thật, không `[SLA-WARN]`. Chi tiết `qa_report.md` §25.7.

**Lần 1/3** (02:38–02:52, HEAD `089fc63` + lần gộp còn dở): FAIL vì cây git dở và 3 lỗi của Claude (nhận cổng LISTENING trên PC, script không đánh thức Cockpit, đếm nhầm `RegisterHotKey`), đã sửa ở `8d1caec`. Chi tiết §25.6.

| # | Bằng chứng | Lần 2/3 |
|---|------------|-----|
| E1 | build `-warnaserror` 0/0, Zero-BOM, parse PS 5.1 mọi `tools\*.ps1`, ADR-67 có, ADR-68 chốt `v0.16.5`, số lệnh `RegisterHotKey` không đổi (4), không animation, selftest `net-ptt` | ✅ |
| E2 | `/net port` LISTENING < 15 ms → FREE; MCP `get_network_radar` < 15 ms; thẻ Radar; NIC vật lý `192.168.1.x` gateway `.1`, không lấy `26.x`; ping ≤ 300 ms; NITRO5 ONLINE hoặc OFFLINE (AWAY); dòng NITRO5 đổi; Alt+Q lật hai chiều; ping TEST-NET 0/3 | ✅ |
| E3 | Selftest bộ chọn + máy thật: **FIFINE K669 cắm sẵn** được chọn, iVCam `[Ignored Virtual Mic]` | ✅ |
| E4 | VAD tổng hợp (im lặng / 440 Hz / 1 kHz / nhiễu), bộ đệm về 0; `/ptt test 2` thật trên FIFINE; `/ptt test 10` dừng sớm; clipboard với chủ bị treo | ✅ |
| E5 | RAM rảnh ≤ 45 MB, CPU 0 %, không timer, micro đóng, chuỗi Verify-Task16c → 15 → 16b → 16 → 13 → 12 → 13a → 14…03 (`-SkipLive`), 0 lượt gọi Gemini thật, git sạch, CI | ✅ |

### Lệnh chạy (máy để rảnh khoảng 20 phút, micro FIFINE cắm sẵn, laptop Nitro 5 bật hay tắt đều được)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-17-net-radar-voice-ptt
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task17.ps1
```
Kết quả: `03_Local_Data_Vault/handoff/task17_verification.json`. Trước khi chạy: nếu Task Manager còn `NEXUSHome.HostService` treo thì End task. Lúc chạy, selftest làm trống clipboard một lần (chữ được khôi phục, ảnh thì không).

### Sau khi Architect đóng dấu (Nhật Hòa làm)
```powershell
cd C:\Agent\NEXUS_Home_Hub
git checkout main
git pull
git merge --ff-only origin/claude/task-17-net-radar-voice-ptt
git push origin main
git tag -a v0.17.0 -m "Gate 17: TASK-17 /net radar + NITRO5, FIFINE mic + PTT VAD (ADR-67)"
git push origin v0.17.0
```

---

## 3. Điểm mở cần Architect quyết định

- **Đã quyết qua đặc tả `089fc63`**: dải [SLA-WARN] WAKE (§2.3) — Claude đã áp đúng quy tắc vào Verify-Task15 / 16b (Verify-Task16c giữ ngưỡng cứng). Ở lần 2/3 không mẫu nào cần tới dải này.
- **(17.1) Cầu chì**: Claude tính lần 1/3 vào cầu chì (có lỗi thật của Claude). Architect quyết lại nếu muốn; không ảnh hưởng kết quả vì lần 2/3 đã ALL PASS.
- **(17.2) Đổi chiều cao cửa sổ**: đường đánh thức không có `UpdateLayout()` thừa; thời gian `WindowHeightMs` là lần vẽ đồng bộ của cửa sổ layered khi đổi kích thước (một nhịp VSync). Không đổi mã; nếu muốn giảm thật thì cần một task đo A/B riêng (giữ cửa sổ cao cố định, đổi lại khoảng +1,3 MB RAM rảnh).
- **(17.3) Alt+Q** không còn mở các trang LEFT MFD cũ (telemetry / agent / iGPU); thông tin vẫn có qua `/status`, `/mem`, `/config`.
- **(17.4) Đích LAN là hằng số** trong code (gateway `.1`, NITRO5 `.22` / `.192`, TV `.97`). Có cần đưa vào `nexus_config.json` không?
- **(17.5) Theo dõi**: Task16b RAM dao động giữa các lần (lần 1/3: 48,7 MB khi cây git dở; lần 2/3: 44,9 MB, sát ngưỡng 45).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Đóng dấu Gate 17 → `v0.17.0`** *(khuyến nghị)* | Nhật Hòa merge + tag theo lệnh ở mục 2 | E1–E5 đủ bằng chứng trên PC |
| B. Task kế tiếp do Architect chọn | Ví dụ: STT cục bộ thật sau `ISpeechToTextProvider`; dịch vụ 5055 / 5056 trên Nitro 5; đưa đích LAN vào cấu hình (17.4); đo A/B đổi chiều cao (17.2) | Đều đã có chỗ gắn sẵn trong mã TASK-17 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. **Verify-Task17 lần 2/3 (`1a6c2b0`) ALL PASS E1–E5** trên PC `bitmappp` (chi tiết `qa_report.md` §25.6–25.7, `gate17_handoff.md`):
> - E2: `/net port` cổng ngẫu nhiên LISTENING **1,93 ms** → FREE; MCP `get_network_radar` 0,88 ms; `/net` → thẻ Radar sau 183 ms: `Ethernet 192.168.1.212`, gateway `.1`, **NITRO5 ONLINE `.22` 2 ms**; dòng `⚡ NITRO5 (.22) · 2 ms`; Alt+Q lật hai chiều; ping TEST-NET 0/3 trong 642 ms.
> - E3: chọn `Mic (FIFINE K669 Microphone)`, bỏ qua `Steam Streaming Microphone`.
> - E4: `/ptt test 2` VAD = True (55/100 khung), bộ đệm còn 0 B; dừng sớm 0,67 s; clipboard với chủ bị treo trả về sau 209,7 ms.
> - E5: RAM rảnh **43,7 MB**, CPU 0 %, chuỗi Verify-Task16c → … → 03 PASS (800 s), **0 lượt gọi Gemini thật**, git sạch, CI xanh. WAKE trong chuỗi: 16c 8,13 / 10,04; 15 8,44 / 11,75; 16b 8,35 / 9,47 ms — không cần tới dải [SLA-WARN].
>
> Lần 1/3 trượt vì PC đang gộp dở (commit đặc tả `089fc63` của bạn chưa đẩy, `git pull` xung đột ở `current_task.md`) cộng 3 lỗi của tôi: Windows trên PC trả `TcpListener` loopback dưới dạng State Unknown / đầu xa `0.0.0.0:0` nên `/net port` báo ESTABLISHED; script không đánh thức Cockpit trước khi kiểm chuỗi; E1 đếm cả chữ "RegisterHotKey" trong chú thích. Đã sửa ở `8d1caec`; PC giữ đặc tả của bạn và đẩy lên `1a6c2b0`. Dải [SLA-WARN] dùng đúng quy tắc §2.3 của bạn ở Verify-Task15 / 16b.
>
> Mời bạn đóng dấu Gate 17 → `v0.17.0`. Điểm mở (không chặn): (17.1) lần 1/3 có tính cầu chì không; (17.2) đường đánh thức không có `UpdateLayout()` thừa, phần `WindowHeightMs` là lần vẽ đồng bộ của cửa sổ layered (một nhịp VSync) — muốn giảm thật cần task đo A/B riêng; (17.3) Alt+Q không còn mở các trang LEFT MFD cũ; (17.4) đích LAN đang là hằng số, có đưa vào `nexus_config.json` không; (17.5) theo dõi RAM Task16b (44,9 MB, sát ngưỡng).
