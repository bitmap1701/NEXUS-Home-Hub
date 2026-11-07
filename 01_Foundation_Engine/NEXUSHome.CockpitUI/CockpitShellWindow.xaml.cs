using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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

        // TASK-04: in-process dispatch (ADR-05) + Focus Restoration
        private readonly CockpitCommandProcessor _commandProcessor;
        private IntPtr _previousForeground;
        private bool _commandMode;

        // TASK-05 ADR-14: two-way sync with McpTools through handoff/latest_dispatch.json
        private FileSystemWatcher? _latestWatcher;
        private readonly DispatcherTimer _syncDebounce;
        private long _syncSequence;
        private bool _syncRetried;
        private object _lastSyncState = new { sequence = 0L, dispatchId = (long?)null, status = (string?)null };

        private static readonly Brush DispatchedBrush = Frozen("#38BDF8");
        private static readonly Brush PassedBrush = Frozen("#4ADE80");
        private static readonly Brush NeedsWorkBrush = Frozen("#FBBF24");
        private static readonly Brush EscalatedBrush = Frozen("#F87171");
        private static readonly Brush MutedBrush = Frozen("#CBD5E1");
        private static readonly Brush ThinkingBrush = Frozen("#A78BFA");

        public double LastWakeupLatencyMs { get; private set; }

        /// <summary>Human-readable description of the monitor the Cockpit is pinned to (for logs and the MFD).</summary>
        public string TargetMonitorInfo { get; private set; } = "Unresolved";

        public CockpitShellWindow(NexusRuntime runtime)
        {
            _memoryStore = runtime.Store;
            _agentRegistry = runtime.Registry;
            _commandProcessor = new CockpitCommandProcessor(runtime);
            InitializeComponent();
            VirtualizedOutputList.ItemsSource = _outputLines;

            AppendOutput($"[SYSTEM] NEXUS Cockpit initialized (Zero-Blur Vector HUD, RenderMode={System.Windows.Media.RenderOptions.ProcessRenderMode}).");
            AppendOutput("[HOTKEY] AULA HERO 68 HE 65% bindings active (150ms Debounce Gate).");

            _syncDebounce = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(_commandProcessor.Config.Current.WatcherDebounceMs)
            };
            _syncDebounce.Tick += OnSyncDebounceTick;
            StartLatestDispatchWatcher();
            LatestDispatchSnapshot? lastKnown = LatestDispatchFile.TryRead(_commandProcessor.LatestDispatchPath);
            if (lastKnown != null) ApplySnapshot(lastKnown); // show the last Dev-QA state after a restart
            _isInitializedUi = true;
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static Brush StatusBrush(string? status) => status switch
        {
            "PASSED" => PassedBrush,
            "NEEDS_WORK" => NeedsWorkBrush,
            "ESCALATED" => EscalatedBrush,
            "DISPATCHED" => DispatchedBrush,
            _ => MutedBrush
        };

        /// <summary>
        /// ADR-14: watch latest_dispatch.json (written by McpTools or by this process). Writers replace the file via
        /// temp + rename, so Created/Renamed matter as much as Changed. Events arrive on a pool thread; the 150 ms
        /// DispatcherTimer debounces bursts on the UI thread. No polling: zero CPU while idle.
        /// </summary>
        private void StartLatestDispatchWatcher()
        {
            string? dir = System.IO.Path.GetDirectoryName(_commandProcessor.LatestDispatchPath);
            if (dir == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                _latestWatcher = new FileSystemWatcher(dir, LatestDispatchFile.FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                FileSystemEventHandler changed = (_, _) => ScheduleSync();
                _latestWatcher.Changed += changed;
                _latestWatcher.Created += changed;
                _latestWatcher.Renamed += (_, e) =>
                {
                    if (string.Equals(e.Name, LatestDispatchFile.FileName, StringComparison.OrdinalIgnoreCase)) ScheduleSync();
                };
                _latestWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                AppendOutput($"[SYNC] Không theo dõi được latest_dispatch.json: {ex.Message}");
            }
        }

        private void ScheduleSync()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _syncDebounce.Stop();
                _syncDebounce.Start();
            }));
        }

        private void OnSyncDebounceTick(object? sender, EventArgs e)
        {
            _syncDebounce.Stop();
            LatestDispatchSnapshot? snapshot = LatestDispatchFile.TryRead(_commandProcessor.LatestDispatchPath);
            if (snapshot == null)
            {
                // Caught mid-replace (or unreadable): try once more after another debounce window.
                if (!_syncRetried)
                {
                    _syncRetried = true;
                    _syncDebounce.Start();
                }
                return;
            }
            _syncRetried = false;

            double? latencyMs = null;
            if (DateTime.TryParse(snapshot.UpdatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime updated))
            {
                latencyMs = Math.Round((DateTime.UtcNow - updated.ToUniversalTime()).TotalMilliseconds, 1);
            }

            string? previousStatus = CurrentCardStatus;
            long? previousId = CurrentCardDispatchId;
            ApplySnapshot(snapshot);
            _syncSequence++;
            _lastSyncState = new
            {
                sequence = _syncSequence,
                dispatchId = snapshot.DispatchId,
                status = snapshot.Status,
                retryCount = snapshot.RetryCount,
                evidenceNotes = snapshot.EvidenceNotes,
                source = snapshot.Source,
                @event = snapshot.Event,
                aiModel = snapshot.AiModel,
                aiLatencyMs = snapshot.AiLatencyMs,
                aiResponseChars = snapshot.AiResponse.Length,
                updatedAtUtc = snapshot.UpdatedAtUtc,
                receivedAtUtc = DateTime.UtcNow.ToString("o"),
                latencyMs
            };

            bool isNews = previousId != snapshot.DispatchId || previousStatus != snapshot.Status;
            if (isNews && snapshot.Source != "CockpitUI")
            {
                AppendOutput($"[SYNC ← {snapshot.Source}] Dispatch #{snapshot.DispatchId} → " +
                             $"{DispatchVerdictService.StatusLabel(snapshot.Status, snapshot.RetryCount)} ({latencyMs?.ToString("F0") ?? "?"} ms)");
                if (snapshot.Status == "ESCALATED") AppendEscalationAlert(snapshot.DispatchId);
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshRecentDispatches));
        }

        /// <summary>Last state picked up from latest_dispatch.json (pipe SYNCSTATE, Verify-Task05 E4).</summary>
        public string GetSyncStateJson() => System.Text.Json.JsonSerializer.Serialize(_lastSyncState);

        private long? CurrentCardDispatchId { get; set; }

        private string? CurrentCardStatus { get; set; }

        private void AppendEscalationAlert(long dispatchId) =>
            AppendOutput($"⚠ [3-STRIKE ESCALATION] Dispatch #{dispatchId}: QA trượt 3 lần — dừng tự sửa, chờ Nhật Hòa / Architect quyết định (qa_report.md).");

        protected override void OnClosed(EventArgs e)
        {
            _syncDebounce.Stop();
            _latestWatcher?.Dispose();
            _latestWatcher = null;
            base.OnClosed(e);
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
            // Focus Restoration: remember who had focus (game / IDE) before the Cockpit takes it.
            IntPtr foreground = HudWindowInterop.Foreground();
            IntPtr self = new WindowInteropHelper(this).Handle;
            if (foreground != IntPtr.Zero && foreground != self) _previousForeground = foreground;
            _commandMode = true;

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
            _commandMode = false;
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

            // ADR-10: only style flag + focus happen on the hot path; SQLite reads and text updates run afterwards.
            double capturedLatency = LastWakeupLatencyMs;
            Dispatcher.InvokeAsync(() =>
            {
                UpdateStatusBadge();
                RefreshTelemetrySnapshot();
                RefreshRecentDispatches();
                AppendOutput($"[{DateTime.Now:HH:mm:ss.fff}] Wakeup ({reason}) in {capturedLatency:F2} ms.");
            }, DispatcherPriority.Background);

            return LastWakeupLatencyMs;
        }

        public void HideCockpit(string reason = "Hide", bool trimWorkingSet = false)
        {
            bool restore = _commandMode;
            _commandMode = false;
            ApplyHudMode(noActivate: true);
            Visibility = Visibility.Hidden;
            if (restore) HudWindowInterop.RestoreForeground(_previousForeground);
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
            CommandInputBox.Clear(); // TASK-04: hint text replaces the old "@agent " prefill
        }

        /// <summary>
        /// Focus Restoration: back to Passive HUD (WS_EX_NOACTIVATE) and give the keyboard back to the window that
        /// had it before Ctrl+Shift+Space (game / IDE). The Cockpit stays visible on the MSI G272.
        /// </summary>
        public void ReturnToPassive(string reason)
        {
            _commandMode = false;
            ApplyHudMode(noActivate: true);
            bool restored = HudWindowInterop.RestoreForeground(_previousForeground);
            UpdateStatusBadge();
            AppendOutput($"[{DateTime.Now:HH:mm:ss}] {reason}: Passive HUD, focus {(restored ? "trả về cửa sổ trước" : "không trả được (cửa sổ trước đã đóng?)")}.");
        }

        /// <summary>Runs a Cockpit command coming from the pipe ({"cmd":"COCKPIT","text":"..."}); used by Verify-Task04 E6.</summary>
        public string ExecuteFromPipe(string text)
        {
            CockpitCommandResult result = RunCommand(text, HudWindowInterop.Foreground());
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = result.Kind,
                aiPending = result.AiTask != null,
                ok = result.Ok,
                message = result.Message,
                dispatchId = result.Dispatch?.DispatchId ?? result.Verdict?.Dispatch?.Id,
                squadId = result.Dispatch?.SquadId ?? result.Verdict?.Dispatch?.SquadId,
                status = result.Snapshot?.Status,
                retryCount = result.Snapshot?.RetryCount,
                escalatedNow = result.Verdict?.EscalatedNow,
                elapsedMs = result.ElapsedMs
            });
        }

        private CockpitCommandResult RunCommand(string text, IntPtr foregroundForBoost)
        {
            // TASK-07 Zero-Secret-Leak: the echo never shows "/apikey set <key>" or anything that looks like a key.
            AppendOutput($"NEXUS> {CockpitCommandProcessor.RedactForDisplay(text)}");
            CockpitCommandResult result = _commandProcessor.Execute(text, foregroundForBoost);
            AppendOutput(result.Message);

            if (result.Snapshot != null) ApplySnapshot(result.Snapshot);
            if (result.Verdict?.EscalatedNow == true && result.Verdict.Dispatch != null) AppendEscalationAlert(result.Verdict.Dispatch.Id);
            if (result.Kind == "hide") HideCockpit("CommandHide");
            if (result.Kind == "reload") ApplyConfigToUi();
            if (result.AiTask != null) _ = AwaitAiAsync(result.AiTask);

            // SQLite reads after the caller has given focus back (TASK-07: /ask returns to the game at once).
            UpdateStatusBadge();
            Dispatcher.InvokeAsync(() =>
            {
                RefreshRecentDispatches();
                RefreshTelemetrySnapshot();
            }, DispatcherPriority.Background);
            return result;
        }

        /// <summary>TASK-07: Gemini finished in the background; this continuation runs back on the UI thread.</summary>
        private async System.Threading.Tasks.Task AwaitAiAsync(System.Threading.Tasks.Task<ConsultOutcome> aiTask)
        {
            try
            {
                ConsultOutcome outcome = await aiTask;
                if (outcome.Snapshot != null) ApplySnapshot(outcome.Snapshot);
                AppendOutput($"[AI #{outcome.DispatchId}] {(outcome.AiOk ? "OK" : "KHÔNG CÓ CÂU TRẢ LỜI")} | " +
                             $"{(outcome.AiModel.Length > 0 ? outcome.AiModel : "-")} | {outcome.AiLatencyMs} ms | key: {outcome.KeySource}" +
                             (outcome.FileError != null ? $" | {outcome.FileError}" : string.Empty));
                RefreshRecentDispatches();
            }
            catch (Exception ex)
            {
                AppendOutput($"[AI] Lỗi nền: {ex.GetType().Name}");
            }
        }

        /// <summary>Active Squad card from a latest_dispatch.json snapshot (own command or McpTools via the watcher).</summary>
        private void ApplySnapshot(LatestDispatchSnapshot snapshot)
        {
            Brush brush = StatusBrush(snapshot.Status);
            DispatchCardBorder.BorderBrush = brush;
            DispatchCardTitle.Foreground = brush;
            DispatchCardTitle.Text = $"ACTIVE SQUAD // {snapshot.SquadId}: {snapshot.SquadName ?? "-"}";
            DispatchCardAgents.Text = $"Primary: {snapshot.PrimaryAgent.Name}   QA: {snapshot.QaAgent.Name}   Gate: Reality Checker";
            DispatchCardMeta.Text =
                $"Dispatch ID: #{snapshot.DispatchId}   " +
                (snapshot.ElapsedMs is double ms ? $"Latency: {ms:F2} ms   " : string.Empty) +
                $"Mode: {snapshot.NexusMode}   Nguồn: {snapshot.Source}";
            DispatchCardTask.Text = snapshot.TaskSummary.Length > 0 ? $"Task: {snapshot.TaskSummary}" : string.Empty;
            bool thinking = snapshot.Event == "ask" && snapshot.AiResponse.Length == 0;
            DispatchCardStatus.Foreground = thinking ? ThinkingBrush : brush;
            DispatchCardStatus.Text = thinking
                ? "[THINKING... Calling Gemini]"
                : $"[{DispatchVerdictService.StatusLabel(snapshot.Status, snapshot.RetryCount)}]";

            // TASK-07 AI Response box: shown while thinking and once an answer (or diagnostic) is stored.
            bool hasAnswer = snapshot.AiResponse.Length > 0;
            AiResponseHeader.Visibility = thinking || hasAnswer ? Visibility.Visible : Visibility.Collapsed;
            AiResponseHeader.Text = hasAnswer
                ? $"AI RESPONSE // {(snapshot.AiModel.Length > 0 ? snapshot.AiModel : "local")} · {snapshot.AiLatencyMs} ms"
                : "AI RESPONSE // đang gọi Gemini...";
            AiResponseText.Text = snapshot.AiResponse;
            AiResponseScroll.Visibility = hasAnswer ? Visibility.Visible : Visibility.Collapsed;
            if (hasAnswer) AiResponseScroll.ScrollToTop();
            DispatchCardEvidence.Text = snapshot.EvidenceNotes.Length > 0 ? $"Evidence: {snapshot.EvidenceNotes}" : string.Empty;
            DispatchCardEvidence.Visibility = snapshot.EvidenceNotes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            CurrentCardDispatchId = snapshot.DispatchId;
            CurrentCardStatus = snapshot.Status;
        }

        private void RefreshRecentDispatches()
        {
            try
            {
                var rows = _memoryStore.GetRecentDispatches(5);
                RecentDispatchesText.Inlines.Clear();
                if (rows.Count == 0)
                {
                    RecentDispatchesText.Inlines.Add(new Run("(trống)"));
                    return;
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    RecentDispatch r = rows[i];
                    if (i > 0) RecentDispatchesText.Inlines.Add(new LineBreak());
                    RecentDispatchesText.Inlines.Add(new Run($"#{r.Id} {r.SquadId} "));
                    RecentDispatchesText.Inlines.Add(new Run($"[{DispatchVerdictService.StatusLabel(r.Status, r.RetryCount)}]")
                    {
                        Foreground = StatusBrush(r.Status)
                    });
                }
            }
            catch (Exception ex)
            {
                RecentDispatchesText.Inlines.Clear();
                RecentDispatchesText.Inlines.Add(new Run($"(lỗi SQLite: {ex.Message})"));
            }
        }

        private void UpdateStatusBadge()
        {
            using var proc = Process.GetCurrentProcess();
            LatencyBadgeText.Text =
                $"MODE: {_commandProcessor.DefaultMode} | HUD: {(_commandMode ? "COMMAND" : "PASSIVE")} | " +
                $"RAM: {proc.WorkingSet64 / (1024.0 * 1024.0):F0} MB | WAKE: {LastWakeupLatencyMs:F2}ms | " +
                $"RENDER: {System.Windows.Media.RenderOptions.ProcessRenderMode}";
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
        /// TASK-06 /reload: settings the window owns. Debounce applies to the next file change; the monitor is
        /// re-resolved from preferredMonitorKeywords. Hotkeys need a HostService restart (they are registered once).
        /// </summary>
        private void ApplyConfigToUi()
        {
            NexusConfig config = _commandProcessor.Config.Current;
            _syncDebounce.Interval = TimeSpan.FromMilliseconds(config.WatcherDebounceMs);
            string before = TargetMonitorInfo;
            PositionOnTargetMonitor();
            if (TargetMonitorInfo != before) AppendOutput($"[CONFIG] Cockpit chuyển sang màn hình: {TargetMonitorInfo}");
            UpdateStatusBadge();
        }

        /// <summary>
        /// 3-tier monitor routing (Gate 2 decision 3): EDID friendly name containing every keyword of
        /// nexus_config.json "preferredMonitorKeywords" (env NEXUS_COCKPIT_MONITOR still overrides, as before)
        /// -> first non-primary monitor -> primary monitor, docked top-right as a HUD.
        /// Moves in physical pixels (SetWindowPos), which is correct under PerMonitorV2 DPI awareness.
        /// </summary>
        private void PositionOnTargetMonitor()
        {
            string? envHint = Environment.GetEnvironmentVariable("NEXUS_COCKPIT_MONITOR");
            IReadOnlyList<string> keywords = string.IsNullOrWhiteSpace(envHint)
                ? _commandProcessor.Config.Current.PreferredMonitorKeywords
                : new[] { envHint };
            TargetMonitor? target = MonitorLocator.Find(keywords);
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
                ReturnToPassive("Esc");
            }
        }

        private void OnCommandTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            CommandHintText.Visibility = CommandInputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnCommandInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ReturnToPassive("Esc");
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                string cmd = CommandInputBox.Text.Trim();
                CommandInputBox.Clear();
                if (string.IsNullOrEmpty(cmd)) return;

                // /boost from command mode shields the window that had focus before (game / IDE), not the Cockpit.
                CockpitCommandResult result = RunCommand(cmd, _previousForeground);
                if (result.Kind != "hide") ReturnToPassive("Enter");
            }
        }

        private void OnQuickSwitchChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitializedUi) return;
            if (ReferenceEquals(sender, ToggleGameBoost))
            {
                // In HUD mode (WS_EX_NOACTIVATE) the foreground window is still the game, so it is shielded.
                // Unticking restores the processes lowered by Background Yield.
                RunCommand(ToggleGameBoost.IsChecked == true ? "/boost" : "/boost off", HudWindowInterop.Foreground());
            }
        }
    }
}
