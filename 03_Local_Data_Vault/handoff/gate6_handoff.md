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

## 2. Trạng thái Gate 6 — ✅ E1–E5 ALL PASS (lần chạy 2)

- **Lần chạy 1** (20:39, commit `e961de2`): build `-warnaserror` 0/0, E1–E4 PASS; E5 trượt chỉ vì `git status` có 6 file agent tự chép (đã vá `.gitignore`).
- **Lần chạy 2** (20:42, commit `a812742`, `-SkipBuild`, code không đổi): **ALL CHECKS PASS (E1–E5)**; `git status` sạch; hồi quy Task05/04/03 ALL PASS (P95 dispatch 2,97 ms, private WS 44,1 MB, CPU 0,13 %).
- **`main` đã được fast-forward tới đầu nhánh TASK-06** (ADR-22 hoàn tất). Chưa gắn tag `v0.6.0` (chờ Architect).
- Chưa đo thật hồ sơ LiveCombat (không có game chạy lúc kiểm thử).

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
| E1 | Build + BOM | `-warnaserror` 0/0; `GEMINI.md`, `current_task.md`, `nexus_config.json`, `latest_dispatch.json` không BOM | ✅ 0/0; cả 4 file không BOM |
| E2 | `--selftest config` + `/reload` thật qua pipe | Tự tạo file; đổi `memoryTrimFloorMb` 50→64 và `defaultNexusMode` → `/reload` áp dụng ngay, `/config` hiển thị đúng; JSON hỏng → `/reload` báo lỗi, HostService vẫn sống, giữ cấu hình cũ, file chuẩn được ghi lại, bản hỏng được sao lưu; trường rỗng giữ giá trị cũ | ✅ riêng biệt + thật trên Cockpit (floor 64 / Sprint; JSON hỏng: HostService sống, giữ 64, file ghi lại, có bản sao lưu; file gốc khôi phục) |
| E3 | `--selftest adr19` | `/fail` ×3 → ESCALATED r3 + 1 dòng; `/fail` lần 4 → r4, ESCALATED, vẫn 1 dòng; `/pass Đã khắc phục xong` → PASSED r4; mở lại PASSED → ESCALATED r5, vẫn 1 dòng; dispatch 2: PASSED → `/fail` → NEEDS_WORK r2 | ✅ #318 r3 (1 dòng) → r4 (1 dòng) → PASSED r4 → r5 (1 dòng); #319 NEEDS_WORK r2 |
| E4 | Mã dùng chung + ADR-17 | Hai project tham chiếu Core, khởi tạo qua `NexusRuntime`, không định nghĩa lại kiểu lõi; fingerprint config của HostService (selftest), Cockpit đang chạy (`/config`) và McpTools (`get_nexus_config`) trùng nhau; hồ sơ SLA cả 3 phía + script trùng nhau | ✅ fingerprint `1bb8041a2925` ở cả 3; Desktop/10 ms ở cả 4 |
| E5 | Git + hồi quy | task-02…05 nằm trong `origin/main`; nhánh này fast-forward được từ `main`; `git status` sạch; Verify-Task05/04/03 ALL PASS (private WS < 60 MB, CPU < 1 %, P95 theo ADR-17) | Lần 1: ❌ chỉ `git status`. **Lần 2 ✅** git sạch, fast-forward được, hồi quy ALL PASS (P95 2,97 ms, WS 44,1 MB, CPU 0,13 %) |

Kết quả gốc: `handoff/task06_verification.json`, `handoff/task06_selftest_*.json`.

**Đã làm sau khi Gate 6 PASS**: fast-forward `main` tới đầu nhánh này. Đề xuất gắn tag `v0.6.0` (theo mục tiêu của đặc tả) khi Architect đồng ý.

---

## 3. Điểm mở cần Architect quyết định

1. **Trường thêm `combatProcessPrefixes`** (mặc định `DeltaForce`, `TslGame`, `SGuard`) để nhận diện LiveCombat, vì `protectedProcessPrefixes` có `explorer`, `dwm`, `NEXUSHome` luôn chạy. Có muốn thêm `ACE-`, `fczf` (FC Online), `Roblox` không?
2. **`nexus_config.json` không đưa vào git** (mỗi máy tự tạo; giá trị mặc định nằm trong code). Đồng ý?
3. **Nghĩa của `memoryTrimFloorMb`**: Claude hiểu là "không dọn RAM tiến trình nền có Working Set ≤ ngưỡng". Đúng ý chưa?
4. **Mâu thuẫn ADR-19 khi mở lại dispatch PASSED đã ≥ 3 strike**: "ghi khi chuyển sang ESCALATED" và "không bao giờ ghi dòng thứ hai cho cùng dispatchId" không thể cùng đúng. Claude chọn không ghi dòng thứ hai. Xác nhận?
5. **Phím tắt cần khởi động lại HostService** mới đổi được (không nằm trong 5 tham số của `/reload`). Có cần đổi phím ngay khi `/reload` không?
6. **Tag `v0.6.0`**: `main` đã chứa TASK-06 (fast-forward sau khi Gate 6 PASS). Gắn tag `v0.6.0` lên commit đó?
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

> Chào Gemini, Gate 6 (TASK-06) đã đủ bằng chứng: trên máy Nhật Hòa, **Verify-Task06 ALL CHECKS PASS (E1–E5)** — build `-warnaserror` 0/0, 4 file không BOM; `nexus_config.json` tự tạo, `/reload` trên Cockpit đang chạy áp dụng ngay, JSON hỏng thì HostService vẫn sống, giữ cấu hình cũ, ghi lại file chuẩn và sao lưu bản hỏng; ADR-19 đúng mọi bước (ESCALATED r3 → r4 vẫn 1 dòng → `/pass` PASSED r4 → mở lại r5 vẫn 1 dòng); HostService, Cockpit và McpTools cùng fingerprint cấu hình và cùng hồ sơ SLA; `git status` sạch; hồi quy Task05/04/03 ALL PASS (P95 dispatch 2,97 ms, private WS 44,1 MB, CPU nền 0,13 %). **ADR-22 hoàn tất: `main` đã được fast-forward tới TASK-06**, chứa toàn bộ TASK-02…06, không merge commit, không mất lịch sử. Lần chạy 1 trượt E5 chỉ vì 6 file agent McpTools tự chép vào `02_Agents_Registry/` (đã thêm vào `.gitignore`). Lưu ý minh bạch: hồ sơ LiveCombat 25 ms chưa được đo thật vì lúc kiểm thử không chạy Delta Force. Chi tiết: `gate6_handoff.md`, `qa_report.md` §8 trên `main`. Với vai Reality Checker: nghiệm thu Gate 6. Với vai Architect: quyết định các điểm ở §3 (`combatProcessPrefixes`, không commit `nexus_config.json`, nghĩa `memoryTrimFloorMb`, mâu thuẫn ADR-19 khi mở lại PASSED, tag `v0.6.0`), rồi chốt TASK-07 (Claude khuyến nghị Hướng A: nối Gemini API vào Cockpit, khóa lưu bằng DPAPI). Nhớ commit đặc tả TASK-07 lên GitHub.
