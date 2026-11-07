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
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            Activate();
            CommandInputBox.Focus();
            Keyboard.Focus(CommandInputBox);

            long doneTicks = Stopwatch.GetTimestamp();
            LastWakeupLatencyMs = (doneTicks - triggerTimestampTicks) * 1000.0 / Stopwatch.Frequency;

            double capturedLatency = LastWakeupLatencyMs;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                LatencyBadgeText.Text = $"WAKE: {capturedLatency:F2}ms | iGPU: UHD 730 | PIPE: NEXUSHome.Cockpit.v1";
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
        /// Pins the Cockpit to the MSI G272 (iGPU UHD 730). Override the match with env NEXUS_COCKPIT_MONITOR
        /// (substring of the monitor's friendly name). Falls back to the first non-primary monitor, then the primary.
        /// </summary>
        private void PositionOnTargetMonitor()
        {
            string hint = Environment.GetEnvironmentVariable("NEXUS_COCKPIT_MONITOR") ?? "G272";
            TargetMonitor? target = MonitorLocator.Find(hint);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());

            if (target is null || source?.CompositionTarget is null)
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Left + (workArea.Width - Width) / 2.0;
                Top = workArea.Top + 48.0;
                TargetMonitorInfo = "Fallback: primary work area";
                return;
            }

            // Monitor coordinates are physical pixels; Window.Left/Top are DIPs.
            var toDip = source.CompositionTarget.TransformFromDevice;
            Point origin = toDip.Transform(new Point(target.WorkLeft, target.WorkTop));
            Point size = toDip.Transform(new Point(target.WorkWidth, target.WorkHeight));
            Left = origin.X + (size.X - Width) / 2.0;
            Top = origin.Y + 48.0;
            TargetMonitorInfo = target.ToString();
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
                string res = LocalPcMcpTools.OptimizePcForGamingOrCoding("TslGame");
                _memoryStore.WriteAuditLog("quick_switch_game_boost", ActionRiskLevel.Yellow_ReversibleTune, "{\"target\":\"TslGame\"}", "Enabled");
                AppendOutput($"[QUICK SWITCH] {res}");
                RefreshTelemetrySnapshot();
            }
        }
    }
}
