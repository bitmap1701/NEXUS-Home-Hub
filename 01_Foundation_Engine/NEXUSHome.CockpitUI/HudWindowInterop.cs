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

        // ---------------------------------------------------------------- TASK-16 U1 / B6 / U2

        private const long WS_EX_TRANSPARENT = 0x00000020L;
        private const long WS_EX_LAYERED = 0x00080000L;

        public static long GetExStyle(IntPtr hwnd) =>
            hwnd == IntPtr.Zero ? 0 : IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

        public static bool HasTransparent(long exStyle) => (exStyle & WS_EX_TRANSPARENT) != 0;

        public static bool HasLayered(long exStyle) => (exStyle & WS_EX_LAYERED) != 0;

        /// <summary>
        /// TASK-16 U1 click-through: on = WS_EX_TRANSPARENT | WS_EX_LAYERED (mouse clicks and wheel go to the window
        /// behind, e.g. YouTube on the G272). Off clears WS_EX_TRANSPARENT only: WS_EX_LAYERED stays because the WPF window
        /// uses AllowsTransparency (per-pixel alpha needs it). Returns the new extended style (0 = no window).
        /// </summary>
        public static long SetClickThrough(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero) return 0;
            long style = GetExStyle(hwnd);
            long updated = on ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT;
            if (updated == style) return style;
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
            else
                SetWindowLong32(hwnd, GWL_EXSTYLE, unchecked((int)updated));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return updated;
        }

        /// <summary>Window rectangle in physical pixels of the virtual screen.</summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out int left, out int top, out int width, out int height)
        {
            left = top = width = height = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return false;
            left = rect.Left;
            top = rect.Top;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width > 0 && height > 0;
        }

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MessageBeep(uint type);

        /// <summary>TASK-16b: the system "asterisk" sound (shiftCompleteSound = true); no audio library is loaded.</summary>
        public static void Beep() => MessageBeep(0x00000040);

        /// <summary>A top-level window (not a child control, caret or accessibility sub-object).</summary>
        public static bool IsTopLevel(IntPtr hwnd) => hwnd != IntPtr.Zero && GetAncestor(hwnd, GA_ROOT) == hwnd;
    }

    /// <summary>
    /// TASK-16 U2: out-of-context WinEvent hooks on the Cockpit UI thread (its message loop delivers them; nothing is
    /// injected into other processes, no extra thread, no polling). Events of this process are skipped. The callback only
    /// filters and calls <see cref="Raised"/>; the Cockpit debounces it with a one-shot timer.
    ///   EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZESTART..END, EVENT_OBJECT_SHOW..HIDE,
    ///   EVENT_OBJECT_NAMECHANGE (title change: a YouTube tab opened / closed).
    /// </summary>
    internal sealed class WinEventSubscription : IDisposable
    {
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int OBJID_WINDOW = 0;
        private const int CHILDID_SELF = 0;

        private static readonly (uint Min, uint Max)[] Ranges =
        {
            (0x0003, 0x0003), // EVENT_SYSTEM_FOREGROUND
            (0x000B, 0x000B), // EVENT_SYSTEM_MOVESIZEEND
            (0x0016, 0x0017), // EVENT_SYSTEM_MINIMIZESTART / MINIMIZEEND
            (0x8002, 0x8003), // EVENT_OBJECT_SHOW / HIDE
            (0x800C, 0x800C)  // EVENT_OBJECT_NAMECHANGE
        };

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private readonly WinEventDelegate _callback; // kept alive for as long as the hooks exist
        private readonly IntPtr[] _hooks;
        private long _received;
        private long _accepted;

        public WinEventSubscription(Action<uint> raised)
        {
            Raised = raised;
            _callback = OnEvent;
            _hooks = new IntPtr[Ranges.Length];
            for (int i = 0; i < Ranges.Length; i++)
                _hooks[i] = SetWinEventHook(Ranges[i].Min, Ranges[i].Max, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        public Action<uint> Raised { get; }

        public int HookCount => Array.FindAll(_hooks, h => h != IntPtr.Zero).Length;

        public long Received => System.Threading.Interlocked.Read(ref _received);

        public long Accepted => System.Threading.Interlocked.Read(ref _accepted);

        private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            System.Threading.Interlocked.Increment(ref _received);
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            if (eventType != 0x0003 && !HudWindowInterop.IsTopLevel(hwnd)) return;
            System.Threading.Interlocked.Increment(ref _accepted);
            Raised(eventType);
        }

        public void Dispose()
        {
            for (int i = 0; i < _hooks.Length; i++)
            {
                if (_hooks[i] != IntPtr.Zero) UnhookWinEvent(_hooks[i]);
                _hooks[i] = IntPtr.Zero;
            }
        }
    }
}
