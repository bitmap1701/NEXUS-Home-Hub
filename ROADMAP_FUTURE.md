# NEXUS Home Hub — Future Roadmap (v1.0.0 to v3.0+)

> **Nguồn**: Phần A do **Gemini Web** (Architect & Reality Checker) đề xuất, giữ nguyên văn. Phần B là góp ý và bổ sung của **Claude**, rút ra từ các Gate 5–11. Mục nào ở Phần B được Gemini và Nhật Hòa duyệt thì chuyển vào Phần A, ghi phiên bản và Gate tương ứng.
>
> **Cách dùng**: đây là tài liệu sống, build và cập nhật dần. Mỗi task mới vẫn có đặc tả riêng ở `03_Local_Data_Vault/handoff/current_task.md`; roadmap chỉ định hướng thứ tự và ràng buộc.
>
> **Cập nhật lần cuối**: 2026-11-08, `main` @ `6424d0f` (`v0.11.0`). Gate 11b (`v0.11.1`) chưa chạy.

---

# PHẦN A — Lộ trình đề xuất (Gemini Web)

Tài liệu này định hình chiến lược phát triển dài hạn cho hệ thống **NEXUS Home Hub**, mở rộng từ cột mốc hoàn thành cơ sở hạ tầng văn bản (`v0.11.1` / `v1.0.0`) sang kỷ nguyên trợ lý không gian cục bộ, đa phương thức và tự động hóa thông minh.

---

## 🗺️ Tổng Quan Các Giai Đoạn Phát Triển

| Giai đoạn | Phiên bản Mục tiêu | Trọng tâm Kỹ thuật & Nghiệp vụ | Trạng thái |
| :--- | :--- | :--- | :--- |
| **Phase 1: Foundation & Cockpit** | `v0.1` – `v0.8` | Nhân C#, SQLite WAL, Local MCP, HostService, phím tắt AULA HERO 68 HE, CockpitUI 144Hz | **ĐÃ HOÀN THÀNH** |
| **Phase 2: Resilient Text AI Core** | `v0.9` – `v0.11.1` | Khóa DPAPI, hội thoại đa lượt ADR-43, Backoff+Jitter, QuotaGuard, Cache ADR-44, Vault Grounding ADR-45 | **ĐANG CHỐT `v0.11.1`** |
| **Phase 3: Multimodal Vision & Micro-Widgets**| `v0.12` – `v0.15` | Đưa ảnh/chụp màn hình vào hỏi Gemini, thẻ tiện ích tương tác (Widgets), công cụ tính toán tức thì, ghim MFD | **CHUẨN BỊ TRIỂN KHAI** |
| **Phase 4: Voice, OS Context & Smart Home** | `v0.16` – `v0.18` | Giọng nói Push-to-Talk, đọc ngữ cảnh cửa sổ đang mở trên Windows, điều khiển thiết bị Smart Home | **ĐỊNH HƯỚNG** |
| **Phase 5: Autonomous Workflows & Release** | `v0.19` – `v1.0.0` | Tự động hóa theo lịch/sự kiện, tự sao lưu bảo trì SQLite WAL, đóng gói bộ cài chính thức `v1.0.0` | **ĐỊNH HƯỚNG** |
| **Phase 6: Beyond 1.0 (Advanced OS)** | `v1.x` – `v3.0+` | Multi-Agent Swarm, Local Vector RAG, Hybrid Edge-Cloud AI, visionOS / Spatial Holographic Cockpit | **TẦM NHÌN DÀI HẠN** |

---

## 🚀 Chi Tiết Các Tính Năng Sắp Tới (Phase 3 & Phase 4)

### 1. Multimodal Vision Engine (TASK-12 / ADR-46)
- **Lệnh `/img` & `/snap`**: Cho phép dán ảnh từ Clipboard (`Ctrl + V`) hoặc chụp nhanh cửa sổ màn hình MSI G272, tự động nén bằng Windows WIC (< 250 KB) để gửi lên Gemini Vision.
- **Vision Cache**: Băm SHA-256 của `Ảnh + Câu hỏi` lưu trong SQLite WAL giúp tra cứu lại kết quả phân tích ảnh trong < 2 ms mà không tốn token.

### 2. Interactive HUD Micro-Widgets (TASK-13 / ADR-47)
- **Actionable Response Cards**: Tự động bóc tách khối Code hoặc Checklist thành các thẻ tương tác.
- **Phím tắt nhanh**: `Alt + C` (Copy code ngay lập tức), `Alt + P` (Ghim thông tin quan trọng sang Right MFD).

### 3. Zero-Token Local Utilities (TASK-14 / ADR-48)
- **Tiện ích nội bộ không gọi AI**: `/calc` (máy tính biểu thức), `/timer` (đồng hồ đếm ngược trên MFD), `/note` (ghi chú nhanh vào Vault) chạy hoàn toàn cục bộ 0ms / 0 token.

### 4. Active Window Context (TASK-15 / ADR-49)
- **Lệnh `/ctx`**: Tự động nhận diện tiêu đề cửa sổ phần mềm đang mở và đoạn văn bản đang bôi đen trên Windows để đặt ngữ cảnh hỏi ngay lập tức.

---

## 🌐 Tầm Nhìn Xa (Beyond v1.0.0)
- **v1.x (Agentic Workspace)**: Hệ thống đa tác tử (Multi-Agent Swarm) phối hợp tự động lập trình và kiểm thử; Vector RAG cục bộ tra cứu tài liệu dưới 5 ms.
- **v2.0 (Edge Neural OS)**: Kết hợp mô hình ngôn ngữ nhỏ chạy offline (Edge SLM) tận dụng phần cứng NPU/GPU và nhận diện giọng nói đánh thức liên tục.
- **v3.0 (Spatial Holographic Cockpit)**: Đưa giao diện buồng lái NEXUS lên không gian thực tế hỗn hợp (Spatial Computing / WebXR / visionOS).

---

# PHẦN B — Góp ý & bổ sung của Claude (chờ Gemini / Nhật Hòa duyệt)

## B1. Ba việc nên chốt trước khi mở Phase 3

### B1.1 Số ADR đang bị trùng — đề xuất sổ đăng ký ADR
- **ADR-44, ADR-45** được chốt ở commit `7b97945` (đặc tả TASK-10) với nghĩa *Dual Execution Semantics* và *Prompt Construction Separation*. Sau đó TASK-11 dùng lại đúng hai số này cho *GeminiResponseCache* và *VaultGroundingEngine*.
- **ADR-46, ADR-47, ADR-48** cũng đã có nghĩa ở commit đó: kiểm tra `Set-NexusGeminiKey.ps1`, Live Gemini Resilience và Shift Handover Protocol. Phần A lại gán chúng cho TASK-12, 13, 14.
- **Đề xuất**:
  - giữ nguyên các số đã chốt;
  - đánh số lại bộ ADR của TASK-11 thành ADR-44b/45b, hoặc ghi chú "ADR-44 (TASK-11)";
  - **ADR mới của Phase 3 bắt đầu từ ADR-50**;
  - tạo `docs/ADR_REGISTRY.md` (số · tên · task · commit · trạng thái) và để mỗi đặc tả mới tra cứu ở đó trước khi gán số.

### B1.2 Đóng Gate 11b và giữ hạ tầng kiểm thử khỏe
- Phase 2 chỉ coi là xong khi Gate 11b (`v0.11.1`) ALL PASS **ở cấu hình chuẩn** (`maxDailyTokens = 250000`, `geminiModel = gemini-3.5-flash-lite`), không nâng tạm hạn mức như lần chạy 3 của Gate 11.
- **Chuỗi hồi quy dài thêm theo từng Gate**: Verify-Task11 → 03 hiện mất khoảng 8–10 phút. Đến Phase 4 sẽ thành 20 phút trở lên. Đề xuất chia hai tầng:
  - **Smoke**: selftest mock của Gate hiện tại + hai Gate trước, khoảng 2 phút, chạy mỗi lần sửa code;
  - **Full**: toàn bộ chuỗi, chỉ chạy khi chốt Gate.
- **Ngưỡng độ trễ luôn chấm theo nhiều mẫu** (P50/P95, như TASK-11b), không chấm một mẫu đơn lẻ. Lý do: Gate 11 lần chạy 2 trượt chỉ vì nhiễu khoảng 1 ms.

### B1.3 Build tự động trên GitHub (CI) — để ca nào cũng biết code có build được không
- Ca Claude trên claude.ai **không có .NET SDK** nên thường ghi "chưa build". Gate 9 và 11b đều phải chờ máy Nhật Hòa mới biết code có biên dịch không.
- **Đề xuất**: workflow GitHub Actions `windows-latest` chạy `dotnet build -c Release -warnaserror` và các selftest **mock** (không khóa, không mạng) trên mỗi nhánh `claude/*`. Chỉ phần đo RAM/CPU, gọi thật và màn hình MSI G272 mới cần máy thật. Tiết kiệm một vòng gửi qua lại cho mỗi Gate.

## B2. Ràng buộc bất biến áp dụng cho mọi Phase

| Ràng buộc | Giá trị hiện tại | Ghi chú khi mở rộng |
|-----------|------------------|---------------------|
| RAM private của HostService | < 60 MB (đo được 40,7–49,5 MB) | Ảnh, âm thanh, chỉ mục phải nằm ngoài heap lâu dài: buffer trả về pool, không giữ bản sao |
| CPU khi rảnh | < 1 % (đo được 0 %) | Không vòng lặp chờ bận; timer dùng 1 bộ đếm hệ thống; không polling cửa sổ |
| Độ trễ đường đồng bộ | `/ask` P95 < 10 ms; trúng cache P95 ≤ 2–4 ms | Mọi lệnh mới phải có bảng đo như `AskSyncBreakdown` |
| GPU | GTX 1660 dành cho game/IDE; Cockpit dùng iGPU UHD 730 (SoftwareOnly) | Không tính năng nào chiếm dGPU khi đang chơi |
| Gói NuGet | Không thêm mới | Ưu tiên API có sẵn: WPF/WIC, WinRT, SQLite FTS5 |
| Bảo mật | Khóa chỉ trong DPAPI; chặn chuỗi `AIza…` khi ghi DB và log | Ảnh, chữ bôi đen, giọng nói cũng phải qua lớp lọc tương tự |
| Cấp thao tác | Xanh / Vàng / Đỏ (`GEMINI.md`) | Thiết bị Smart Home, tự động hóa và xóa dữ liệu phải gắn cấp trước khi code |

## B3. Góp ý từng tính năng Phase 3–4

| Tính năng | Góp ý | Rủi ro cần đưa vào đặc tả |
|-----------|-------|---------------------------|
| **Vision `/img`, `/snap`** | WIC đã có sẵn trong WPF (`BitmapEncoder`), không cần NuGet. Nén trong `Task.Run`, giải phóng buffer ngay. Khóa cache nên gồm SHA-256 của **ảnh đã nén** + câu hỏi + model | **Chống gian lận**: không chụp trực tiếp cửa sổ game có ACE/BattlEye (Delta Force dùng ACE); chỉ chụp màn hình MSI G272 hoặc vùng người dùng chọn, bằng API chụp màn hình chuẩn, không can thiệp tiến trình game. **Riêng tư**: khóa đang ở **gói Free** của Google, nên nội dung gửi đi có thể được Google dùng để cải thiện sản phẩm; `/snap` cần xem trước và xác nhận, có cảnh báo khi trong ảnh có chữ giống mật khẩu hoặc khóa |
| **Micro-Widgets** | Bóc tách code/checklist bằng parser Markdown tối giản trong Core, có selftest | **Phím tắt `Alt+C`, `Alt+P`** dễ trùng phím trong game và IDE: chỉ bật khi Cockpit đang ở Command mode, không đăng ký phím toàn cục |
| **`/calc`, `/timer`, `/note`** | `/calc`: tự viết parser biểu thức an toàn, không dùng eval hay `DataTable.Compute`. `/timer`: một `System.Threading.Timer`, hiện trên MFD. `/note`: ghi file `.md` trong Vault bằng ghi nguyên tử, nên **VaultGroundingEngine tự tìm thấy ghi chú** (cộng hưởng với ADR-45) | `/note` nhận chuỗi giống khóa API thì chặn như `/ask`. Thư mục ghi chú cần quy ước, ví dụ `03_Local_Data_Vault/notes/` |
| **`/ctx`** | Lấy tiêu đề cửa sổ bằng `GetForegroundWindow` + `GetWindowText` (rẻ). Lấy chữ bôi đen bằng UI Automation chỉ khi người dùng gõ `/ctx` | **Bỏ qua ô mật khẩu** (UIA `IsPassword`). **Không đọc cửa sổ game có anti-cheat**: dùng danh sách `protectedProcessPrefixes` sẵn có làm danh sách chặn. Không giữ ngữ cảnh sau khi trả lời |
| **Giọng nói Push-to-Talk** | Một phím trên AULA HERO 68 HE làm PTT; chỉ thu âm khi giữ phím (không nghe liên tục ở Phase 4) | Đo CPU khi đang chơi game; quy định rõ âm thanh gửi lên đâu (Gemini Live hay STT cục bộ) |
| **Smart Home** | Đi qua cầu nối chuẩn (Home Assistant REST/MQTT), không gọi API từng hãng | Thao tác vật lý (khóa cửa, bếp, điều hòa) xếp **Cấp Đỏ**: luôn hỏi xác nhận, không cho chạy tự động |
| **Tự động hóa & sao lưu (Phase 5)** | Sao lưu bằng `VACUUM INTO` (đã dùng ở migration TASK-05). Thêm việc dọn dữ liệu cũ: cache hết hạn, `system_audit_logs` quá N ngày, các bản `.invalid-*` | Kích thước DB tăng theo mỗi lần chạy kiểm thử (1 770 dòng điều phối sau Gate 11); cần giới hạn hoặc tách DB kiểm thử |
| **Bộ cài `v1.0.0`** | Quyết định sớm: self-contained hay cần .NET runtime; tự khởi động cùng Windows; gỡ cài đặt sạch | Khóa DPAPI gắn theo **tài khoản Windows**: đổi máy hoặc tài khoản phải nạp lại khóa; cần di trú `nexus_config.json` giữa các phiên bản |

## B4. Góp ý Tầm nhìn xa (v1.x – v3.0)

- **Vector RAG cục bộ (v1.x)** mâu thuẫn với hai ràng buộc hiện tại: không giữ chỉ mục lớn trên heap (TASK-11) và không thêm NuGet. Đề xuất đi qua bước trung gian **SQLite FTS5** (xếp hạng BM25). FTS5 có sẵn trong bản SQLite mà `Microsoft.Data.Sqlite` đang dùng, chỉ mục nằm trên đĩa, không cần gói mới. Chỉ chuyển sang vector khi FTS5 không đủ, và cần ADR riêng để nới ràng buộc.
- **Multi-Agent Swarm (v1.x)**: đã có nền là 3 vai Gemini / Claude / Antigravity, cầu chì 3 lần thử, `/retry` và bàn giao ca. Việc tiếp theo là chuẩn hóa định dạng bàn giao (ADR-48) thành dữ liệu máy đọc được (JSON) bên cạnh file Markdown.
- **Edge SLM (v2.0)**: i5-12400 **không có NPU**. GTX 1660 6 GB dành cho game, còn UHD 730 yếu. Mô hình nhỏ chạy offline chỉ khả thi trên CPU khi máy rảnh, hoặc phải nâng cấp phần cứng. Nên ghi rõ điều kiện phần cứng trước khi cam kết v2.0.
- **Giọng nói đánh thức liên tục (v2.0)** đi ngược mục tiêu CPU 0 % khi rảnh: cần đo trên máy thật trước khi chốt.
- **Spatial Cockpit (v3.0)**: giữ là tầm nhìn. Bước nhỏ có ích trước đó là đưa trạng thái Cockpit ra một trang web cục bộ chỉ đọc (dùng lại `latest_dispatch.json`), làm nền cho WebXR sau này.

## B5. Thứ tự Claude đề xuất cho Phase 3

1. **Gate 11b** (`v0.11.1`) + B1.1 (sổ ADR) + B1.3 (CI build): chi phí nhỏ, giảm rủi ro cho mọi task sau.
2. **TASK-14 `/calc`, `/timer`, `/note`** lên trước: 0 token, dễ kiểm thử, `/note` làm giàu Vault cho grounding.
3. **TASK-13 Micro-Widgets.**
4. **TASK-12 Vision**: tốn token nhất và có rủi ro chống gian lận / riêng tư, nên làm sau khi hạ tầng đo token và cache đã ổn định.
5. **TASK-15 `/ctx`**: cùng nhóm rủi ro với Vision (đọc nội dung ứng dụng khác), làm cuối Phase 3 hoặc gộp sang Phase 4.

---

## Nhật ký cập nhật

| Ngày | Ai | Nội dung |
|------|----|----------|
| 2026-11-08 | Gemini Web | Phần A: lộ trình Phase 1–6, chi tiết Phase 3–4, tầm nhìn v1.x–v3.0 |
| 2026-11-08 | Claude | Phần B: trùng số ADR, hạ tầng kiểm thử và CI, ràng buộc bất biến, rủi ro từng tính năng, thứ tự đề xuất cho Phase 3 |
