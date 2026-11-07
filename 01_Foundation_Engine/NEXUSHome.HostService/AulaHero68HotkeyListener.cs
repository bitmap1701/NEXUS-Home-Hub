using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NEXUSHomeHub.Core;

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

        private readonly Action<string>? _log;

        private readonly HotkeyChord _passiveChord;
        private readonly HotkeyChord _commandChord;

        /// <param name="passiveHotkey">nexus_config.json "passiveHudHotkey" (TASK-06); invalid -> Alt+Space.</param>
        /// <param name="commandHotkey">nexus_config.json "commandModeHotkey"; invalid -> Ctrl+Shift+Space.</param>
        public AulaHero68HotkeyListener(Action<HotkeyAction, long> onHotkeyTriggered, Action<string>? log = null,
            string? passiveHotkey = null, string? commandHotkey = null)
        {
            _onHotkeyTriggered = onHotkeyTriggered;
            _log = log;
            _passiveChord = ParseOr(passiveHotkey, new HotkeyChord(MOD_ALT, VK_SPACE, "Alt+Space"));
            _commandChord = ParseOr(commandHotkey, new HotkeyChord(MOD_CONTROL | MOD_SHIFT, VK_SPACE, "Ctrl+Shift+Space"));

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

        private HotkeyChord ParseOr(string? text, HotkeyChord fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (HotkeyChord.TryParse(text, out HotkeyChord? chord) && chord != null) return chord;
            _log?.Invoke($"[HOTKEY] '{text}' không hợp lệ trong nexus_config.json: dùng {fallback.Text}");
            return fallback;
        }

        private void RegisterDefault65PercentBindings()
        {
            IntPtr hwnd = _hwndSource.Handle;

            // 1. Passive HUD (default Alt + Space) -> ToggleCockpit
            Register(hwnd, HotkeyAction.ToggleCockpit, _passiveChord.Modifiers | MOD_NOREPEAT, _passiveChord.VirtualKey, _passiveChord.Text);

            // 2. Command mode (default Ctrl + Shift + Space) -> QuickAgentPtt
            Register(hwnd, HotkeyAction.QuickAgentPtt, _commandChord.Modifiers | MOD_NOREPEAT, _commandChord.VirtualKey, _commandChord.Text);

            // 3. Alt + Q -> CycleMfdMode
            Register(hwnd, HotkeyAction.CycleMfdMode, MOD_ALT | MOD_NOREPEAT, VK_Q, "Alt+Q");

            // 4. Alt + Backspace -> PanicHide
            Register(hwnd, HotkeyAction.PanicHide, MOD_ALT | MOD_NOREPEAT, VK_BACK, "Alt+Backspace");
        }

        private void Register(IntPtr hwnd, HotkeyAction action, uint modifiers, uint vk, string label)
        {
            if (RegisterHotKey(hwnd, (int)action, modifiers, vk))
            {
                _log?.Invoke($"[HOTKEY] Registered {label} -> {action}");
            }
            else
            {
                // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED (another app, e.g. PowerToys Run, owns the combo).
                _log?.Invoke($"[HOTKEY] FAILED {label} -> {action} (Win32 error {Marshal.GetLastWin32Error()})");
            }
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
                        _log?.Invoke($"[HOTKEY] Debounced id={wParam.ToInt32()} ({elapsedMs:F1} ms < {DebounceWindowMs} ms)");
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
