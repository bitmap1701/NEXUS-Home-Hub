using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NEXUSHome.HostService
{
    public enum HotkeyAction
    {
        ToggleCockpit = 1,
        QuickAgentPtt = 2,
        CycleMfdMode = 3,
        PanicHide = 4
    }

    /// <summary>
    /// Hardware Hotkey Listener tailored for AULA HERO 68 HE (65% Layout, 8000Hz Polling, Rapid Trigger).
    /// Strictly uses Win32 RegisterHotKey with MOD_NOREPEAT on STA HwndSource message pump (ZERO WH_KEYBOARD_LL).
    /// Includes a mandatory 150ms Debounce Gate measured via Stopwatch.GetTimestamp().
    /// </summary>
    public sealed class AulaHero68HotkeyListener : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint VK_BACK = 0x08;
        private const uint VK_SPACE = 0x20;
        private const uint VK_Q = 0x51;

        private const double DebounceWindowMs = 150.0;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly HwndSource _hwndSource;
        private readonly Action<HotkeyAction, long> _onHotkeyTriggered;
        private long _lastAcceptedTriggerTicks;
        private bool _disposed;

        public IntPtr MessageWindowHandle => _hwndSource.Handle;

        public AulaHero68HotkeyListener(Action<HotkeyAction, long> onHotkeyTriggered)
        {
            _onHotkeyTriggered = onHotkeyTriggered;

            var parameters = new HwndSourceParameters("NEXUS_AulaHero68_MessagePump")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE (message-only window, 0 GPU/UI overhead)
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);

            RegisterDefault65PercentBindings();
        }

        private void RegisterDefault65PercentBindings()
        {
            IntPtr hwnd = _hwndSource.Handle;

            // 1. Alt + Space -> ToggleCockpit
            RegisterHotKey(hwnd, (int)HotkeyAction.ToggleCockpit, MOD_ALT | MOD_NOREPEAT, VK_SPACE);

            // 2. Ctrl + Shift + Space -> QuickAgentPtt
            RegisterHotKey(hwnd, (int)HotkeyAction.QuickAgentPtt, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE);

            // 3. Alt + Q -> CycleMfdMode
            RegisterHotKey(hwnd, (int)HotkeyAction.CycleMfdMode, MOD_ALT | MOD_NOREPEAT, VK_Q);

            // 4. Alt + Backspace -> PanicHide
            RegisterHotKey(hwnd, (int)HotkeyAction.PanicHide, MOD_ALT | MOD_NOREPEAT, VK_BACK);
        }

        /// <summary>
        /// Dispatches a real Win32 WM_HOTKEY message through the STA HwndSource pump for deterministic latency benchmarking.
        /// </summary>
        public void DispatchSyntheticWmHotkey(HotkeyAction action)
        {
            SendMessage(_hwndSource.Handle, WM_HOTKEY, new IntPtr((int)action), IntPtr.Zero);
        }

        public void ResetDebounceGateForBenchmark()
        {
            _lastAcceptedTriggerTicks = 0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                if (_lastAcceptedTriggerTicks != 0)
                {
                    double elapsedMs = (nowTicks - _lastAcceptedTriggerTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs < DebounceWindowMs)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }

                _lastAcceptedTriggerTicks = nowTicks;
                int id = wParam.ToInt32();
                if (Enum.IsDefined(typeof(HotkeyAction), id))
                {
                    handled = true;
                    _onHotkeyTriggered((HotkeyAction)id, nowTicks);
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            IntPtr hwnd = _hwndSource.Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, (int)HotkeyAction.ToggleCockpit);
                UnregisterHotKey(hwnd, (int)HotkeyAction.QuickAgentPtt);
                UnregisterHotKey(hwnd, (int)HotkeyAction.CycleMfdMode);
                UnregisterHotKey(hwnd, (int)HotkeyAction.PanicHide);
            }

            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
        }
    }
}
