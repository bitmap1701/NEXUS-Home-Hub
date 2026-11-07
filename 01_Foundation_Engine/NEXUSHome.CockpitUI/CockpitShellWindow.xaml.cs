using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NEXUSHomeHub.Core;

namespace NEXUSHome.CockpitUI
{
    public partial class CockpitShellWindow : Window
    {
        private readonly SqliteMemoryStore _memoryStore;
        private readonly AgentRegistry _agentRegistry;
        private readonly ObservableCollection<string> _outputLines = new();
        private readonly string[] _mfdModes = { "TELEMETRY_WAL", "AGENT_SQUAD_283", "IGPU_GUARD_MSI_G272" };
        private int _currentMfdIndex;
        private bool _isInitializedUi;

        public double LastWakeupLatencyMs { get; private set; }

        /// <summary>Human-readable description of the monitor the Cockpit is pinned to (for logs and the MFD).</summary>
        public string TargetMonitorInfo { get; private set; } = "Unresolved";

        public CockpitShellWindow(SqliteMemoryStore memoryStore, AgentRegistry agentRegistry)
        {
            _memoryStore = memoryStore;
            _agentRegistry = agentRegistry;
            InitializeComponent();
            VirtualizedOutputList.ItemsSource = _outputLines;

            AppendOutput("[SYSTEM] NEXUS Cockpit initialized (Zero-Blur Vector HUD for Intel UHD 730).");
            AppendOutput("[HOTKEY] AULA HERO 68 HE 65% bindings active (150ms Debounce Gate).");
            _isInitializedUi = true;
        }

        /// <summary>
        /// Pre-creates the underlying Win32 HWND and warms the WPF layout/glyph tree in hidden state
        /// so WM_HOTKEY (Alt+Space) wakeup is consistently under 16ms (typically < 3ms).
        /// </summary>
        public void WarmUpHidden()
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero)
            {
                helper.EnsureHandle();
                HwndSource.FromHwnd(helper.Handle)?.AddHook(OnWindowMessage);
            }
            ApplyHudMode(noActivate: true);
            PositionOnTargetMonitor();

            Opacity = 0.0;
            Show();
            UpdateLayout();
            CommandInputBox.Focus();
            Keyboard.Focus(CommandInputBox);
            Hide();
            Opacity = 1.0;
        }

        public double ShowAndFocusCockpit(long triggerTimestampTicks, string reason = "ToggleCockpit")
        {
            // Command mode: drop WS_EX_NOACTIVATE so the command box can take keyboard focus.
            ApplyHudMode(noActivate: false);
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            Activate();
            CommandInputBox.Focus();
            Keyboard.Focus(CommandInputBox);

            return RecordWakeup(triggerTimestampTicks, reason);
        }

        /// <summary>
        /// Shows the Cockpit WITHOUT activating it (XAML ShowActivated="False"): the game on the primary
        /// display keeps keyboard focus, so Exclusive Fullscreen titles are not minimized.
        /// Use Ctrl+Shift+Space (QuickAgentPtt) to focus the command box.
        /// </summary>
        public double ShowPassiveCockpit(long triggerTimestampTicks, string reason = "PassiveShow")
        {
            // HUD mode: WS_EX_NOACTIVATE, so neither showing nor clicking the HUD steals focus from the game.
            ApplyHudMode(noActivate: true);
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            return RecordWakeup(triggerTimestampTicks, reason);
        }

        private double RecordWakeup(long triggerTimestampTicks, string reason)
        {
            long doneTicks = Stopwatch.GetTimestamp();
            LastWakeupLatencyMs = (doneTicks - triggerTimestampTicks) * 1000.0 / Stopwatch.Frequency;

            double capturedLatency = LastWakeupLatencyMs;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                LatencyBadgeText.Text = $"WAKE: {capturedLatency:F2}ms | RENDER: {System.Windows.Media.RenderOptions.ProcessRenderMode} | PIPE: NEXUS_Home_Cockpit_Pipe";
                RefreshTelemetrySnapshot();
                AppendOutput($"[{DateTime.Now:HH:mm:ss.fff}] Wakeup ({reason}) in {capturedLatency:F2} ms.");
            }));

            return LastWakeupLatencyMs;
        }

        public void HideCockpit(string reason = "Hide", bool trimWorkingSet = false)
        {
            Visibility = Visibility.Hidden;
            if (trimWorkingSet)
            {
                LocalPcMcpTools.TrimCurrentProcessWorkingSet();
            }
        }

        public void CycleMfdMode()
        {
            _currentMfdIndex = (_currentMfdIndex + 1) % _mfdModes.Length;
            string mode = _mfdModes[_currentMfdIndex];
            MfdModeBadgeText.Text = $"[MFD: {mode}]";
            RefreshTelemetrySnapshot();
            AppendOutput($"[{DateTime.Now:HH:mm:ss}] Cycled MFD mode -> {mode}");
        }

        public void TriggerQuickAgentPtt(long triggerTimestampTicks)
        {
            ShowAndFocusCockpit(triggerTimestampTicks, "QuickAgentPtt");
            ToggleVoicePtt.IsChecked = true;
            CommandInputBox.Text = "@agent ";
            CommandInputBox.CaretIndex = CommandInputBox.Text.Length;
        }

        public void RefreshTelemetrySnapshot()
        {
            using var proc = Process.GetCurrentProcess();
            double workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            double privateMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
            var (journalMode, auditCount, memCount) = _memoryStore.GetWalTelemetrySummary();

            string mode = _mfdModes[_currentMfdIndex];
            if (mode == "TELEMETRY_WAL")
            {
                LeftMfdContentText.Text =
                    $"SQLite Mode : {journalMode} (5000ms)\n" +
                    $"Audit Logs  : {auditCount} records\n" +
                    $"Memories    : {memCount} records\n" +
                    $"Working Set : {workingSetMb:F1} MB\n" +
                    $"Private Mem : {privateMb:F1} MB\n" +
                    $"CPU Threads : {Environment.ProcessorCount} (i5-12400)\n" +
                    $"Wake Latency: {LastWakeupLatencyMs:F2} ms";
            }
            else if (mode == "AGENT_SQUAD_283")
            {
                var sampleAgents = _agentRegistry.ListAvailableAgents(6);
                LeftMfdContentText.Text =
                    "Active Squad:\n" +
                    "- agents-orchestrator\n" +
                    "- desktop-app-engineer\n" +
                    "- xr-cockpit-specialist\n" +
                    "- reality-checker\n" +
                    $"Loaded Skills: {sampleAgents.Count}+ ready";
            }
            else
            {
                LeftMfdContentText.Text =
                    $"Display Target: {TargetMonitorInfo}\n" +
                    "Refresh Rate  : 144 Hz\n" +
                    "Render Engine : Vector 1px\n" +
                    "Blur/Shadow   : DISABLED (0%)\n" +
                    "Primary GPU   : GTX 1660 Free\n" +
                    $"Cockpit RAM   : {workingSetMb:F1} MB";
            }
        }

        /// <summary>
        /// 3-tier monitor routing (Gate 2 decision 3): EDID friendly name / env NEXUS_COCKPIT_MONITOR (default "G272")
        /// -> first non-primary monitor -> primary monitor, docked top-right as a HUD.
        /// Moves in physical pixels (SetWindowPos), which is correct under PerMonitorV2 DPI awareness.
        /// </summary>
        private void PositionOnTargetMonitor()
        {
            string hint = Environment.GetEnvironmentVariable("NEXUS_COCKPIT_MONITOR") ?? "G272";
            TargetMonitor? target = MonitorLocator.Find(hint);
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

            if (target is null || !HudWindowInterop.TryGetWindowSize(hwnd, out int widthPx, out _))
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Right - Width - 16.0;
                Top = workArea.Top + 16.0;
                TargetMonitorInfo = "Fallback: primary work area (top-right HUD)";
                return;
            }

            const int MarginPx = 16;
            bool isPrimaryFallback = target.MatchedBy == "PrimaryFallback";
            int x = isPrimaryFallback
                ? target.WorkLeft + target.WorkWidth - widthPx - MarginPx
                : target.WorkLeft + Math.Max(0, (target.WorkWidth - widthPx) / 2);
            int y = isPrimaryFallback ? target.WorkTop + MarginPx : target.WorkTop + 48;

            HudWindowInterop.MoveTo(hwnd, x, y);
            TargetMonitorInfo = isPrimaryFallback ? $"{target} [top-right HUD]" : target.ToString();
        }

        /// <summary>Extended-style summary for logs (e.g. "NOACTIVATE|TOOLWINDOW").</summary>
        public string HudStyleInfo { get; private set; } = "Unapplied";

        private void ApplyHudMode(bool noActivate)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long style = HudWindowInterop.ApplyHudStyle(hwnd, noActivate);
            if (style != 0)
            {
                HudStyleInfo = $"{(noActivate ? "NOACTIVATE|" : string.Empty)}TOOLWINDOW (exstyle=0x{style:X8})";
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            if (msg == WM_DISPLAYCHANGE)
            {
                // Monitor hot-plug / resolution change: re-pin to the G272 once the new layout settles.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(PositionOnTargetMonitor));
            }
            return IntPtr.Zero;
        }

        private void AppendOutput(string message)
        {
            _outputLines.Add(message);
            while (_outputLines.Count > 200)
            {
                _outputLines.RemoveAt(0);
            }
            if (_outputLines.Count > 0)
            {
                VirtualizedOutputList.ScrollIntoView(_outputLines[^1]);
            }
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideCockpit("PanicHide_Esc");
            }
        }

        private void OnCommandInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideCockpit("PanicHide_Esc");
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                string cmd = CommandInputBox.Text.Trim();
                CommandInputBox.Clear();
                if (string.IsNullOrEmpty(cmd)) return;

                AppendOutput($"NEXUS> {cmd}");
                ExecuteCockpitCommand(cmd);
            }
        }

        private void ExecuteCockpitCommand(string cmd)
        {
            if (cmd.Equals("telemetry", StringComparison.OrdinalIgnoreCase))
            {
                RefreshTelemetrySnapshot();
                AppendOutput(LocalPcMcpTools.GetSystemTelemetry());
            }
            else if (cmd.StartsWith("tune", StringComparison.OrdinalIgnoreCase))
            {
                string target = cmd.Length > 4 ? cmd[4..].Trim() : "devenv";
                string res = LocalPcMcpTools.OptimizePcForGamingOrCoding(target);
                _memoryStore.WriteAuditLog("optimize_pc_for_gaming_or_coding", ActionRiskLevel.Yellow_ReversibleTune, target, "ExecutedFromCockpit");
                AppendOutput(res);
                RefreshTelemetrySnapshot();
            }
            else if (cmd.Equals("hide", StringComparison.OrdinalIgnoreCase))
            {
                HideCockpit("CommandHide");
            }
            else
            {
                _memoryStore.SaveMemory("cockpit_cmd", "user_input", cmd);
                AppendOutput($"[ORCHESTRATOR] Dispatched '{cmd}' to SQLite WAL & Agent Bus.");
                RefreshTelemetrySnapshot();
            }
        }

        private void OnQuickSwitchChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitializedUi) return;
            if (ReferenceEquals(sender, ToggleGameBoost) && ToggleGameBoost.IsChecked == true)
            {
                // Game process prefix; override with env NEXUS_GAME_PROCESS when the main game changes.
                string game = Environment.GetEnvironmentVariable("NEXUS_GAME_PROCESS") ?? "DeltaForceClient";
                string res = LocalPcMcpTools.OptimizePcForGamingOrCoding(game);
                _memoryStore.WriteAuditLog("quick_switch_game_boost", ActionRiskLevel.Yellow_ReversibleTune,
                    System.Text.Json.JsonSerializer.Serialize(new { target = game }), "Enabled");
                AppendOutput($"[QUICK SWITCH] {res}");
                RefreshTelemetrySnapshot();
            }
        }
    }
}
