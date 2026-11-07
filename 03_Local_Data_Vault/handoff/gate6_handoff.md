# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 6 (TASK-06) & Nhận TASK-07

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-06-config-consolidation`, tách từ `main` mới (`6255b7f`)
- **`main`**: đã fast-forward tới `6255b7f` (toàn bộ TASK-02…05, Gate 2–5 PASSED)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-06, lấy từ máy Nhật Hòa)
- **Chi tiết kỹ thuật**: `qa_report.md` §8

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Nhánh `main` | Chỉ có TASK-02 (`ed67341`); 4 nhánh xếp chồng | `main` = task-05 (`6255b7f`), fast-forward, không mất lịch sử; TASK-06 sẽ fast-forward tiếp sau khi Gate 6 PASS |
| 2 | Cấu hình | Rải rác: hằng số trong code + biến môi trường (`NEXUS_COCKPIT_MONITOR`, `NEXUS_GAME_PROCESS`) | `03_Local_Data_Vault/config/nexus_config.json` tự tạo; chống lỗi JSON hỏng / trường rỗng; `/reload`, `/config` trên Cockpit |
| 3 | Game Boost | Danh sách bảo vệ cố định; dọn RAM mọi tiến trình nền | Danh sách từ config (vẫn giữ sàn hệ thống cứng); bỏ qua tiến trình ≤ `memoryTrimFloorMb` |
| 4 | Phím tắt / màn hình | Cố định trong code | Đọc từ config (phím: khi khởi động; màn hình: cả khi `/reload`) |
| 5 | Khởi tạo dùng chung | HostService, McpTools, Cockpit, SelfTest mỗi nơi tự dựng DB/registry/dispatcher | Một `NexusRuntime` trong Core cho tất cả; `AtomicFile` dùng chung; MCP tool `get_nexus_config` |
| 6 | Máy trạng thái verdict | Ghi escalation theo `retry_count == 3` | ADR-19: theo chuyển trạng thái (đọc trạng thái cũ trong transaction), một dòng/dispatch, `/pass` từ ESCALATED, mở lại PASSED |
| 7 | SLA dispatch | Cố định 10 ms | ADR-17: Desktop 10 ms / LiveCombat 25 ms, tự nhận diện, script in hồ sơ áp dụng |
| 8 | `git status` | Luôn bẩn sau mỗi lần kiểm thử (file JSON sinh ra) | `.gitignore` cho file kiểm thử, `latest_dispatch.json`, config cục bộ |
| 9 | Kiểm thử | — | Selftest `config`, `adr19`, `sla`; `Verify-Task06.ps1` (E1–E5) |

---

## 2. Trạng thái Gate 6 — ⏳ CHƯA ĐỦ BẰNG CHỨNG (chưa build)

Môi trường của Claude không có .NET SDK và PowerShell: **chưa build, chưa có số liệu đo**. Máy trạng thái ADR-19 đã chạy thử trên SQLite thật (qa_report §8.3).

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
# current_task.md trên máy là bản Gemini ghi nhưng chưa commit (nhánh này đã có bản giống hệt); cất tạm để checkout:
git stash push -m "gemini-local-task06" -- 03_Local_Data_Vault/handoff/current_task.md
git fetch origin
git checkout claude/task-06-config-consolidation
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task06.ps1
```
Script tự tắt HostService để build, bật lại và để nó chạy. E2 sửa tạm `config\nexus_config.json` thật rồi **khôi phục nguyên file**. E5 chạy toàn bộ hồi quy (Verify-Task05 → 04 → 03), mất khoảng 2–3 phút. E5 cần `git status` sạch lúc bắt đầu.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + BOM | `-warnaserror` 0/0; `GEMINI.md`, `current_task.md`, `nexus_config.json`, `latest_dispatch.json` không BOM | ⏳ |
| E2 | `--selftest config` + `/reload` thật qua pipe | Tự tạo file; đổi `memoryTrimFloorMb` 50→64 và `defaultNexusMode` → `/reload` áp dụng ngay, `/config` hiển thị đúng; JSON hỏng → `/reload` báo lỗi, HostService vẫn sống, giữ cấu hình cũ, file chuẩn được ghi lại, bản hỏng được sao lưu; trường rỗng giữ giá trị cũ | ⏳ |
| E3 | `--selftest adr19` | `/fail` ×3 → ESCALATED r3 + 1 dòng; `/fail` lần 4 → r4, ESCALATED, vẫn 1 dòng; `/pass Đã khắc phục xong` → PASSED r4; mở lại PASSED → ESCALATED r5, vẫn 1 dòng; dispatch 2: PASSED → `/fail` → NEEDS_WORK r2 | ⏳ |
| E4 | Mã dùng chung + ADR-17 | Hai project tham chiếu Core, khởi tạo qua `NexusRuntime`, không định nghĩa lại kiểu lõi; fingerprint config của HostService (selftest), Cockpit đang chạy (`/config`) và McpTools (`get_nexus_config`) trùng nhau; hồ sơ SLA cả 3 phía + script trùng nhau | ⏳ |
| E5 | Git + hồi quy | task-02…05 nằm trong `origin/main`; nhánh này fast-forward được từ `main`; `git status` sạch; Verify-Task05/04/03 ALL PASS (private WS < 60 MB, CPU < 1 %, P95 theo ADR-17) | ⏳ |

Kết quả gốc: `handoff/task06_verification.json`, `handoff/task06_selftest_*.json`.

**Sau khi Gate 6 PASS**: Claude fast-forward `main` tới đầu nhánh này (đề xuất gắn tag `v0.6.0` theo mục tiêu của đặc tả).

---

## 3. Điểm mở cần Architect quyết định

1. **Trường thêm `combatProcessPrefixes`** (mặc định `DeltaForce`, `TslGame`, `SGuard`) để nhận diện LiveCombat, vì `protectedProcessPrefixes` có `explorer`, `dwm`, `NEXUSHome` luôn chạy. Có muốn thêm `ACE-`, `fczf` (FC Online), `Roblox` không?
2. **`nexus_config.json` không đưa vào git** (mỗi máy tự tạo; giá trị mặc định nằm trong code). Đồng ý?
3. **Nghĩa của `memoryTrimFloorMb`**: Claude hiểu là "không dọn RAM tiến trình nền có Working Set ≤ ngưỡng". Đúng ý chưa?
4. **Mâu thuẫn ADR-19 khi mở lại dispatch PASSED đã ≥ 3 strike**: "ghi khi chuyển sang ESCALATED" và "không bao giờ ghi dòng thứ hai cho cùng dispatchId" không thể cùng đúng. Claude chọn không ghi dòng thứ hai. Xác nhận?
5. **Phím tắt cần khởi động lại HostService** mới đổi được (không nằm trong 5 tham số của `/reload`). Có cần đổi phím ngay khi `/reload` không?
6. **Thứ tự merge**: TASK-06 fast-forward vào `main` chỉ sau khi Gate 6 PASS trên máy thật (không đẩy code chưa build lên `main`). Đồng ý?
7. **Còn mở từ Gate 5**: CPU nền có đỉnh 3–4 % khoảng mỗi 6 s (nghi timer MFD); thanh trạng thái vẫn hiện `WorkingSet64` thay vì private WS; chữ "Gate: Reality Checker" bị cắt; MFD còn ghi "144 Hz" và "Intel UHD 730" lỗi thời.
8. **Đặc tả vẫn chưa lên GitHub**: `current_task.md` TASK-06 chỉ có trên máy. Lời nhắn giao ca nói "đã commit" nhưng không nhánh nào có; bản Gemini dán qua chat còn bị cắt ở dòng 59.

---

## 4. Đề xuất TASK-07

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Nối Gemini API vào Cockpit** *(khuyến nghị)* | Khóa API lưu bằng DPAPI (`ProtectedData`, CurrentUser) qua lệnh `/apikey` (không bao giờ vào git/log); sau dispatch, gửi SystemInstruction của Primary Agent + nhiệm vụ lên Gemini, hiện câu trả lời trên MSI G272; cấu hình model/timeout trong `nexus_config.json`; QA Agent tự chấm rồi gợi ý `/pass`/`/fail`. | Đây là bước biến dispatch thành câu trả lời thật. Nền móng đã sẵn: config ngoài, runtime dùng chung, vòng Dev-QA, DPAPI đã có trong luật. Cần đo CPU/RAM khi gọi mạng lúc đang chơi. |
| B. Dọn giao diện & hiệu năng nền | Tìm và bỏ đỉnh CPU ~6 s; thanh trạng thái hiện private WS; sửa chữ bị cắt; cập nhật MFD (200 Hz, GPU thật). | Nhỏ, rủi ro thấp; có thể gộp vào A. |
| C. Giọng nói (PCM 16 kHz + VAD) → Dispatch | Mic → Gemini Live → lệnh Cockpit. | Phụ thuộc A (khóa API, kết nối Gemini). |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đã triển khai TASK-06 trên nhánh `claude/task-06-config-consolidation` của https://github.com/bitmap1701/NEXUS-Home-Hub. Hãy đọc `03_Local_Data_Vault/handoff/gate6_handoff.md` và `qa_report.md` §8. Trạng thái: **chưa build** (môi trường Claude không có .NET); Nhật Hòa sẽ chạy `Verify-Task06.ps1` (E1–E5). Đã làm: (1) ADR-22: chuỗi nhánh tuyến tính nên `main` đã được **fast-forward tới đầu task-05** (Gate 2–5), không merge commit, không mất lịch sử; TASK-06 sẽ fast-forward vào `main` sau khi Gate 6 PASS. (2) ADR-20: `nexus_config.json` tự tạo, JSON hỏng thì giữ cấu hình cũ + sao lưu bản hỏng + ghi lại file chuẩn, trường rỗng giữ giá trị cũ; `/reload` và `/config` trên Cockpit. (3) ADR-21: một `NexusRuntime` trong Core cho HostService, McpTools, Cockpit và selftest; MCP tool mới `get_nexus_config` để E4 so fingerprint cấu hình giữa các tiến trình. (4) ADR-19: escalation theo chuyển trạng thái (đọc trạng thái cũ trong transaction), một dòng cho mỗi dispatch; đã chạy thử trên SQLite đúng mọi bước của E3. (5) ADR-17: SLA 10/25 ms tự nhận diện. Điểm cần bạn quyết định (§3): thêm trường `combatProcessPrefixes` (vì `protectedProcessPrefixes` chứa `explorer`/`dwm` luôn chạy), không commit `nexus_config.json`, nghĩa của `memoryTrimFloorMb`, và mâu thuẫn ADR-19 khi mở lại dispatch PASSED đã ≥ 3 strike. Lưu ý: đặc tả TASK-06 **vẫn chưa có trên GitHub** (lời nhắn nói đã commit nhưng không nhánh nào có) — Claude lấy từ máy Nhật Hòa. Claude khuyến nghị TASK-07 Hướng A: nối Gemini API vào Cockpit với khóa lưu bằng DPAPI.
