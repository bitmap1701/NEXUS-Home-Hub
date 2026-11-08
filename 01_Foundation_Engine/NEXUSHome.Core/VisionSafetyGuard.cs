using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-12: a screen rectangle in physical pixels (left/top inclusive, right/bottom exclusive).</summary>
    public readonly record struct VisionRect(int Left, int Top, int Right, int Bottom);

    /// <summary>TASK-12 §3.3: screen rectangle /snap may copy (physical pixels) and the NEXUS windows to paint black in it.</summary>
    public sealed record VisionSnapRegion(
        string Monitor,
        string MatchedBy,
        int Left,
        int Top,
        int Width,
        int Height,
        IReadOnlyList<VisionRect> Blackouts);

    /// <summary>TASK-12 §3.3: decision of the two /snap shields. Allowed = false means nothing was captured.</summary>
    public sealed record VisionGuardResult(
        bool Allowed,
        string? BlockedBy,          // "combat" | "privacy" | null
        string Message,
        string? Keyword,
        string? MatchedTitle,
        bool Simulated,
        IReadOnlyList<string> CombatProcesses,
        int WindowsChecked,
        double ElapsedMs,
        VisionSnapRegion? Region);

    /// <summary>
    /// TASK-12 §3.3 (ADR-61): the two hard shields in front of every screen capture.
    /// 1. Combat-Mode Shield: a process from combatProcessPrefixes runs (or NEXUS_SIMULATE_COMBAT_MODE=1) → blocked before any
    ///    window or screen API is touched (no GDI BitBlt, no DXGI), so nothing reads the screen while an anti-cheat is loaded.
    /// 2. Privacy Window Guard: the foreground window and every visible window on the capture area are matched against
    ///    privacyRedactedWindowKeywords (case-insensitive substring). NEXUS_SIMULATE_WINDOW_TITLE adds one more title to check;
    ///    it can only cause a block, never lift one (the real windows are always checked too).
    /// Only when both pass does /snap get a region (MSI G272 work area by preferredMonitorKeywords, else the first secondary,
    /// else the primary) plus the NEXUS HostService windows to paint black.
    /// </summary>
    public static class VisionSafetyGuard
    {
        public const string SimulateCombatVariable = "NEXUS_SIMULATE_COMBAT_MODE";
        public const string SimulateWindowTitleVariable = "NEXUS_SIMULATE_WINDOW_TITLE";

        public const string CombatShieldMessage =
            "[COMBAT SHIELD] /snap screen capture is disabled while combat/anti-cheat processes are active. Use /img with clipboard image instead.";

        public static string PrivacyGuardMessage(string keyword) =>
            $"[PRIVACY GUARD] /snap blocked because a sensitive window title was detected ({keyword}).";

        public static bool SimulatedCombat => Environment.GetEnvironmentVariable(SimulateCombatVariable)?.Trim() == "1";

        /// <summary>First keyword of <paramref name="keywords"/> contained in <paramref name="title"/> (ignoring case), or null.</summary>
        public static string? MatchKeyword(string? title, IReadOnlyList<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            foreach (string keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) && title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)) return keyword.Trim();
            }
            return null;
        }

        /// <summary>Runs both shields for /snap. Never throws; never captures anything itself.</summary>
        /// <param name="previousForeground">Window that had focus before the Cockpit took it (checked like the foreground window).</param>
        public static VisionGuardResult CheckSnap(NexusConfig config, IntPtr previousForeground = default)
        {
            long t0 = Stopwatch.GetTimestamp();

            // 1. Combat-Mode Shield (first, before any window enumeration).
            if (SimulatedCombat)
            {
                return new VisionGuardResult(false, "combat", CombatShieldMessage, null, null, true,
                    new[] { $"{SimulateCombatVariable}=1" }, 0, Ms(t0), null);
            }
            DispatchSlaProfile sla = NexusConfigService.DetectSla(config);
            if (sla.MatchedProcesses.Count > 0)
            {
                return new VisionGuardResult(false, "combat", CombatShieldMessage, null, null, false, sla.MatchedProcesses, 0, Ms(t0), null);
            }

            if (!OperatingSystem.IsWindows())
            {
                return new VisionGuardResult(false, "platform", "[VISION INFO] /snap needs Windows.", null, null, false,
                    Array.Empty<string>(), 0, Ms(t0), null);
            }

            // 2. Privacy Window Guard, in physical pixels so window rectangles and the capture area agree.
            IReadOnlyList<string> keywords = config.PrivacyRedactedWindowKeywords ?? NexusConfig.DefaultPrivacyRedactedWindowKeywords;
            using var dpi = new PerMonitorDpiScope();
            string? simulated = Environment.GetEnvironmentVariable(SimulateWindowTitleVariable);
            if (MatchKeyword(simulated, keywords) is string simulatedKeyword)
            {
                return new VisionGuardResult(false, "privacy", PrivacyGuardMessage(simulatedKeyword), simulatedKeyword, simulated, true,
                    Array.Empty<string>(), 1, Ms(t0), null);
            }

            TargetMonitor? monitor = MonitorLocator.Find(config.PreferredMonitorKeywords);
            if (monitor == null)
            {
                return new VisionGuardResult(false, "platform", "[VISION INFO] /snap: no monitor found.", null, null, false,
                    Array.Empty<string>(), 0, Ms(t0), null);
            }
            var area = new Win32Rect(monitor.WorkLeft, monitor.WorkTop, monitor.WorkLeft + monitor.WorkWidth, monitor.WorkTop + monitor.WorkHeight);
            HashSet<uint> nexusPids = NexusProcessIds();
            IntPtr foreground = GetForegroundWindow();
            var blackouts = new List<VisionRect>();
            int checkedCount = 0;
            foreach (VisibleWindow w in EnumerateVisibleWindows())
            {
                bool nexus = nexusPids.Contains(w.ProcessId);
                bool onArea = w.Rect.Intersects(area);
                if (nexus)
                {
                    if (onArea) blackouts.Add(new VisionRect(w.Rect.Left, w.Rect.Top, w.Rect.Right, w.Rect.Bottom));
                    continue; // the Cockpit itself is never "sensitive" and is painted black anyway
                }
                if (!onArea && w.Handle != foreground && w.Handle != previousForeground) continue;
                checkedCount++;
                if (MatchKeyword(w.Title, keywords) is string keyword)
                {
                    return new VisionGuardResult(false, "privacy", PrivacyGuardMessage(keyword), keyword, w.Title, false,
                        Array.Empty<string>(), checkedCount, Ms(t0), null);
                }
            }

            var region = new VisionSnapRegion(monitor.FriendlyName, monitor.MatchedBy, monitor.WorkLeft, monitor.WorkTop,
                monitor.WorkWidth, monitor.WorkHeight, blackouts);
            return new VisionGuardResult(true, null, $"[SNAP] Khiên an toàn: OK ({checkedCount} cửa sổ đã kiểm tra) → {monitor.FriendlyName}.",
                null, null, false, Array.Empty<string>(), checkedCount, Ms(t0), region);
        }

        /// <summary>Process ids whose windows are NEXUS UI (HostService = Cockpit) plus this process.</summary>
        private static HashSet<uint> NexusProcessIds()
        {
            var set = new HashSet<uint> { (uint)Environment.ProcessId };
            foreach (Process p in Process.GetProcessesByName("NEXUSHome.HostService"))
            {
                using (p) set.Add((uint)p.Id);
            }
            return set;
        }

        internal readonly record struct Win32Rect(int Left, int Top, int Right, int Bottom)
        {
            public bool Intersects(Win32Rect o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
        }

        internal sealed record VisibleWindow(IntPtr Handle, string Title, Win32Rect Rect, uint ProcessId);

        /// <summary>Visible, not minimised, not cloaked top-level windows that have a title.</summary>
        internal static List<VisibleWindow> EnumerateVisibleWindows()
        {
            var list = new List<VisibleWindow>();
            EnumWindowsProc callback = (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                int length = GetWindowTextLength(hwnd);
                if (length <= 0) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(hwnd, text, text.Capacity);
                if (!GetWindowRect(hwnd, out RECT r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                list.Add(new VisibleWindow(hwnd, text.ToString(), new Win32Rect(r.Left, r.Top, r.Right, r.Bottom), pid));
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return list;
        }

        /// <summary>Per-Monitor-V2 DPI for this thread while it lives (McpTools is not DPI aware by manifest; HostService is).</summary>
        internal sealed class PerMonitorDpiScope : IDisposable
        {
            private readonly IntPtr _previous;

            public PerMonitorDpiScope()
            {
                try { _previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { _previous = IntPtr.Zero; } // before Windows 10 1607
            }

            public void Dispose()
            {
                if (_previous == IntPtr.Zero) return;
                try { SetThreadDpiAwarenessContext(_previous); }
                catch (EntryPointNotFoundException) { }
            }
        }

        private static double Ms(long startTicks) =>
            Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);

        // ---------------- Win32 interop ----------------
        private const int DwmwaCloaked = 14;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    }
}
