using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-16 U2: one detection pass (what was found, how long the EnumWindows pass took).</summary>
    public sealed record YouTubeCheck(bool Watching, string? Title, string? Process, string? Monitor, bool Simulated, double ElapsedMs, string AtUtc);

    /// <summary>
    /// TASK-16 U2 (ADR-62): "is a browser on the MSI G272 showing YouTube?". The Cockpit itself has no periodic cycle
    /// (it is event-driven), so this adds one low-frequency pass (every youtubeCheckIntervalMs, 3 s by default: one
    /// EnumWindows over visible top-level windows, a process name looked up only for titles that contain "YouTube").
    /// The timer runs only while the auto-switch is on and the Equalizer APO K450 profiles exist; otherwise no thread wakes
    /// up at all. On a change (start / stop watching) it switches config.txt once and raises <see cref="Changed"/>.
    /// Windows that are minimised or cloaked do not count; the window centre must be inside the G272 work area.
    /// The G272 must be found by name or as the first non-primary monitor: on a single-monitor PC nothing is detected.
    /// </summary>
    public sealed class YouTubeG272Watcher : IDisposable
    {
        public static readonly string[] BrowserProcesses = { "chrome", "msedge", "brave", "firefox" };
        private const int MonitorRefreshEvery = 20; // the monitor layout is re-read every 20 passes (~1 min), or on demand

        private readonly NexusConfigService _config;
        private readonly object _gate = new();
        private Timer? _timer;
        private TargetMonitor? _monitor;
        private int _passesSinceMonitor = int.MaxValue;
        private bool _watching;
        private long _passes;

        public YouTubeG272Watcher(NexusConfigService config)
        {
            _config = config;
        }

        /// <summary>Test hook (pipe YOUTUBE, selftest ui-v2): a browser window with this title counts as being on the G272.</summary>
        public static string? SimulatedTitle { get; set; }

        /// <summary>(watching, check, eq) on the thread that ran the pass; the Cockpit marshals to the UI thread.</summary>
        public event Action<bool, YouTubeCheck, EqSwitchResult?>? Changed;

        public bool Watching { get { lock (_gate) return _watching; } }
        public bool Running { get { lock (_gate) return _timer != null; } }
        public long Passes => Interlocked.Read(ref _passes);
        public YouTubeCheck? LastCheck { get; private set; }
        public EqSwitchResult? LastSwitch { get; private set; }
        public string? InactiveReason { get; private set; }

        /// <summary>Starts or stops the timer according to the config and the EQ files (call at start-up and after /reload).</summary>
        public void Configure()
        {
            NexusConfig c = _config.Current;
            string dir = EqualizerApoSwitcher.ResolveDirectory(c);
            bool active = OperatingSystem.IsWindows() && c.YoutubeEqAutoSwitch && (EqualizerApoSwitcher.IsAvailable(dir) || SimulatedTitle != null);
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _passesSinceMonitor = int.MaxValue;
                InactiveReason = active ? null
                    : !c.YoutubeEqAutoSwitch ? "youtubeEqAutoSwitch=false"
                    : $"{EqualizerApoSwitcher.ConfigFileName} or {EqualizerApoSwitcher.ProfileFile(EqualizerApoSwitcher.YoutubeProfile)} not found in {dir}";
                if (active) _timer = new Timer(_ => CheckNow(), null, c.YoutubeCheckIntervalMs, c.YoutubeCheckIntervalMs);
            }
        }

        /// <summary>One pass now (also the timer callback). Switches the EQ only when the state changes.</summary>
        public YouTubeCheck CheckNow()
        {
            YouTubeCheck check = Detect();
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

        private YouTubeCheck Detect()
        {
            long t0 = Stopwatch.GetTimestamp();
            string now = DateTime.UtcNow.ToString("o");
            string? simulated = SimulatedTitle;
            if (simulated != null)
                return new YouTubeCheck(IsYouTubeTitle(simulated), simulated, "simulated", "G272 (simulated)", true, Ms(t0), now);
            if (!OperatingSystem.IsWindows()) return new YouTubeCheck(false, null, null, null, false, Ms(t0), now);

            TargetMonitor? monitor;
            lock (_gate)
            {
                if (_passesSinceMonitor >= MonitorRefreshEvery)
                {
                    _monitor = MonitorLocator.Find(_config.Current.PreferredMonitorKeywords);
                    _passesSinceMonitor = 0;
                }
                _passesSinceMonitor++;
                monitor = _monitor;
            }
            if (monitor == null || monitor.MatchedBy == "PrimaryFallback") return new YouTubeCheck(false, null, null, null, false, Ms(t0), now);

            foreach (VisionSafetyGuard.VisibleWindow w in VisionSafetyGuard.EnumerateVisibleWindows())
            {
                if (!IsYouTubeTitle(w.Title)) continue;
                int cx = (w.Rect.Left + w.Rect.Right) / 2, cy = (w.Rect.Top + w.Rect.Bottom) / 2;
                if (cx < monitor.WorkLeft || cx >= monitor.WorkLeft + monitor.WorkWidth || cy < monitor.WorkTop || cy >= monitor.WorkTop + monitor.WorkHeight) continue;
                string? process = ProcessName(w.ProcessId);
                if (process == null || !IsBrowser(process)) continue;
                return new YouTubeCheck(true, w.Title, process, monitor.FriendlyName, false, Ms(t0), now);
            }
            return new YouTubeCheck(false, null, null, monitor.FriendlyName, false, Ms(t0), now);
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

        public void Dispose()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
