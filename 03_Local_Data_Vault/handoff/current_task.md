# TASK-02: NEXUSHome.HostService & NEXUSHome.CockpitUI (AULA HERO 68 HE + MSI G272 iGPU Cockpit)

## 1. Yêu cầu Tiền kiểm (Reality Checker Gate 1 -> Gate 2)
- [x] `SqliteMemoryStore`: Thiết lập tường minh `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;`
- [x] `NEXUSHome.McpTools`: Sử dụng `new UTF8Encoding(false)` (không phát sinh BOM) cho `Console.InputEncoding` và `Console.OutputEncoding`.

## 2. Cấu trúc Project TASK-02
- `01_Foundation_Engine/NEXUSHome.CockpitUI`: .NET 8 WPF Library/Application hiển thị Cockpit 3 phân vùng (Left MFD: Telemetry SQLite WAL & RAM; Center HUD: Command Input auto-focus + Virtualized Output; Right MFD: 4 công tắc điều khiển nhanh), tối ưu cho iGPU Intel UHD 730 trên màn MSI G272.
- `01_Foundation_Engine/NEXUSHome.HostService`: .NET 8 Windows Background Host (`OutputType=WinExe`), quản lý System Tray, Named Pipe Server (`NEXUSHome.Cockpit.v1`), Lazy Warm Window và `AulaHero68HotkeyListener` (`RegisterHotKey` + `MOD_NOREPEAT` + `150ms` Debounce Gate bằng `Stopwatch.GetTimestamp()`).

## 3. Ràng buộc Kỹ thuật Bắt buộc
1. **Bàn phím AULA HERO 68 HE (65% Layout, 8000Hz Polling)**:
   - CẤM dùng `WH_KEYBOARD_LL`. Dùng P/Invoke `RegisterHotKey` (`user32.dll`) với `MOD_NOREPEAT` trên luồng STA Message Pump.
   - Tổ hợp phím mặc định:
     - `Alt + Space`: `ToggleCockpit`
     - `Ctrl + Shift + Space`: `QuickAgentPtt`
     - `Alt + Q`: `CycleMfdMode`
     - `Alt + Backspace` (hoặc `Esc` khi đang mở): `PanicHide`
   - Chốt chặn chống dội phím (Debounce Gate) `150ms` tính bằng `Stopwatch.GetTimestamp()`.
2. **Đồ họa iGPU Intel UHD 730 & Màn hình MSI G272**:
   - CẤM dùng `BlurEffect` hoặc `DropShadowEffect`.
   - Nền `#EB090D16`, viền Vector 1px `#3338BDF8`, bật `UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`, `TextOptions.TextFormattingMode="Display"`.
   - Khi ẩn Cockpit, đạt **0.0% GPU** và **0.0% CPU** khi nghỉ.
