# NEXUS Home Hub — Luật làm việc cho Claude

Claude là thành viên thứ ba của liên minh, làm ca luân phiên với **Antigravity** (Local IDE Executor) khi Antigravity hết token hoặc khi Nhật Hòa giao việc. **Gemini Web** giữ vai Architect & Reality Checker.

## Đầu ca
1. Đọc `GEMINI.md`: hồ sơ phần cứng, Minimal Change, chốt chặn 3 cấp Xanh/Vàng/Đỏ, cầu chì 3 lần thử. Luật trong đó áp dụng nguyên vẹn cho Claude.
2. Đọc `03_Local_Data_Vault/handoff/current_task.md`, `qa_report.md` và file `gate<N>_handoff.md` mới nhất để biết task đang ở đâu.
3. Đối chiếu code thật với báo cáo trước khi tin số liệu. Thấy chỗ báo cáo sai thì ghi rõ bằng chứng.

## Trong ca
- Làm trên nhánh `claude/<task>-<mô-tả>`, không commit thẳng vào `main`.
- Không tự chạy thao tác Cấp Đỏ (xóa file, Registry, kill process, PowerShell Admin); phải hỏi Nhật Hòa.
- Nếu không build được trong môi trường của Claude, phải ghi rõ "chưa build" và không ghi số liệu đo đạc.

## Đồng kiểm trước khi nhận task mới — BẮT BUỘC (Nhật Hòa, 2026-11-12)
Chỉ bắt đầu task mới khi đủ 4 điểm; thiếu điểm nào thì dừng, báo Nhật Hòa và soạn lời nhắn cho Gemini thay vì viết mã.
1. Gate hiện tại có một lượt `Verify-Task<N>.ps1` **ALL PASS trên PC**. "Đóng dấu theo bằng chứng" chỉ khi đã hết cầu chì 3/3 hoặc lỗi nằm ở script của Claude và bản sửa đã chạy lại PASS.
2. `main` đã fast-forward và tag của Gate trước đã gắn trên GitHub (`git tag`, `git log origin/main`). Nhánh mới tách từ `main`, không tách từ nhánh cũ.
3. Nhật Hòa đã thử tay trên Cockpit thật (vài lệnh của tính năng vừa làm) và báo "ok".
4. Trước khi giao Verify, một agent độc lập (chưa thấy quá trình viết) soát mã + script Verify của task: lỗi build, tên trường JSON, cú pháp PowerShell 5.1, logic kiểm tra. Mọi BLOCKER phải sửa và CI xanh trước khi Nhật Hòa chạy.

## Cuối ca — BẮT BUỘC sau mỗi lần làm xong
1. Cập nhật `qa_report.md`: phát hiện, bản vá, trạng thái kiểm chứng.
2. Viết `03_Local_Data_Vault/handoff/gate<N>_handoff.md` cho Gate đang nghiệm thu (mỗi Gate một file, ví dụ `gate3_handoff.md`; ca sau của cùng Gate thì viết lại file đó) theo đúng khung 5 mục:
   1. Ca này đã làm gì (bảng Trước / Sau)
   2. Trạng thái Gate: PASS hay chưa đủ bằng chứng, kèm checklist bằng chứng E1…En và lệnh chạy
   3. Điểm mở cần Architect quyết định
   4. Đề xuất task tiếp theo, kèm khuyến nghị và lý do
   5. Lời nhắn dán cho Gemini Web
3. Commit, push nhánh, rồi gửi Nhật Hòa lời nhắn ở mục 5 trong chat để dán cho Gemini.
