# 📦 HỒ SƠ BÀN GIAO — Gate 17 (TASK-17, ADR-67) — TẠM DỪNG: làm giao diện Cockpit v2 theo bản thiết kế đã duyệt trước

- **Ca thực thi**: Claude, 2026-11-11 (rạng sáng)
- **Gửi tới**: Gemini Web (Architect & Reality Checker), Antigravity, Claude Code
- **Nhánh**: `claude/task-17-net-radar-voice-ptt` (đặc tả `5e07d39`, tách từ `main` @`60322e2` = `v0.16.0`)
- **Trạng thái build**: **chưa build** — ca này chỉ sửa tài liệu, TASK-17 chưa có dòng code nào.

---

## 1. Ca này đã làm gì

| # | Hạng mục | Trước | Sau |
|---|----------|-------|-----|
| 1 | ADR-62 | "Đang triển khai (`v0.16.0`)" | **Chốt** — Gate 15 ALL PASS lần 3/3, `v0.16.0` |
| 2 | ADR-67 | Chỉ có trong `current_task.md` | Có dòng trong `docs/ADR_REGISTRY.md` (Dự kiến, tạm dừng); số trống kế tiếp **ADR-68** |
| 3 | ROADMAP | TASK-15 chưa ghi kết quả | Ghi "Gate 15 ALL PASS lần 3/3" |
| 4 | TASK-17 | Vừa giao | Đã khảo sát code (lệnh Cockpit, MFD, cấu hình, MCP, selftest). **Tạm dừng** theo quyết định của Nhật Hòa |
| 5 | Phát hiện | — | Canvas "NEXUS Cockpit UI v2" (Nhật Hòa đã duyệt 2026-11-08) mới được làm phần hành vi, **chưa làm phần nhìn** (xem `qa_report.md` §23) |

Lưu ý: `main` và tag `v0.16.0` trỏ `60322e2` (= `1e1d6cf` + một commit tài liệu; code giống nhau), không phải `1e1d6cf` như đặc tả TASK-17 ghi.

---

## 2. Trạng thái Gate 17 — chưa bắt đầu (tạm dừng)

Không có bằng chứng E1–E5 nào. Khi TASK-17 làm lại, đặc tả `current_task.md` trên nhánh vẫn dùng được; trang `NET` và nút mic sẽ gắn vào giao diện mới.

---

## 3. Điểm mở cần Architect quyết định

1. **Viết đặc tả task giao diện** theo canvas đã duyệt: tên task, số ADR (đề xuất ADR-68), phiên bản (đề xuất `v0.16.5`, trước `v0.17.0` của TASK-17).
2. **Chữ**: Inter + JetBrains Mono như canvas (phải nhúng file font, tốn thêm RAM), hay Segoe UI Variable + Cascadia Mono có sẵn trên Windows 11 (0 byte thêm)?
3. **Các ô tick "Quick Switches"** (Game Boost, iGPU lock, mic, 3-Tier Gate, tự thu gọn, xuyên thấu) không có trong canvas. Gom vào bảng F1 / một menu cài đặt, hay giữ ở cột phải?
4. **Nút Hủy** trên thanh tiến trình cần hủy được lời gọi Gemini đang chạy (hiện chưa có đường hủy). Có nằm trong task giao diện không?
5. **TASK-17**: Ctrl+Shift+Space đang tự tick ô "3. Mic 16kHz VAD Stream" dù chưa thu âm gì. Khi làm TASK-17, phím này có được tự mở mic không? Claude khuyến nghị **không**, vì quyền riêng tư và ngân sách WAKE.

---

## 4. Đề xuất task tiếp theo

| Hướng | Nội dung | Lý do |
|-------|---------|-------|
| **A. Task giao diện Cockpit v2 theo canvas, trước TASK-17** *(Nhật Hòa đã chọn)* | Bố cục 2 cột, chip trạng thái, thẻ hội thoại, thanh tiến trình, bảng lệnh `/` + `@`, cột phải dạng thẻ, thẻ "Việc tiếp theo" | Nhật Hòa đã duyệt từ 08/11; làm trước để trang NET / nút mic của TASK-17 không phải làm hai lần |
| B. TASK-17 ngay sau đó | Theo đặc tả `5e07d39` | Đã khảo sát xong, sẵn sàng viết |

---

## 5. Lời nhắn dán cho Gemini Web

> Chào Gemini, Claude đây. Đã nhận Gate 15 → `v0.16.0`. Lưu ý: `main` và tag trỏ `60322e2` (= `1e1d6cf` + một commit tài liệu, code giống nhau). Đã chuyển ADR-62 sang "Chốt" và ghi ADR-67 vào sổ; số trống kế tiếp là **ADR-68**.
>
> **TASK-17 tạm dừng theo quyết định của Nhật Hòa** (mới khảo sát code, chưa viết dòng nào). Lý do: Nhật Hòa báo "UI vẫn cũ".
>
> Ngày 08/11, Claude làm một bản thiết kế "NEXUS Cockpit UI v2" (6 bảng) và Nhật Hòa đã duyệt. TASK-16/16b chỉ lấy **phần hành vi** của bản đó (3 mức cao 40/330/640 px, xuyên thấu, lịch sử hỏi–đáp, phím lười, bảng liên minh Alt+A, huy hiệu xong ca). **Phần nhìn chưa bao giờ được làm**: Cockpit vẫn là giao diện terminal 3 cột cũ. Claude nhận lỗi vì đã không đối chiếu code với bản thiết kế.
>
> Nhật Hòa chọn làm giao diện trước TASK-17. Mời bạn viết đặc tả task giao diện (đề xuất ADR-68, `v0.16.5`) theo bản thiết kế đã duyệt:
>
> 1. **Bố cục 2 cột**:
>    - cột hội thoại;
>    - cột phải rộng 300 px.
>
>    Bỏ cột LEFT MFD; lịch sử chuyển thành chuỗi thẻ trong cột hội thoại.
> 2. **Thanh trên 44 px**:
>    - chấm trạng thái + "NEXUS COCKPIT" + nhãn `COMMAND` / `PASSIVE`;
>    - các chip RAM · CPU · WAL / chế độ;
>    - nút chọn độ cao Gọn / Chuẩn / Cao (40 / 330 / 640 px);
>    - nút F1 Phím tắt.
>
>    Khi thu gọn 40 px chỉ còn một dòng: NEXUS · RAM · trạng thái Gemini.
> 3. **Mỗi câu hỏi là một thẻ**:
>    - giờ, câu hỏi, squad · agent, model, nhãn QA;
>    - đoạn trả lời (markdown);
>    - nút Copy (Alt+C) · Ghim (Alt+P) · Hỏi tiếp (`/f`) · Chạy lại (`/retry`).
>
>    Lệnh cục bộ (`/calc`…) hiện thành một dòng gọn, có nhãn "cục bộ · 0 token".
> 4. **Thanh tiến trình** trên thẻ đang chạy: Điều phối → (Nén ảnh) → Gemini đang trả lời → QA, kèm đồng hồ giây và nút Hủy (Esc).
> 5. **Ô lệnh**:
>    - có nhãn "Lệnh hoặc câu hỏi", tiền tố `NEXUS›`;
>    - gợi ý "Gõ câu hỏi · / xem lệnh · @ chọn agent", "Enter gửi · Esc về game".
> 6. **Bảng lệnh**:
>    - gõ `/` hiện danh sách lệnh (mục Vừa dùng + Tất cả, mỗi dòng có mô tả và ví dụ), lọc theo chữ, ↑↓ chọn, Tab điền;
>    - gõ `@` hiện danh sách agent từ registry 283, lọc theo chữ, chọn ra một nhãn agent trong ô lệnh.
> 7. **Cột phải**:
>    - "THẺ ĐÃ GHIM n/5" hiện dạng thẻ (loại · ngôn ngữ, 2 dòng xem trước);
>    - dòng hẹn giờ;
>    - ở đáy là ô **LIÊN MINH** (task, trạng thái, việc kế tiếp) có nút "Mở bảng điều phối · Alt+A".
> 8. **Chế độ lười**:
>    - khi rảnh, hiện thẻ lớn **"VIỆC TIẾP THEO"** với nút Alt+Enter và vòng liên minh ✓ Claude viết code → ✓ Claude Code Verify → ● Gemini duyệt → ○ Merge + tag;
>    - khi clipboard có ảnh, hiện thẻ gợi ý hỏi (Enter / 2 / 3);
>    - sau mỗi câu trả lời, các phím số 1–4 hiện thành nút.
> 9. **Thanh kéo** ở đáy: "Kéo xuống mở rộng · kéo lên thu gọn · nhấp đúp: Thu gọn ↔ Nửa" (hành vi đã có; thêm phần nhìn).
> 10. **Màu**: nền `#0B1220`, viền `#1E2A40`, nhấn `#38BDF8` / `#7DD3FC`, chữ `#E2E8F0` / `#CBD5E1` / `#94A3B8`, xanh lá `#34D399`, vàng `#FDE68A`.
>
> **Ràng buộc đề xuất:**
> - Private WS rảnh ≤ 45 MB; CPU rảnh 0 %; không animation, không timer mới.
> - WAKE 40 → 330 px giữ P50 ≤ 10 / P95 ≤ 16 ms. Phần thân phải được **cắt**, không Collapsed, như Gate 16b.
> - Chuỗi thẻ giới hạn khoảng 20 thẻ và dùng lại phần tử để RAM không tăng theo lịch sử.
> - Không thêm phím tắt toàn cục.
> - Giữ các lệnh pipe / trường `UISTATE` mà Verify-Task04…16b đang dùng, hoặc ghi rõ cái nào đổi.
> - 0 token, zeroLiveCalls, 0-byte BOM.
>
> **Cần bạn quyết:**
> - (1) Chữ: Inter + JetBrains Mono nhúng kèm (tốn RAM), hay Segoe UI Variable + Cascadia Mono có sẵn trên Windows 11 (0 byte thêm)? Claude khuyến nghị cái sau.
> - (2) Các ô tick "Quick Switches" (Game Boost, iGPU lock, mic, 3-Tier Gate, tự thu gọn, xuyên thấu) không có trong bản thiết kế: gom vào bảng F1 / menu cài đặt, hay giữ ở cột phải?
> - (3) Nút Hủy cần hủy được lời gọi Gemini đang chạy (hiện chưa có đường hủy): làm trong task này hay để sau?
> - (4) Cho TASK-17 sau này: Ctrl+Shift+Space đang tự tick ô "3. Mic 16kHz VAD Stream". Có cho phím này tự mở mic không? Claude khuyến nghị không, vì quyền riêng tư và ngân sách WAKE.
>
> Nhật Hòa sẽ gửi kèm ảnh 6 bảng thiết kế. Khi có đặc tả, Claude làm ngay rồi quay lại TASK-17.
