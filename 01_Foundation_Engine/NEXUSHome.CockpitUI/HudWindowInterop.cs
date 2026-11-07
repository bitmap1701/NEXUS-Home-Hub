using System;
using System.Runtime.InteropServices;

namespace NEXUSHome.CockpitUI
{
    /// <summary>
    /// Win32 helpers for the Cockpit HUD window (Gate 2 decisions 3 and 4).
    /// - WS_EX_TOOLWINDOW: never in Alt+Tab / taskbar.
    /// - WS_EX_NOACTIVATE (HUD mode): showing or clicking the HUD never takes focus from a fullscreen game.
    ///   Cleared only for command mode (Ctrl+Shift+Space) so the command box can receive keyboard input.
    /// - Physical-pixel move via SetWindowPos, which stays correct under PerMonitorV2 DPI awareness.
    /// </summary>
    internal static class HudWindowInterop
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_NOACTIVATE = 0x08000000L;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

#pragma warning disable CS0649 // filled by the marshaler
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
#pragma warning restore CS0649

        /// <summary>Applies WS_EX_TOOLWINDOW and sets or clears WS_EX_NOACTIVATE. Returns the new extended style.</summary>
        public static long ApplyHudStyle(IntPtr hwnd, bool noActivate)
        {
            if (hwnd == IntPtr.Zero) return 0;

            long style = IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
                : GetWindowLong32(hwnd, GWL_EXSTYLE);

            long updated = style | WS_EX_TOOLWINDOW;
            updated = noActivate ? updated | WS_EX_NOACTIVATE : updated & ~WS_EX_NOACTIVATE;
            if (updated == style) return style;

            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
            else
                SetWindowLong32(hwnd, GWL_EXSTYLE, unchecked((int)updated));

            // ADR-10 (TASK-05): SWP_FRAMECHANGED forces a full non-client recalculation + software re-render of the
            // layered window (the 28–79 ms seen in Gate 4). WS_EX_NOACTIVATE is not a frame style, so toggling it only
            // needs the plain SetWindowPos that flushes cached window data; FRAMECHANGED is kept for the one-time
            // WS_EX_TOOLWINDOW change (first show, taskbar / Alt+Tab removal).
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
            if (((updated ^ style) & WS_EX_TOOLWINDOW) != 0) flags |= SWP_FRAMECHANGED;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
            return updated;
        }

        public static bool TryGetWindowSize(IntPtr hwnd, out int width, out int height)
        {
            width = height = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return false;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width > 0 && height > 0;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        public static IntPtr Foreground() => GetForegroundWindow();

        /// <summary>Focus Restoration (TASK-04): give keyboard focus back to the game / IDE window.</summary>
        public static bool RestoreForeground(IntPtr hwnd) =>
            hwnd != IntPtr.Zero && IsWindow(hwnd) && SetForegroundWindow(hwnd);

        public static bool MoveTo(IntPtr hwnd, int x, int y) =>
            hwnd != IntPtr.Zero &&
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
