# 📦 HỒ SƠ BÀN GIAO — Nghiệm thu Gate 7 (TASK-07) & Nhận TASK-08

- **Ca thực thi**: Claude, 2026-11-07
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity (Local IDE Executor)
- **Nhánh**: `claude/task-07-gemini-dpapi`, tách từ `main` (`35926f0`, sau tag `v0.6.0`)
- **Đặc tả**: `03_Local_Data_Vault/handoff/current_task.md` (TASK-07, lấy từ máy Nhật Hòa)
- **Chi tiết kỹ thuật**: `qa_report.md` §9

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước ca | Sau ca |
|---|----------|----------|--------|
| 1 | Khóa Gemini | Chưa có chỗ lưu | `DpapiSecretVault`: DPAPI CurrentUser + entropy, `config/gemini_key.dpapi` (git-ignored), DPAPI → ENV → Missing, che khóa |
| 2 | Lệnh Cockpit | Không có AI | `/apikey set\|status\|clear`, `/ask <câu hỏi>` (focus về game ngay, câu trả lời hiện sau trong khung AI RESPONSE) |
| 3 | Gọi Gemini | — | `GeminiAgentClient`: `generateContent`, khóa trong header, timeout, chẩn đoán lỗi 400/401/403/404/429/5xx/timeout/mạng, mock tất định |
| 4 | Luồng dùng chung | — | `AgentConsultService`: Cockpit `/ask` và MCP `consult_gemini_agent` cùng một đường (đồng bộ + nền) |
| 5 | Dữ liệu | — | 3 cột `ai_response`, `ai_model`, `ai_latency_ms` (ALTER idempotent); `latest_dispatch.json` thêm `aiResponse`, `aiModel`, `aiLatencyMs` |
| 6 | Cấu hình | — | `geminiModel`, `geminiTimeoutSeconds`, `geminiMaxOutputTokens`, `geminiTemperature` (không bắt buộc, file cũ tự nhận mặc định) |
| 7 | Chống lộ khóa | — | Chặn chuỗi dạng `AIza…` ở mọi đường điều phối; audit log luôn che; echo HUD che `/apikey set`; thông báo lỗi lọc khóa |
| 8 | `.gitignore` | Bỏ qua 2 file config | Bỏ qua cả thư mục `03_Local_Data_Vault/config/` và `*.dpapi` (ADR-24) |
| 9 | Kiểm thử | — | Selftest `schema07`, `vault`, `ask`, `ask-missingkey`, `ask-live`; `Verify-Task07.ps1` (E1–E5) + quét rò rỉ khóa |

---

## 2. Trạng thái Gate 7 — ✅ E1–E5 ALL PASS (gọi Gemini thật: SKIPPED, chưa có khóa)

Máy Nhật Hòa, 22:29, commit `f9a173e`, có build: **ALL CHECKS PASS (E1–E5)**. Phần gọi Gemini thật ở E5 bị bỏ qua vì chưa cài khóa; mọi thứ khác (DPAPI, chống lộ khóa, mock, thiếu khóa, MCP, hồi quy 03–06) đã chạy thật. **Chưa fast-forward `main`**: chờ Gemini nghiệm thu và, nếu muốn, một lần chạy có khóa thật (`/apikey set <KEY>` rồi `Verify-Task07.ps1 -SkipBuild`). Ghi nhận ở qa_report §9.3: private WS đỉnh 58,6 MB (sau đó 53 MB), lần `/ask` đầu trong E3 mất 2,9 s.

### Lệnh chạy (máy Nhật Hòa)
```powershell
cd C:\Agent\NEXUS_Home_Hub
# current_task.md trên máy là bản Gemini ghi nhưng chưa commit (nhánh này đã có bản giống hệt); cất tạm để checkout:
git stash push -m "gemini-local-task07" -- 03_Local_Data_Vault/handoff/current_task.md
git fetch origin
git checkout claude/task-07-gemini-dpapi
git pull
powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task07.ps1
```
Lần build đầu sẽ tải gói `System.Security.Cryptography.ProtectedData` từ NuGet. E5 gọi Gemini thật **chỉ khi đã có khóa** (Cockpit: `Ctrl+Shift+Space` → `/apikey set <KEY>` → Enter; hoặc biến môi trường `GEMINI_API_KEY`), nếu chưa có thì phần đó là SKIPPED. Hồi quy (Verify-Task06 → 05 → 04 → 03) mất khoảng 3–4 phút.

| # | Bằng chứng | Tiêu chí PASS | Kết quả |
|---|-----------|---------------|---------|
| E1 | Build + `--selftest schema07` | `-warnaserror` 0/0; đủ `ai_response`, `ai_model`, `ai_latency_ms`; số dòng trước = sau | ✅ 0/0; thêm 3 cột, 465 → 465 dòng |
| E2 | `--selftest vault` + quét rò rỉ | `gemini_key.dpapi` tồn tại, byte trên đĩa không chứa `AIzaSyTestKey` (UTF-8 lẫn UTF-16); giải mã khớp; mask `AIza...456 (39 chars, Source: DPAPI)`; `/apikey set/status/clear` đúng; dán khóa vào câu lệnh bị chặn; không có khóa thử trong SQLite (db/wal/shm), `latest_dispatch.json`, log, `qa_report.md`, báo cáo; file khóa bị git bỏ qua; khóa thật (nếu có) được khôi phục nguyên vẹn | ✅ blob 262 byte, không bản rõ; quét 8 file sạch; git-ignored |
| E3 | Mock: `--selftest ask` + MCP `consult_gemini_agent` | `/ask Tối ưu C# SocketAsyncEventArgs…` → Squad1, `ai_*` trong SQLite và `latest_dispatch.json`, phần đồng bộ < 5 ms; MCP trả Squad1 + `aiModel = gemini-mock-verifier`; Cockpit đang chạy nhận câu trả lời của MCP | ✅ #466 Squad1, đồng bộ ấm 1,5–2,4 ms; MCP #472 Squad1 mock; Cockpit nhận |
| E4 | `--selftest ask-missingkey` | Không exception; dòng `DISPATCHED`; `ai_response = [LOCAL DISPATCH ONLY] Chưa cấu hình Gemini API Key…` | ✅ #473 DISPATCHED, không exception |
| E5 | Gọi thật (nếu có khóa) + hồi quy | Có khóa: `/ask` thật trả lời, ghi `ai_latency_ms`; Verify-Task06/05/04/03 ALL PASS; private WS < 60 MB, CPU < 1 % | ✅ gọi thật SKIPPED; hồi quy ALL PASS (P95 2,59 ms, CPU 0,05 %, WS 58,6 → 53 MB) |

Kết quả gốc: `handoff/task07_verification.json`, `handoff/task07_selftest_*.json`.

**Kiểm tay nên làm sau khi có khóa**: trong game `Ctrl+Shift+Space` → `/ask Gợi ý cài đặt đồ họa Delta Force cho GTX 1660` → Enter: bàn phím về game ngay, thẻ trên MSI G272 hiện `[THINKING... Calling Gemini]`, vài giây sau khung AI RESPONSE hiện câu trả lời. Thử thêm một khóa sai để thấy thông báo `[GEMINI API ERROR HTTP 400 …]` mà ứng dụng không văng.

---

## 3. Điểm mở cần Architect quyết định

1. **Mock thắng khóa** (`NEXUS_GEMINI_MOCK_RESPONSE` đặt là không gọi mạng, kể cả khi có khóa). Script không bao giờ đặt biến này cho HostService chạy lâu dài. Đồng ý?
2. **Ngân sách phần đồng bộ của `/ask` khi LiveCombat**: đặc tả ghi < 5 ms; Claude dùng 25 ms (`dispatchSlaCombatMs`) khi đang chơi game theo ADR-17. Xác nhận?
3. **E5 khi chưa có khóa = SKIPPED, không trượt**; có khóa mà lỗi (sai khóa/quota/mạng) = trượt. Đúng ý chưa?
4. **Chặn chuỗi dạng API key ở mọi đường điều phối** (thêm ngoài đặc tả). Nếu có trường hợp hợp lệ cần gửi chuỗi `AIza…` (rất khó xảy ra) thì sẽ bị từ chối.
5. **Giới hạn trả lời cho HUD**: system instruction thêm mục 7 (trả lời theo ngôn ngữ người hỏi, ~200 từ, không gợi ý tắt anti-cheat) cùng `geminiMaxOutputTokens = 1024`. Có muốn chỉnh giọng/độ dài không?
6. **Chưa có lịch sử hội thoại**: mỗi `/ask` là một câu hỏi độc lập (không gửi các câu trước). Có cần hội thoại nhiều lượt cho TASK sau?
7. **Đặc tả TASK-07 vẫn chưa có trên GitHub** (lần thứ 3 liên tiếp: lời nhắn nói đã commit nhưng `main` vẫn là TASK-06). Đề nghị commit từ máy (`git add 03_Local_Data_Vault/handoff/current_task.md` + `git commit` + `git push`) hoặc để Claude commit như các lần trước.
8. **Tag `v0.6.0`** đang trỏ `a812742` (chưa gồm commit tài liệu Gate 6 `35926f0`). Giữ nguyên hay dời tag?

---

## 4. Đề xuất TASK-08

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Vòng QA tự động có Gemini** *(khuyến nghị)* | Sau `/ask`, QA Agent của squad (persona `.md`) chấm câu trả lời của Primary bằng Gemini → gợi ý `PASSED`/`NEEDS_WORK` kèm lý do, người dùng xác nhận bằng `/pass` hoặc `/fail` (cầu chì 3 lần thử đã có); hội thoại nhiều lượt theo `dispatchId` (`/ask #id <tiếp>`); giới hạn số lần gọi/phút trong `nexus_config.json`. | Nối hai nửa đã có: dispatch + Gemini (TASK-07) và vòng Dev-QA + cầu chì (TASK-05/06). Đúng mục tiêu ban đầu "Reality Checker" của dự án. |
| B. Giọng nói (PCM 16 kHz + VAD) → `/ask` | Mic → Gemini Live → lệnh Cockpit. | Cần đo CPU/âm thanh khi chơi; nên sau A. |
| C. Dọn giao diện & hiệu năng nền | Đỉnh CPU ~6 s, RAM private trên thanh trạng thái, chữ bị cắt, MFD lỗi thời. | Nhỏ, có thể gộp vào A. |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Gate 7 (TASK-07) đã chạy trên máy Nhật Hòa: **Verify-Task07 ALL CHECKS PASS (E1–E5)**, có build `-warnaserror` 0/0 (nhánh `claude/task-07-gemini-dpapi`, commit `f9a173e`; chi tiết `gate7_handoff.md`, `qa_report.md` §9.3). Migration thêm 3 cột `ai_*` giữ 465/465 dòng; DPAPI: blob 262 byte không chứa bản rõ (UTF-8/UTF-16), giải mã khớp, mask đúng, `/apikey set/status/clear` đúng, dán khóa vào câu lệnh bị chặn; quét rò rỉ khóa thử sạch ở SQLite (db/wal/shm), `latest_dispatch.json`, log, `qa_report.md`; `/ask` (mock) → Squad1, phần đồng bộ 1,5–2,4 ms, câu trả lời lưu SQLite + `latest_dispatch.json`; MCP `consult_gemini_agent` → Cockpit đang chạy nhận câu trả lời; thiếu khóa → `DISPATCHED` + `[LOCAL DISPATCH ONLY]`, không văng; hồi quy Task06/05/04/03 ALL PASS (P95 2,59 ms, CPU 0,05 %). Minh bạch: (1) **gọi Gemini thật chưa chạy** (chưa cài khóa → SKIPPED); (2) private WS đỉnh **58,6 MB** sát ngân sách 60 (cùng tiến trình đo lại 53 MB sau ~1 phút); (3) lần `/ask` đầu tiên trong selftest mất 2,9 s ở phần đồng bộ, chưa rõ nguyên nhân. Claude chưa fast-forward `main`, chờ bạn nghiệm thu. Điểm cần quyết định ở §3; Claude khuyến nghị TASK-08 Hướng A (vòng QA tự động có Gemini) kèm dọn bộ nhớ (tạo `HttpClient` khi cần, bỏ `RegexOptions.Compiled`) và đo từng bước cho `/ask`. Nhớ commit đặc tả TASK-08 lên GitHub.
