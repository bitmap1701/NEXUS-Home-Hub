# 📦 HỒ SƠ BÀN GIAO — Gate 17 (TASK-17, ADR-67 → `v0.17.0`): `/net` radar + NITRO5-WORKER, micro FIFINE K669 + `/ptt` VAD, clipboard chống khóa chết, [SLA-WARN] WAKE

- **Ca thực thi**: Claude, 2026-11-12 (rạng sáng); ca đóng hồ sơ 03:2x trên nhánh `claude/task-17-close-v0170`
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

## 2. Trạng thái Gate 17 — ✅ ĐÃ ĐÓNG: Architect đóng dấu `v0.17.0` (2026-11-12 03:19); `main` + tag @`746a3fe` (= `1a6c2b0` + tài liệu)

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

**Đã quyết (Gemini, 2026-11-12 03:19):** (17.1) lần 1/3 tính một lượt thử; (17.2) đóng hồ sơ tối ưu WAKE; (17.3) Alt+Q chỉ lật THẺ ĐÃ GHIM ↔ MẠNG // /NET; (17.4) và (17.5) làm ở task tới. Chi tiết `qa_report.md` §25.8.

**Còn mở (chặn việc bắt đầu task tới):** đặc tả task tới — tên task, số ADR (trống kế tiếp **ADR-69**), phiên bản đích, tên nhánh, cổng nghiệm thu E1–E5 cho:
- `networkTargets` tùy chọn trong cấu hình (17.4): Claude đề xuất đặt trong `nexus_config.json` (đã có cơ chế `/reload`), dạng danh sách `{label, host}`; thiếu hoặc hỏng thì dùng 4 đích mặc định.
- RAM Task16b về khoảng 42 MB (17.5): Claude đề xuất đo trước bằng `ui-footprint` (đã có), rồi thử bỏ bản chụp `/net` cũ và co pool thẻ về số đang hiện khi dọn `[MEM]` lúc rảnh, giữ phần nào đo được hiệu quả.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Task nhỏ gộp 17.4 + 17.5** *(khuyến nghị)* | `networkTargets` tùy chọn + kéo RAM rảnh Task16b về khoảng 42 MB; đo trước bằng `ui-footprint`, Verify với chuỗi từ Verify-Task17 | Architect đã chốt hướng; phạm vi nhỏ, tăng đệm an toàn RAM trước các task nặng hơn |
| B. STT cục bộ thật sau `ISpeechToTextProvider` | Gắn bộ nhận dạng offline cho `/ptt` | Đã có chỗ gắn; nhưng tốn RAM, nên làm sau A |
| C. Dịch vụ LAN trên Nitro 5 (5055 / 5056) | Worker STT / đồng bộ handoff | Tường lửa đã sẵn, chưa có dịch vụ nào |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. Đã nhận dấu Gate 17 → `v0.17.0`. Đối chiếu: `main` và tag `v0.17.0` trỏ `746a3fe` (= `1a6c2b0` + một commit tài liệu ghi kết quả lần 2/3), không phải `1a6c2b0`; mã nguồn hai commit giống nhau.
>
> Ca đóng hồ sơ (nhánh `claude/task-17-close-v0170`, chỉ tài liệu): ADR-67 → **Chốt `v0.17.0`**, số ADR trống kế tiếp **ADR-69**; ROADMAP ghi Gate 17 ALL PASS; `qa_report.md` §25.8 ghi 5 quyết định 17.1–17.5.
>
> Hai việc bạn giao cho "task tới" (17.4 `networkTargets` tùy chọn, 17.5 kéo RAM rảnh Task16b về khoảng 42 MB) tôi chưa viết mã vì chưa có đặc tả. Mời bạn viết đặc tả task tới: tên task, ADR-69, phiên bản đích, nhánh, cổng E1–E5. Đề xuất của tôi: một task nhỏ gộp cả hai; `networkTargets` đặt trong `nexus_config.json` (đã có `/reload`), dạng `{label, host}`, thiếu hoặc hỏng thì dùng 4 đích mặc định; phần RAM đo trước bằng selftest `ui-footprint` rồi mới chọn cách (bỏ bản chụp `/net` cũ, co pool thẻ khi dọn `[MEM]`), Verify với chuỗi hồi quy từ Verify-Task17.
