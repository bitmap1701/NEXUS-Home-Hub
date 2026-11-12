# 📦 HỒ SƠ BÀN GIAO — Gate 18b (TASK-18b, ADR-73 → `v0.18.2`): worker STT TCP 5055 (whisper.cpp) cho Nitro 5

- **Ca thực thi**: Claude, 2026-11-12
- **Gửi tới**: Nhật Hòa (Verify trên PC `bitmappp`, cài worker trên Nitro 5), Gemini Web (Architect & Reality Checker), Antigravity
- **Nhánh**: `claude/task-18b-nitro-whisper-worker` (từ `main` = `012036b`, tag `v0.18.1`)
- **Chi tiết kỹ thuật**: `qa_report.md` §30
- **Trạng thái build**: Claude **chưa build**; CI build + selftest `stt-worker-e2e`. Chưa có số đo trên PC.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | Phía máy chủ LAN | Chỉ có hợp đồng phía PC | `McpTools --stt-worker serve`: TCP 5055, WAV → whisper-cli → UTF-8, lỗi `ERR:` |
| 2 | Bảo vệ | — | Chỉ loopback + 192.168.1.x, WAV ≤ 2 MB, đọc ≤ 3 s, một nhận dạng một lúc, WAV tạm luôn xóa |
| 3 | Nitro 5 | — | `Start-NitroSttWorker.ps1` (bind địa chỉ LAN, kiểm tường lửa chỉ đọc) |
| 4 | Kiểm thử | — | Selftest `stt-worker-e2e` (CI), `Verify-Task18b.ps1` |
| 5 | Sổ cái | ADR-72 đang triển khai | ADR-72 **Chốt `v0.18.1`**, ADR-73 đang triển khai, trống kế tiếp ADR-74 |

---

## 2. Trạng thái Gate 18b — ✅ Architect đóng dấu `v0.18.2` theo bằng chứng lần 1/3 (`0c553cc`; đặc tả TASK-19, 19:12); quyết 18b.1 = `ggml-small-q5_1`, 18b.3 = chạy lại 1 lần + `[SLA-WARN]`

**Lần 1/3** (`0c553cc`, 19:10): E1–E4 ✅ (worker 91,2 ms, WAKE P95 10,76 ms, RAM 31,7 MB). E5 ❌ chuỗi: Verify-Task15 E3 `cacheHits` P95 3,198 ms (một mẫu 4,75 ms trong 25). Chi tiết `qa_report.md` §30.3.

```powershell
cd C:\Agent\NEXUS_Home_Hub
git fetch origin
git checkout claude/task-18b-nitro-whisper-worker
git pull
git status
powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task18b.ps1
```
Khoảng 75 phút (chuỗi Verify-Task18a → … → 03). Không cần bật Nitro 5. Kết quả: `03_Local_Data_Vault/handoff/task18b_verification.json`.

| # | Bằng chứng | Trạng thái |
|---|------------|-----|
| E1 | build 0/0, BOM, ADR-72 chốt `v0.18.1`, ADR-73, script khởi động, selftest | ⏳ |
| E2 | worker loopback → tiếng Việt ≤ 500 ms, 0 file tạm | ⏳ |
| E3 | rỗng / không phải WAV / > 2 MB / cắt ngang bị từ chối ≤ 100 ms, vẫn phục vụ; whisper-cli thiếu → SAPI ≤ 250 ms | ⏳ |
| E4 | WAKE P50 ≤ 10 / P95 ≤ 16 ms | ⏳ |
| E5 | RAM ≤ 45 (mục tiêu ≤ 36), CPU 0 %, chuỗi 18a → 03 ALL PASS, 0 lượt gọi thật | ⏳ |

### Dùng thật trên Nitro 5 (sau Gate, Nhật Hòa làm)
1. Tải whisper.cpp (bản Windows có `whisper-cli.exe`) và một mô hình đa ngôn ngữ (`ggml-small.bin` cho tiếng Việt tốt hơn, `ggml-base.bin` nhẹ hơn).
2. Trên Nitro 5: `git pull`, `dotnet build .\01_Foundation_Engine\NEXUSHome.McpTools -c Release`, rồi
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Start-NitroSttWorker.ps1 -WhisperExe <đường dẫn>\whisper-cli.exe -Model <đường dẫn>\ggml-small.bin`
3. Trên PC: thêm vào `nexus_config.json` `"stt": { "provider": "auto", "lanEndpoint": "192.168.1.22:5055", "language": "vi-VN" }`, gõ `/reload`, rồi `/ptt` và nói tiếng Việt.

---

## 3. Điểm mở cần Architect quyết định

- **(18b.1) Mô hình Whisper**: `small` (~470 MB, chính xác hơn với tiếng Việt, chậm hơn) hay `base` (~140 MB, nhanh hơn). Ảnh hưởng thời gian trả lời trên Nitro 5 (chưa đo).

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Verify-Task18b lần 1/3** *(khuyến nghị)* | Lệnh ở mục 2 | CI xanh trước |
| B. Đo thật trên Nitro 5 | Cài whisper.cpp + mô hình, đo độ trễ `/ptt` tiếng Việt | 18b.1 |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. TASK-18b đã viết xong trên `claude/task-18b-nitro-whisper-worker` (từ `main` = `012036b`, tag `v0.18.1`; `qa_report.md` §30):
> - `McpTools --stt-worker serve [--bind] [--cli] [--args] [--lang]`: phía máy chủ của hợp đồng LAN 18a. Lỗi trả `ERR: …` → PC lùi về SAPI. Chỉ loopback + 192.168.1.x; WAV ≤ 2 MB; đọc ≤ 3 s; `SemaphoreSlim(1,1)`; WAV tạm luôn xóa.
> - `Start-NitroSttWorker.ps1`: bind địa chỉ 192.168.1.x của máy :5055, tham số whisper-cli + mô hình, chỉ đọc luật tường lửa.
> - Selftest `stt-worker-e2e` chạy trên PC không cần Nitro 5 (worker thật trên loopback + CLI giả lập); `Verify-Task18b.ps1` E1–E5.
>
> Điểm mở **(18b.1)**: mô hình Whisper cho Nitro 5: `small` hay `base`?
