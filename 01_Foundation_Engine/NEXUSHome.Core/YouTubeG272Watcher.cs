using System;
using System.Diagnostics;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-16 U2: one detection pass (what was found, how long the EnumWindows pass took, what woke it).</summary>
    public sealed record YouTubeCheck(bool Watching, string? Title, string? Process, string? Monitor, bool Simulated, string Reason, double ElapsedMs, string AtUtc);

    /// <summary>
    /// TASK-16 U2 (ADR-62): "is a browser on the MSI G272 showing YouTube?". No timer and no polling: the Cockpit window
    /// listens to Windows events it already gets for free on its UI thread (SetWinEventHook, out of context: focus change,
    /// window title change, move/resize end, minimise / restore, window shown / hidden) and calls <see cref="CheckNow"/>
    /// once after they settle (youtubeCheckDebounceMs, 500 ms). An idle desktop raises no events, so nothing runs (0 % CPU).
    /// A pass is one EnumWindows over visible top-level windows; the process name is looked up only for titles containing
    /// "YouTube". On a change (start / stop watching) config.txt is switched once and <see cref="Changed"/> is raised.
    /// The window centre must be inside the G272 work area; the G272 must be found by name or as the first non-primary
    /// monitor (single-monitor PC: nothing is detected). Active only while the auto-switch is on and the K450 profiles exist.
    /// </summary>
    public sealed class YouTubeG272Watcher
    {
        public static readonly string[] BrowserProcesses = { "chrome", "msedge", "brave", "firefox" };

        private readonly NexusConfigService _config;
        private readonly object _gate = new();
        private TargetMonitor? _monitor;
        private bool _monitorKnown;
        private bool _watching;
        private long _passes;

        public YouTubeG272Watcher(NexusConfigService config)
        {
            _config = config;
        }

        /// <summary>Test hook (pipe YOUTUBE, selftest ui-v2): a browser window with this title counts as being on the G272.</summary>
        public static string? SimulatedTitle { get; set; }

        /// <summary>(watching, check, eq) on the thread that ran the pass (the Cockpit calls it on its UI thread).</summary>
        public event Action<bool, YouTubeCheck, EqSwitchResult?>? Changed;

        public bool Watching { get { lock (_gate) return _watching; } }
        public bool Active { get; private set; }
        public long Passes => Interlocked.Read(ref _passes);
        public YouTubeCheck? LastCheck { get; private set; }
        public EqSwitchResult? LastSwitch { get; private set; }
        public string? InactiveReason { get; private set; }
        public int DebounceMs => _config.Current.YoutubeCheckDebounceMs;

        /// <summary>Decides whether window events should trigger checks (start-up, /reload). Re-reads the monitor layout.</summary>
        public bool Configure()
        {
            NexusConfig c = _config.Current;
            string dir = EqualizerApoSwitcher.ResolveDirectory(c);
            bool active = OperatingSystem.IsWindows() && c.YoutubeEqAutoSwitch && (EqualizerApoSwitcher.IsAvailable(dir) || SimulatedTitle != null);
            InvalidateMonitor();
            Active = active;
            InactiveReason = active ? null
                : !OperatingSystem.IsWindows() ? "not Windows"
                : !c.YoutubeEqAutoSwitch ? "youtubeEqAutoSwitch=false"
                : $"{EqualizerApoSwitcher.ConfigFileName} or {EqualizerApoSwitcher.ProfileFile(EqualizerApoSwitcher.YoutubeProfile)} not found in {dir}";
            return active;
        }

        /// <summary>Monitor hot-plug / resolution change (WM_DISPLAYCHANGE): the next pass re-reads the layout.</summary>
        public void InvalidateMonitor()
        {
            lock (_gate) _monitorKnown = false;
        }

        /// <summary>One pass now. Switches the EQ only when the state changes.</summary>
        public YouTubeCheck CheckNow(string reason = "manual")
        {
            YouTubeCheck check = Detect(reason);
            Interlocked.Increment(ref _passes);
            bool changed;
            lock (_gate)
            {
                changed = check.Watching != _watching;
                _watching = check.Watching;
                LastCheck = check;
            }
            if (!changed) return check;
            EqSwitchResult? eq = null;
            NexusConfig c = _config.Current;
            if (c.YoutubeEqAutoSwitch)
            {
                eq = EqualizerApoSwitcher.Switch(EqualizerApoSwitcher.ResolveDirectory(c),
                    check.Watching ? EqualizerApoSwitcher.YoutubeProfile : EqualizerApoSwitcher.MusicProfile);
                LastSwitch = eq;
            }
            Changed?.Invoke(check.Watching, check, eq);
            return check;
        }

        private YouTubeCheck Detect(string reason)
        {
            long t0 = Stopwatch.GetTimestamp();
            string now = DateTime.UtcNow.ToString("o");
            string? simulated = SimulatedTitle;
            if (simulated != null)
                return new YouTubeCheck(IsYouTubeTitle(simulated), simulated, "simulated", "G272 (simulated)", true, reason, Ms(t0), now);
            if (!OperatingSystem.IsWindows()) return new YouTubeCheck(false, null, null, null, false, reason, Ms(t0), now);

            TargetMonitor? monitor;
            lock (_gate)
            {
                if (!_monitorKnown)
                {
                    _monitor = MonitorLocator.Find(_config.Current.PreferredMonitorKeywords);
                    _monitorKnown = true;
                }
                monitor = _monitor;
            }
            if (monitor == null || monitor.MatchedBy == "PrimaryFallback") return new YouTubeCheck(false, null, null, null, false, reason, Ms(t0), now);

            foreach (VisionSafetyGuard.VisibleWindow w in VisionSafetyGuard.EnumerateVisibleWindows())
            {
                if (!IsYouTubeTitle(w.Title)) continue;
                int cx = (w.Rect.Left + w.Rect.Right) / 2, cy = (w.Rect.Top + w.Rect.Bottom) / 2;
                if (cx < monitor.WorkLeft || cx >= monitor.WorkLeft + monitor.WorkWidth || cy < monitor.WorkTop || cy >= monitor.WorkTop + monitor.WorkHeight) continue;
                string? process = ProcessName(w.ProcessId);
                if (process == null || !IsBrowser(process)) continue;
                return new YouTubeCheck(true, w.Title, process, monitor.FriendlyName, false, reason, Ms(t0), now);
            }
            return new YouTubeCheck(false, null, null, monitor.FriendlyName, false, reason, Ms(t0), now);
        }

        public static bool IsYouTubeTitle(string? title) =>
            !string.IsNullOrEmpty(title) && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase);

        public static bool IsBrowser(string processName) =>
            Array.Exists(BrowserProcesses, b => string.Equals(b, processName, StringComparison.OrdinalIgnoreCase));

        private static string? ProcessName(uint pid)
        {
            try
            {
                using Process p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
        }

        private static double Ms(long t0) => Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
