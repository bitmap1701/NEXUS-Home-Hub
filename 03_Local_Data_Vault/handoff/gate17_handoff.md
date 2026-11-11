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

## 2. Trạng thái Gate 17 — ❌ lần 1/3 FAIL (gộp dở trên PC + 3 lỗi của Claude, đã sửa); chờ lần 2/3

**Lần 1/3** (02:38–02:52, HEAD `089fc63` + lần gộp còn dở): E3 ✅ (FIFINE được chọn), E4 ✅ (`/ptt` thật VAD = True, bộ đệm 0 B, dừng sớm 0,69 s, clipboard 204,9 ms), RAM 44,4 MB ✅, NITRO5 ONLINE `.22` 2 ms. Trượt: E1 `selftest` + `noNewHotkey`, E2 cổng LISTENING + dòng chuỗi, E5 chuỗi (git không sạch vì gộp dở; Task16b RAM 48,7 MB). Chi tiết `qa_report.md` §25.6.

| # | Bằng chứng | Lần 2/3 cần |
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

- **(17.1) Số của dải [SLA-WARN] WAKE** (quyết định 3.9 chỉ nói "chuẩn hóa"): Claude đặt P50 cứng = 10 + ½ khung 144 Hz = **13,47 ms**, P95 cứng = 16 + 1 khung = **22,94 ms**; trong ngân sách cũ là đạt sạch, giữa hai mức là đạt kèm `[SLA-WARN]`. Hai số lần 3/3 của Gate 16c (P95 17,69; P50 10,27) nằm trong dải cảnh báo. Xin xác nhận hoặc cho số khác.
- **(17.2) Giảm thời gian đổi chiều cao cửa sổ chưa làm**: `WindowHeightMs` (5–11 ms) là lời gọi đổi kích thước một cửa sổ layered (`AllowsTransparency=True`): hệ điều hành cấp lại bề mặt và vẽ lại. Hướng khả dĩ là giữ cửa sổ cao cố định và để phần dưới trong suốt (pixel trong suốt của cửa sổ layered không bắt chuột), nhưng bề mặt 330 px thường trực tốn thêm khoảng 1,3 MB RAM lúc rảnh, sát ngưỡng 45 MB. Claude đề xuất: thêm công cụ đo A/B trên máy (giống `Measure-CockpitFootprint`) trước, rồi mới quyết có đổi không. Làm trong Gate 17 hay task riêng?
- **(17.3) Alt+Q**: theo 3.8 nay lật cột phải; các trang LEFT MFD cũ (telemetry WAL, agent, iGPU) không còn phím, thông tin vẫn có qua `/status`, `/mem`, `/config`. Có cần giữ lối vào nào khác không?
- **(17.4) Đích LAN là hằng số** trong code (gateway `.1`, NITRO5 `.22` / `.192`, TV `.97`), đúng tài liệu Nitro 5. Có cần đưa vào `nexus_config.json` để đổi không cần build không?
- **(17.5) Laptop**: TASK-17 không mở dịch vụ nào trên Nitro 5 (đặc tả không yêu cầu); radar chỉ dùng ICMP ping. Cổng 5055 / 5056 vẫn chưa có dịch vụ nghe, Redis chưa cài (đúng `docs/NITRO5_WORKER_NODE.md` mục 5).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Verify-Task17 lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2, FIFINE cắm sẵn, máy rảnh 20 phút | Lấy số đo thật đầu tiên của Gate 17 |
| B. Song song: Architect trả lời 17.1–17.4 | Nhất là 17.1 (số [SLA-WARN]) vì chuỗi hồi quy dùng ngay | Tránh phải chạy lại chỉ vì đổi ngưỡng |
| C. Sau `v0.17.0` | Đo A/B đổi chiều cao cửa sổ (17.2); hoặc STT cục bộ thật sau `ISpeechToTextProvider` | Theo thứ tự Architect chọn |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. Đã nhận dấu Gate 16c → `v0.16.5` và 4 quyết định. Lưu ý đối chiếu: nhánh `claude/task-17-net-radar-voice-ptt` **chưa có đặc tả mới** (vẫn `8a03815`, nền `v0.16.0`, còn LEFT/RIGHT MFD). Tôi gộp `main` vào nhánh (merge thường) và chép 4 quyết định của bạn vào `current_task.md` mục 0; ADR-68 → "Chốt v0.16.5", ADR-67 → đang triển khai.
>
> **TASK-17 đã viết xong, CI xanh (`6896356`, selftest `net-ptt` 29/29), chờ Verify trên PC** (chi tiết `qa_report.md` §25, `gate17_handoff.md`):
> - `/net` → 1 thẻ Radar trong cột hội thoại (NIC vật lý gateway `.1`, bỏ Radmin `26.x`; ping song song cắt 200 ms tới gateway, NITRO5 `.22`/`.192`, TV `.97`; cổng nghe tiêu biểu). `/net port`, `/net ping` → dòng `cục bộ · 0 token`. Dòng tĩnh `⚡ NITRO5 (.22)` dưới Hẹn giờ; Alt+Q lật `THẺ ĐÃ GHIM` ↔ `MẠNG // /NET`. MCP `get_network_radar`.
> - Micro: chọn FIFINE K669 trước, bỏ qua iVCam/DroidCam/Steam Streaming/Stereo Mix/Virtual Audio; `/mic`, `/mic list`, `/mic select`; `/ptt test [s]` thu waveIn 16 kHz mono, VAD năng lượng + ZCR, bộ đệm về 0 byte, STT stub 0 token. Ctrl+Shift+Space vẫn không mở micro.
> - 3.7: clipboard ghi trên luồng STA ngắn hạn, UI chờ tối đa 200 ms.
> - 3.9: dải [SLA-WARN] VSync trong E4 của Verify-Task15/16b/16c.
>
> **Xin bạn quyết:** (17.1) số của dải [SLA-WARN] tôi đặt: P50 cứng 13,47 ms (10 + ½ khung 144 Hz), P95 cứng 22,94 ms (16 + 1 khung) — xác nhận hay số khác? (17.2) phần **giảm thời gian đổi chiều cao cửa sổ** tôi chưa sửa mã vì không đo được ở đây và đây là đường đã qua Gate 16c; đề xuất đo A/B trên máy trước (phương án giữ cửa sổ cao cố định + phần dưới trong suốt, đổi lại khoảng +1,3 MB RAM rảnh) — làm trong Gate 17 hay task riêng? (17.3) Alt+Q không còn mở các trang LEFT MFD cũ (thông tin vẫn có qua `/status`, `/mem`) — được không? (17.4) đích LAN đang là hằng số theo tài liệu Nitro 5 — có cần đưa vào `nexus_config.json`?
>
> Bước tiếp: Claude Code / Nhật Hòa chạy **Verify-Task17 lần 1/3** (FIFINE cắm sẵn, máy rảnh 20 phút).
