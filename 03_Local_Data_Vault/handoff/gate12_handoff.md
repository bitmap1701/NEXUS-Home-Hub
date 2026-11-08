# 📦 HỒ SƠ BÀN GIAO — Gate 12 (TASK-12: Multimodal Vision `/img` & `/snap`, JPEG ≤ 250 KB, Vision cache, Combat/Privacy Shields, ADR-61)

- **Ca thực thi**: Claude Code trên máy Nhật Hòa (`BITMAPPP`), 2026-11-09, 01:59–02:13 (3 lượt). Ca trước (Claude, không có Windows) viết code tới `8bf3bd2`.
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: **`claude/task-12-multimodal-vision`**, tách từ `main` @`149756e`. Đầu nhánh code là **`3a7b00f`** (bản vá script của ca này); sau đó chỉ có commit tài liệu.
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-12)
- **Chi tiết kỹ thuật và số đo**: `qa_report.md` §19 (§19.4 là kết quả ca này)
- **Trạng thái build**: **đã build** trên máy Nhật Hòa, `-warnaserror` 0/0. CI: xem mục 2, E5.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Chạy trên máy | Chỉ có selftest trên runner CI | 3 lượt `Verify-Task12.ps1` trên máy có Cockpit, clipboard thật, màn hình G272 |
| 2 | E1–E4 | Chưa chạy | ✅ PASS ở cả 3 lượt |
| 3 | E5 | Chưa chạy | Lượt 1: **56,3 MB**, lượt 2: **54,1 MB** ❌ (ngưỡng 52); lượt 3 sau bản vá: **51,6 MB** ✅ |
| 4 | `Verify-Task12.ps1` E5 | Bước chờ ổn định bắt nhầm giai đoạn 54–56 MB ngay sau lần dọn | Bản vá `3a7b00f`: chỉ đọc khi lần dọn đã qua ≥ 10 s, đòi 3 lần đọc liên tiếp lệch ≤ 0,5 MB, ghi mọi lần đọc vào report. Ngưỡng giữ nguyên, không sửa code sản phẩm |
| 5 | Chẩn đoán bộ nhớ | — | Probe: `/img` từ ảnh clipboard đẩy Private WS lên **144,8 MB** trong khoảng 5 s trước lần dọn; khi rảnh dao động ±8 MB |
| 6 | Tài liệu | §19.3 "chưa chạy trên máy" | Thêm §19.4 (bảng 3 lượt, chẩn đoán, số đo lượt 3); viết lại file này |

Không tắt tiến trình nào bằng tay, không đụng `main` hay tag. Trong lúc Verify chạy, Claude không chạy lệnh git nào. Gemini chỉ chạy ở chế độ mock, 0 token. Ảnh chụp G272 chỉ nằm trong RAM. `mfd_pins.json` của Nhật Hòa và chữ trong clipboard đã được khôi phục. Các probe chẩn đoán khởi động lại HostService qua pipe `SHUTDOWN` (như script), và cuối cùng để lại HostService **không có mock**.

---

## 2. Trạng thái Gate 12 — ✅ ALL PASS (E1–E5, lượt 3, `3a7b00f`) — E5 sát ngưỡng, cần Architect xem

### Lệnh chạy
```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-12-multimodal-vision
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task12.ps1
```
Không chạy lệnh git nào khi Verify đang chạy; không copy/paste; không mở cửa sổ ngân hàng hay trình quản lý mật khẩu trên G272. Lượt chạy mất khoảng 4–5 phút, 0 token Gemini.

| # | Bằng chứng | Tiêu chí PASS | Kết quả lượt 3 |
|---|-----------|---------------|---------|
| E1 | Build + BOM + tinh chỉnh Gate 13 | 0/0; 0 BOM; ô Right MFD hết lặp tiêu đề; clipboard có cờ chống Win+V | ✅ 0/0; **102** file, 0 vi phạm; ô thẻ hiện `… / listener.Start();` (bắt đầu từ dòng 2); `exclude=True`, `canIncludeInHistory=0` |
| E2 | JPEG ≤ 256 000 B + `/img` | 2K → JPEG ≤ ngân sách, cạnh ≤ 1280; mock đúng số byte + sha; clipboard chỉ có chữ → `[VISION INFO]`; ảnh clipboard + `--file` trên Cockpit thật | ✅ 8 361 009 → **65 900 B**, 1280×720 q78; nhiễu thuần 236 693 B; mock khớp; sha `07e90604…` giống selftest |
| E3 | Vision cache | 25 lần trùng = 25 trúng, 0 token, P50 ≤ 2,0 / P95 ≤ 3,0 ms; đổi pixel / câu hỏi = trượt | ✅ selftest P50 1,838 / P95 **2,988**; Cockpit thật 25/25, 0 token, P50 1,864 / P95 2,639 ms; trượt đúng khi đổi pixel |
| E4 | Khiên + MCP | `[COMBAT SHIELD]` < 2 ms, 0 lần chụp; `[PRIVACY GUARD]`; MCP bị chặn cả hai, chụp an toàn → Cockpit < 300 ms, foreground không đổi | ✅ P95 **0,131 ms**, 0 lần chụp; chặn "Password"; MCP → Cockpit **157,7 ms**; foreground không đổi |
| E5 | RAM + CPU + hồi quy | Sau tải ảnh 2K + dọn: Private WS ổn định ≤ 52,0 MB; CPU < 0,5 %; tầng nhanh < 90 s; Git; CI | ✅ **51,6 MB** (dư **0,4**); CPU 0 %; **52,6 s**, `[SLA-WARN SUMMARY] Count=0`; HEAD không đổi; CI chưa có lượt cho `3a7b00f` lúc chạy (chưa push) |

### E5 qua ba lượt
| Lượt | Lần dọn (trước → sau) | Đọc được | HUD lúc đọc `MEM` |
|------|-----------------------|----------|--------------------|
| 1 | 97,1 → 56,4 MB | 56,3 ❌ | 47,7 |
| 2 | 97,7 → 54,1 MB | 54,1 ❌ | 46,1 |
| 3 (bản vá) | 96,1 → 54,9 MB | 51,6 / 51,6 / 51,6 ✅ (lần dọn đã qua 10,2 s) | 50,0 |

---

## 3. Điểm mở cần Architect quyết định

1. **Bộ nhớ của đường ảnh (quan trọng nhất).**
   - Sau một phiên dùng vision đầy đủ, Private WS khi rảnh ổn định ở **51,6 MB**: chỉ dư 0,4 MB tới cổng 52, và cao hơn Gate 13 (40,7 MB) khoảng 11 MB.
   - Trước lần dọn, `/img` từ ảnh clipboard đẩy Private WS lên **~145 MB** trong khoảng 5 s, vượt trần 60 MB (ADR-09) trong lúc dùng. Lần dọn mới đưa nó về.
   - Hai hướng:
     - **(a)** *(Claude khuyến nghị)* làm một task nhỏ TASK-12b trước khi thêm tính năng:
       - giải phóng bitmap / mảng byte ngay sau khi nén;
       - gọi dọn ngay sau mỗi `/img` / `/snap`, không chờ 5 s;
       - đo lại bằng E5 với lề an toàn (ví dụ ≤ 48 MB).
     - **(b)** chấp nhận như hiện tại, chỉ theo dõi.
2. **Duyệt bản vá `3a7b00f`** (chỉ sửa script Verify).
   - Bước chờ cũ bắt nhầm giai đoạn 54–56 MB ngay sau lần dọn. Ở lượt 1–2, RAM rơi về 46–48 MB khoảng 5 s sau đó.
   - Bản vá chờ lần dọn đã qua ≥ 10 s và đòi 3 lần đọc ổn định.
   - Điều cần lưu ý: lượt 3 ổn định ở 51,6 MB, không phải 46–48. Nghĩa là mức rảnh thật dao động khoảng 46–52 MB giữa các lượt, và cổng 52 MB đang nằm ngay mép trên.
3. **E3 sát SLA**: selftest P95 2,988 ms (SLA 3,0) ở lượt 3; lượt 1–2 là 2,3 và 2,189 ms. Ngưỡng ms đang chấm cứng vì Verify-Task12 không chạy ở tầng nhanh.
4. Các điểm ca trước nêu (§19.2 mục 1–5, nhánh thử `claude/task-12-ci-probe`) vẫn chờ duyệt; số đo trên máy không mâu thuẫn với chúng.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. TASK-12b: ép bộ nhớ đường ảnh** *(khuyến nghị)* | Giải phóng bitmap / mảng byte sau khi nén, dọn ngay sau `/img` / `/snap`, hạ mục tiêu E5 xuống ≤ 48 MB để có lề | Gate 12 PASS chỉ dư 0,4 MB; đỉnh khoảng 145 MB khi dùng ảnh clipboard |
| B. Merge Gate 12 như hiện tại | Nhật Hòa fast-forward `main` sau khi CI xanh, rồi gắn tag | Mọi tiêu chí đạt theo đúng câu chữ, nhưng lề bộ nhớ quá mỏng cho tính năng kế tiếp |
| C. Dọn nhánh thử | Nhật Hòa xóa `claude/task-12-ci-probe` trên GitHub | Nhánh chỉ dùng để thử trên CI |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, **Gate 12 (TASK-12, ADR-61) đã ALL PASS (E1–E5) ở lượt 3** trên máy Nhật Hòa (Claude Code, 01:59–02:13), nhánh `claude/task-12-multimodal-vision`, code ở `3a7b00f`. **Nhưng E5 chỉ dư 0,4 MB.** Chi tiết ở `gate12_handoff.md` và `qa_report.md` §19.4.
>
> - E1: build 0/0; 102 file, 0 BOM; ô Right MFD hết lặp tiêu đề; clipboard có cờ chống Win+V.
> - E2: PNG 2K 8,36 MB → JPEG 65 900 B 1280×720; sha khớp giữa selftest, ảnh clipboard và `--file` trên Cockpit thật.
> - E3: 25/25 trúng cache, 0 token; selftest P95 2,988 ms (SLA 3,0), Cockpit thật 2,639 ms.
> - E4: `[COMBAT SHIELD]` 0,131 ms, 0 lần chụp; `[PRIVACY GUARD]` chặn "Password"; MCP → Cockpit 157,7 ms, foreground không đổi.
> - E5: lượt 1–2 FAIL (56,3 / 54,1 MB). Bước chờ bắt nhầm giai đoạn ngay sau lần dọn, vì HUD đọc 47,7 / 46,1 MB một giây sau. Bản vá `3a7b00f` (chỉ sửa script) đọc khi lần dọn đã qua ≥ 10 s, đòi 3 lần đọc ổn định. Lượt 3: **51,6 MB** (cổng 52); CPU 0 %; tầng nhanh 52,6 s, `[SLA-WARN SUMMARY] Count=0`.
> - Probe: `/img` từ ảnh clipboard đẩy Private WS lên **~145 MB** trong 5 s trước lần dọn, vượt trần 60 MB trong lúc dùng. Mức rảnh sau phiên vision cao hơn Gate 13 khoảng 11 MB (51,6 so với 40,7).
>
> Cần bạn quyết:
> - (1) làm TASK-12b ép bộ nhớ đường ảnh (giải phóng bitmap sau khi nén, dọn ngay sau `/img` / `/snap`, mục tiêu ≤ 48 MB) trước khi merge, hay merge ngay;
> - (2) duyệt bản vá `3a7b00f`;
> - (3) E3 selftest P95 2,988 ms sát SLA 3,0;
> - (4) các điểm §19.2 của ca trước.
