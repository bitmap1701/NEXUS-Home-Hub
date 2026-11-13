# NEXUS Home Hub — Sổ tay vận hành cho agent thực thi (Claude / Antigravity)

> Claude viết 2026-11-14 sau các Gate 17 → 20a, để Antigravity (hoặc Claude ở ca sau) làm tiếp cùng một cách. Luật gốc vẫn là `GEMINI.md` (phần cứng, Minimal Change, Xanh / Vàng / Đỏ, cầu chì 3 lần) và `CLAUDE.md`; sổ này là **cách làm** đã chạy ổn, kèm các bẫy đã gặp.

## 1. Vai trò và vòng đời một task

| Vai | Ai | Làm gì |
|-----|----|--------|
| Architect / Reality Checker | Gemini Web | Phát đặc tả `TASK-N` (ADR, phiên bản đích, E1…E5), quyết điểm mở, đóng dấu tag |
| Thực thi | Antigravity (IDE trên PC) hoặc Claude (đám mây, **không** có dotnet) | Viết mã + selftest + `Verify-TaskN.ps1` + tài liệu, đẩy nhánh |
| Người quyết | Nhật Hòa | Chạy Verify trên PC `bitmappp`, thử tay trên Cockpit thật, merge ff + gắn tag, mọi thao tác Cấp Đỏ |

Vòng đời (không bỏ bước):
1. Nhận đặc tả → chép vào `03_Local_Data_Vault/handoff/current_task.md`.
2. **Kiểm 4 điểm đồng kiểm** (mục 2). Thiếu điểm nào: dừng, báo Nhật Hòa, soạn lời nhắn Gemini, **không viết mã**.
3. Nhánh mới tách từ `main` (`git fetch; git checkout -b <prefix>/task-N-<mô-tả> origin/main`).
4. Đọc đúng phần mã liên quan → viết mã nhỏ nhất đủ đặc tả → selftest headless → `Verify-TaskN.ps1` (chép từ Verify của task trước, đổi E1–E3, giữ E4 / E5, chuỗi hồi quy gọi Verify của task trước).
5. **Soát độc lập** bằng một agent / phiên khác chưa thấy quá trình viết (mục 4). Sửa mọi BLOCKER.
6. Đẩy mã **cùng** tài liệu một lần → chờ CI xanh **đúng commit đó**.
7. Nhật Hòa chạy Verify trên PC → đọc `taskN_verification.json` khi FAIL (chỉ các trường `pass` / `checks` sai).
8. Nhật Hòa thử tay trên Cockpit thật, báo "ok".
9. Gemini đóng dấu → Nhật Hòa `git checkout main; git pull; git merge --ff-only origin/<nhánh>; git tag vX; git push origin main vX`.
10. Cuối ca: `qa_report.md` (phát hiện / bản vá / trạng thái), `gate<N>_handoff.md` (khung 5 mục), lời nhắn Gemini.

## 2. Đồng kiểm 4 điểm (bắt buộc trước task mới)
1. Gate hiện tại có một lượt Verify **ALL PASS trên PC** ("đóng dấu theo bằng chứng" chỉ khi hết cầu chì 3/3, hoặc lỗi nằm ở script của agent và bản sửa đã chạy lại PASS).
2. `main` đã fast-forward và tag Gate trước đã gắn (`git fetch --tags; git tag -l; git log origin/main -1`).
3. Nhật Hòa đã thử tay trên Cockpit thật và báo "ok".
4. Trước khi giao Verify: agent độc lập soát mã + script; mọi BLOCKER sửa xong và CI xanh.

**Bài học Gate 20:** Verify ALL PASS bằng bộ nhận dạng giả, nhưng thử tay thật thất bại (PC thiếu mục `"stt"`, SAPI chỉ có tiếng Anh, whisper thật chậm hơn ngưỡng). Điểm 3 không được bỏ qua.

## 3. Tiết kiệm token (Nhật Hòa yêu cầu, áp cho cả Gemini)
- **Phân loại việc trước, chọn model theo độ khó:** câu hỏi / sửa chữ / đọc kết quả → model nhẹ; viết mã nhiều file, thiết kế, gỡ lỗi khó → model mạnh; agent soát độc lập có thể dùng model khác để có góc nhìn khác.
- Chỉ đọc phần cần: `grep -n`, khoảng dòng, script nhỏ trích JSON. Không đọc trọn file dài (`qa_report.md` > 2400 dòng).
- Gộp sửa nhiều chỗ vào một lần (một script Python / một lệnh), chạy các kiểm tra độc lập song song; không đọc lại file vừa sửa.
- Chờ CI bằng **một** vòng chờ nền dài, không nhắn trạng thái mỗi lần hỏi.
- Đẩy mã và tài liệu **cùng một push** (workflow hủy lượt CI đang chạy khi có push mới).
- Trước khi dùng tên trường / API: grep trong mã cho chắc (tránh tốn một lượt CI + 90 phút Verify).
- Mỗi bước: đưa Nhật Hòa **tất cả** lệnh + câu hỏi trong **một** tin; trả lời ngắn (kết quả, lệnh, lời nhắn Gemini).
- Mỗi Gate / task một chat mới; trạng thái nằm trong file handoff, không trong trí nhớ chat.
- Lời nhắn cho Gemini: ngắn, có mã commit + mục `qa_report`, chỉ hỏi điều cần quyết.

## 4. Soát độc lập (mẫu yêu cầu)
Giao cho agent / phiên khác, chỉ cho xem `git diff` + file mới, **không cho sửa file**:
- Lỗi build `-warnaserror` + nullable; mọi tên API / trường dùng tới có thật không.
- Script / selftest cũ trong chuỗi hồi quy có bị vỡ mẫu (grep các `-match` / `-like` của Verify-Task cũ, chuỗi `/ptt status`, dòng ADR).
- Cú pháp PowerShell 5.1, tên trường JSON đọc từ báo cáo selftest, BOM.
- Báo theo mức BLOCKER / RISK / NIT kèm file:dòng.
Lần nào cũng bắt được lỗi thật (Gate 19: regex đếm nhầm `UnregisterHotKey`; Gate 20: lượt đầu chưa làm ấm; Gate 20a: nhận biết hết giờ đọc, PS 5.1 dừng vì stderr).

## 5. Bẫy đã gặp (đọc trước khi viết Verify / selftest)
| Bẫy | Cách tránh |
|-----|-----------|
| Mã hóa | `.ps1` UTF-8 **có** BOM; mọi file khác **không** BOM; comment XML / XAML / csproj không chứa `--` |
| File bằng chứng mới làm `git status` bẩn → cả chuỗi FAIL `gitUnchanged` (Gate 20 lần 1/3) | Thêm mẫu `taskN*_selftest_*.json`, `taskN*_verification.json` vào `.gitignore` ngay khi đặt tiền tố mới (đã có `task0*`, `task1*`, `task2*`) |
| PS 5.1: `$ErrorActionPreference = 'Stop'` + lệnh ngoài ghi stderr → dừng cả Verify | Tạm `Continue`, `2>$null`, chỉ đọc dòng cần (ví dụ dòng JSON) |
| PS 5.1: tiến trình con in mọi `Write-Host` ra stdout | Chế độ `-DryRun` / JSON phải in **duy nhất** dòng JSON trước `exit` |
| Regex tên hàm khớp cả hàm khác (`RegisterHotKey` khớp `UnregisterHotKey`) | `\bTên\s*\(`, `-CaseSensitive`, bỏ dòng `extern` / comment |
| Vi đo (µs / ms) lượt đầu chậm do JIT | Làm ấm mọi nhánh trước khi đo |
| P95 bị một mẫu đột biến của OS | Luật 18b.3: `Test-NexusP95WithRetry` (đo lại một lần, PASS lần lại = PASS + `[SLA-WARN]`) |
| Selftest gọi UI trực tiếp, `await` không quay về luồng UI | Đặt `DispatcherSynchronizationContext` như phím bấm thật |
| Số thập phân trên PC vi-VN là `1003,2` | Khi parse chuỗi do mã in ra: đổi `,` → `.` hoặc dùng InvariantCulture |
| E5 kiểm CI theo `head_sha` = HEAD trên PC | Chỉ giao Verify khi CI của **đúng** commit đó đã xanh; push tài liệu sau đó không ảnh hưởng lượt đang chạy |
| Ngưỡng đặt khi chưa có số đo thật (STT LAN 2000 / 3000 ms) | Đo trên thiết bị thật trước, đưa ngưỡng vào `nexus_config.json` thay vì hằng số |
| Script tự chọn nhầm card mạng (Wi-Fi thay vì dây) | Ưu tiên Ethernet đang Up; cho tham số `-Bind` |

## 6. Phần cứng và mạng nhà (đã khảo sát 2026-11-13 / 14)
| Thiết bị | Thông tin |
|----------|-----------|
| PC `bitmappp` | 192.168.1.212, repo `C:\Agent\NEXUS_Home_Hub`, cấu hình `03_Local_Data_Vault\config\nexus_config.json` (đã có mục `"stt"` → `192.168.1.22:5055`) |
| Laptop Nitro 5 | i7-11800H 8 nhân; Ethernet 192.168.1.22 (WoL đã bật trong Windows, chưa thử gói magic thật), Wi-Fi 192.168.1.192; repo `C:\NEXUS_Home_Hub`; whisper.cpp v1.9.5 CPU `C:\whisper\Release\whisper-cli.exe` + `C:\whisper\ggml-small-q5_1.bin`; tường lửa "NEXUS STT 5055" (LocalSubnet) |
| TV Samsung UA43K5520 (Tizen 2016) | 192.168.1.97, cổng 8001 / 8002 mở → J4a qua WebSocket LAN |
| Bộ IR Hunonic | 192.168.1.135, cổng 6668 đóng, chỉ đi qua mây (trả phí) → J4b gác |
| MAC thiết bị | **Không** ghi vào repo (repo public); để trong `nexus_config.json` trên máy |

Worker STT: laptop chạy `Start-NitroSttWorker.ps1 -WhisperExe C:\whisper\Release\whisper-cli.exe -Model C:\whisper\ggml-small-q5_1.bin` (từ TASK-20a tự chọn Ethernet + 8 luồng); số đo: câu 3 s mất 2,4 s (8 luồng) / 3,6 s (4 luồng).

## 7. Trạng thái khi bàn giao (2026-11-14 ~01:30)
- Tag mới nhất: `v0.20.0` (Gate 20, `VoiceCommandMapper`). Đồng kiểm điểm 3 của Gate 20 **chưa xong** (thử tay tiếng Việt chờ TASK-20a).
- Đang làm: **TASK-20a** (`v0.20.1`) trên `claude/task-20a-stt-timeouts` — mã + selftest + Verify xong, soát độc lập 0 BLOCKER; bước kế: CI xanh → Verify-Task20a trên PC → cập nhật worker trên laptop → thử tay Alt+V "kiểm tra mạng" → `/net`. Chi tiết `gate20a_handoff.md`.
- Tiếp theo: **TASK-21 = J1 LocalTtsService** (đặc tả trong `next_task21_spec.md`), rồi theo `ROADMAP_FUTURE.md` Phase 5 (J4a đã duyệt: WoL + TV Samsung qua LAN; J4b gác).
