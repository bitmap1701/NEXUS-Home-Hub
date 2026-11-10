using System;
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
        private readonly List<string> _outputLines = new(); // TASK-16 B3: system log kept for the status-line tooltip (200 lines max)
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

        // TASK-14 ADR-59: /timer expiry -> Passive HUD (WS_EX_NOACTIVATE); evidence for Gate 14 E3 (pipe TIMERALERT).
        private readonly LocalUtilityService _localUtilities;

        // TASK-13a: one-shot idle memory trim (MemorySlimming), re-armed by activity; null when NEXUS_MEMORY_SLIMMING=0.
        private readonly DispatcherTimer? _idleTrimTimer;
        private readonly VaultGroundingEngine _grounding;

        // TASK-13: Right MFD pin board (5 fixed TextBlocks, no per-pin controls) synced from mfd_pins.json.
        private readonly MfdPinManager _pins;
        private readonly HudWidgetService _hudWidgets;
        private readonly VisionConsultService _vision; // TASK-12b: VISIONSTATE burst telemetry
        private readonly DispatcherTimer _pinsDebounce;
        private FileSystemWatcher? _pinsWatcher;
        private object _lastPinSync = new { count = 0L };
        private long _pinSyncCount;
        private object _lastVisionSync = new { count = 0L }; // TASK-12: last vision snapshot shown (pipe VISIONSTATE)
        private long _visionSyncCount;
        private string _pinsSignature = string.Empty;
        private readonly string _runtimeSource;
        private string? _lastLocalUpdatedAt;
        private long _timerAlertCount;
        private object _lastTimerAlert = new { count = 0L };

        // TASK-16 (ADR-65) Cockpit UI v2: 3 snap heights, click-through, auto-collapse, G272 anchoring, YouTube EQ switch
        private readonly NexusRuntime _runtime;
        private CockpitUiState _ui;
        private string _effectiveLevel;
        private string? _tempReason;             // null = the saved level; "auto" / "youtube" = temporary collapse; "command" = temporary Half
        private readonly DispatcherTimer _autoCollapseTimer;
        private readonly int _autoCollapseDelayMs;
        private long _autoCollapseCount;
        private bool _clickThroughApplied;
        private TargetMonitor? _target;
        private double _dragStartHeight;
        private object? _lastLevelChange;
        private long _hiddenTestSnapshots;
        private string? _lastHiddenTest;
        private WinEventSubscription? _winEvents;
        private DispatcherTimer? _youtubeDebounce;
        private string _youtubeTrigger = "start-up";
        private object? _lastYoutube;
        private string _shownMemoryLabel = string.Empty;
        private int _lastCardCount;
        private object[] _recentShown = Array.Empty<object>();
        private int _recentFilteredOut;

        private static readonly Brush DispatchedBrush = Frozen("#38BDF8");
        private static readonly Brush PassedBrush = Frozen("#4ADE80");
        private static readonly Brush NeedsWorkBrush = Frozen("#FBBF24");
        private static readonly Brush EscalatedBrush = Frozen("#F87171");
        private static readonly Brush MutedBrush = Frozen("#CBD5E1");
        private static readonly Brush ThinkingBrush = Frozen("#A78BFA");
        private static readonly Brush QaBrush = Frozen("#F472B6");
        private static readonly Brush LocalBrush = Frozen("#2DD4BF");

        public double LastWakeupLatencyMs { get; private set; }

        /// <summary>Human-readable description of the monitor the Cockpit is pinned to (for logs and the MFD).</summary>
        public string TargetMonitorInfo { get; private set; } = "Unresolved";

        public CockpitShellWindow(NexusRuntime runtime)
        {
            _runtime = runtime;
            _ui = runtime.UiLayout.Load();
            _effectiveLevel = CockpitLayout.Normalize(_ui.HeightLevel);
            _memoryStore = runtime.Store;
            _agentRegistry = runtime.Registry;
            _commandProcessor = new CockpitCommandProcessor(runtime);
            _localUtilities = runtime.LocalUtilities;
            _runtimeSource = runtime.Source;
            _grounding = runtime.Grounding;
            _pins = runtime.Pins;
            _hudWidgets = runtime.HudWidgets;
            _vision = runtime.Vision;
            InitializeComponent();
            CheatSheetText.Text = CheatSheet;
            ToggleAutoCollapse.IsChecked = _ui.AutoCollapse;
            ToggleClickThrough.IsChecked = _ui.ClickThrough;
            _autoCollapseDelayMs = CockpitLayout.ResolveAutoCollapseDelayMs();
            _autoCollapseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(_autoCollapseDelayMs)
            };
            _autoCollapseTimer.Tick += OnAutoCollapseTick;

            AppendOutput($"[SYSTEM] NEXUS Cockpit initialized (Zero-Blur Vector HUD, RenderMode={System.Windows.Media.RenderOptions.ProcessRenderMode}).");
            AppendOutput("[HOTKEY] AULA HERO 68 HE 65% bindings active (150ms Debounce Gate).");

            _syncDebounce = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(_commandProcessor.Config.Current.WatcherDebounceMs)
            };
            _syncDebounce.Tick += OnSyncDebounceTick;
            StartLatestDispatchWatcher();
            _localUtilities.TimerExpired += OnLocalTimerExpired;
            _pinsDebounce = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(_commandProcessor.Config.Current.WatcherDebounceMs)
            };
            _pinsDebounce.Tick += OnPinsDebounceTick;
            StartPinsWatcher();
            RefreshRightMfd("startup");
            if (MemorySlimming.Enabled)
            {
                _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(MemorySlimming.IdleTrimDelayMs)
                };
                _idleTrimTimer.Tick += OnIdleTrimTick;
            }
            LatestDispatchSnapshot? lastKnown = LatestDispatchFile.TryRead(_commandProcessor.LatestDispatchPath);
            if (lastKnown != null) ApplySnapshot(lastKnown); // show the last Dev-QA state after a restart (B1: never a test record)
            ApplyLevel(_effectiveLevel, persist: false, "start-up", null);
            _runtime.YouTube.Changed += OnYouTubeChanged;
            _isInitializedUi = true;
            ArmIdleTrim(); // a freshly started host trims once after start-up too
        }

        /// <summary>TASK-13: watch config/mfd_pins.json so a pin made by McpTools shows on the Right MFD (&lt; 300 ms).</summary>
        private void StartPinsWatcher()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_pins.PinsPath);
                if (directory == null) return;
                Directory.CreateDirectory(directory);
                _pinsWatcher = new FileSystemWatcher(directory, MfdPinManager.FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(new Action(() => { _pinsDebounce.Stop(); _pinsDebounce.Start(); }));
                _pinsWatcher.Changed += changed;
                _pinsWatcher.Created += changed;
                _pinsWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(new Action(() => { _pinsDebounce.Stop(); _pinsDebounce.Start(); }));
            }
            catch (Exception ex)
            {
                AppendOutput($"[PINS] Không theo dõi được mfd_pins.json: {ex.Message}");
            }
        }

        private void OnPinsDebounceTick(object? sender, EventArgs e)
        {
            _pinsDebounce.Stop();
            RefreshRightMfd("watcher");
        }

        /// <summary>
        /// TASK-13: redraw the Right MFD from mfd_pins.json + the current response cards + active timers. Only the text of
        /// the 5 fixed slots changes (no element is created), so the WPF tree does not grow with pins.
        /// </summary>
        private void RefreshRightMfd(string reason)
        {
            long ticks = Stopwatch.GetTimestamp();
            IntPtr before = HudWindowInterop.Foreground();
            IReadOnlyList<MfdPin> pins = _pins.List();
            int max = _pins.MaxPins;
            MfdPinsHeaderText.Text = $"PINNED MFD CARDS ({pins.Count}/{max})";
            System.Windows.Controls.TextBlock[] slots = { MfdPin1Text, MfdPin2Text, MfdPin3Text, MfdPin4Text, MfdPin5Text };
            for (int i = 0; i < slots.Length; i++)
            {
                if (i < pins.Count)
                {
                    MfdPin p = pins[i];
                    // TASK-12 §3.1: no second copy of the title line; the preview starts where the title stops.
                    string preview = WidgetCardExtractor.PreviewAfterTitle(p.Title, p.Content);
                    slots[i].Text = $"#{p.PinId} [{p.Type}{(p.Language != null ? ":" + p.Language : string.Empty)}] {p.Title}" +
                                    (preview.Length > 0 ? "\n" + preview : string.Empty);
                    slots[i].Visibility = Visibility.Visible;
                }
                else
                {
                    slots[i].Text = string.Empty;
                    slots[i].Visibility = i < max && i == pins.Count ? Visibility.Visible : Visibility.Collapsed;
                    if (i == pins.Count) slots[i].Text = "(ô trống: Alt+P hoặc /pin để ghim)";
                }
            }
            SetCardLabel(_hudWidgets.Cards);
            var (activeTimers, _) = _localUtilities.Timers.List();
            MfdTimerText.Text = activeTimers.Count == 0
                ? string.Empty
                : "⏱ " + string.Join(" | ", activeTimers.Take(2).Select(t => $"#{t.Id} {t.Label} còn {LocalTimerManager.FormatDuration(Math.Max(1000, (t.RemainingMs + 999) / 1000 * 1000))}"));

            string signature = string.Join("|", pins.Select(p => p.PinnedAtUtc + p.Content.Length));
            if (signature == _pinsSignature) return;
            _pinsSignature = signature;
            IntPtr after = HudWindowInterop.Foreground();
            double? latencyMs = null;
            MfdPin? newest = pins.OrderByDescending(p => p.PinnedAtUtc, StringComparer.Ordinal).FirstOrDefault();
            if (newest != null && DateTime.TryParse(newest.PinnedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime pinnedAt))
            {
                latencyMs = Math.Round((DateTime.UtcNow - pinnedAt.ToUniversalTime()).TotalMilliseconds, 1);
            }
            _lastPinSync = new
            {
                count = ++_pinSyncCount,
                reason,
                pinCount = pins.Count,
                maxPins = max,
                newestTitle = newest?.Title,
                newestSource = newest?.Source,
                newestPinnedAtUtc = newest?.PinnedAtUtc,
                latencyMs,
                refreshMs = Math.Round((Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency, 3),
                foregroundBefore = $"0x{before.ToInt64():X}",
                foregroundAfter = $"0x{after.ToInt64():X}",
                foregroundUnchanged = before == after,
                visible = Visibility == Visibility.Visible,
                slots = slots.Select(s => s.Text).ToArray(),
                atUtc = DateTime.UtcNow.ToString("o")
            };
        }

        /// <summary>TASK-13 pipe PINSTATE: what the Right MFD shows now + the last pin sync (Verify-Task13 E3/E4).</summary>
        public string GetPinStateJson()
        {
            RefreshRightMfd("pinstate");
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                pins = _pins.List(),
                maxPins = _pins.MaxPins,
                header = MfdPinsHeaderText.Text,
                indicator = WidgetIndicatorText.Text,
                indicatorDetail = WidgetIndicatorText.ToolTip as string,
                slots = new[] { MfdPin1Text.Text, MfdPin2Text.Text, MfdPin3Text.Text, MfdPin4Text.Text, MfdPin5Text.Text },
                lastSync = _lastPinSync
            });
        }

        /// <summary>TASK-13 pipe COPYWIDGET [n]: /copy n on the UI thread, plus what the system clipboard now holds.</summary>
        public string CopyWidgetFromPipe(string? index)
        {
            CockpitCommandResult result = RunCommand(string.IsNullOrWhiteSpace(index) ? "/copy" : $"/copy {index.Trim()}", HudWindowInterop.Foreground());
            string? clipboard = ClipboardService.TryReadSystemText();
            ClipboardHistoryFlags? history = ClipboardService.TryReadHistoryFlags(); // TASK-12 §3.1 evidence
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                message = result.Message,
                elapsedMs = result.ElapsedMs,
                copyMs = result.Copy?.ElapsedMs,
                systemClipboard = result.Copy?.SystemClipboard,
                systemClipboardError = result.Copy?.SystemClipboardError,
                historyExcludedOnWrite = result.Copy?.HistoryExcluded,
                historyFlags = history,
                historyExcluded = history?.HistoryExcluded,
                copiedText = result.Copy?.Text,
                lastCopiedText = ClipboardService.LastCopiedText,
                clipboardText = clipboard,
                clipboardMatches = result.Copy != null && clipboard == result.Copy.Text
            });
        }

        /// <summary>TASK-13a: (re)start the one-shot idle trim. No periodic timer: an idle Cockpit wakes up once, then sleeps.</summary>
        private void ArmIdleTrim()
        {
            if (_idleTrimTimer == null) return;
            _idleTrimTimer.Stop();
            _idleTrimTimer.Start();
        }

        private void OnIdleTrimTick(object? sender, EventArgs e)
        {
            _idleTrimTimer?.Stop();
            if (_commandMode) return; // typing: ReturnToPassive re-arms
            MemoryTrimResult trim = MemorySlimming.Trim("idle");
            UpdateStatusBadge();
            if (Visibility == Visibility.Visible) RefreshTelemetrySnapshot();
            AppendOutput($"[MEM] Dọn khi rảnh: Private WS {trim.PrivateWorkingSetBeforeMb?.ToString("F1") ?? "?"} → {trim.PrivateWorkingSetAfterMb?.ToString("F1") ?? "?"} MB ({trim.DurationMs:F0} ms).");
        }

        /// <summary>TASK-13a pipe MEM: breakdown + the exact memory text the HUD shows (Verify-Task13a E2/E3).</summary>
        public string GetMemoryJson()
        {
            MemorySnapshot snapshot = MemoryDiagnostics.Capture(_agentRegistry, _grounding);
            UpdateStatusBadge();
            long outputChars = 0;
            foreach (string line in _outputLines) outputChars += line.Length;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                snapshot,
                hudBadge = LatencyBadgeText.Text,
                hudLabel = snapshot.HudLabel,
                lastTrim = MemorySlimming.LastTrim,
                trimCount = MemorySlimming.TrimCount,
                slimmingEnabled = MemorySlimming.Enabled,
                idleTrimArmed = _idleTrimTimer?.IsEnabled ?? false,
                idleTrimDelayMs = MemorySlimming.IdleTrimDelayMs,
                outputLines = _outputLines.Count,
                outputChars,
                commandMode = _commandMode,
                visible = Visibility == Visibility.Visible
            });
        }

        // ======================================================================== TASK-16 (ADR-65) Cockpit UI v2

        private const string CheatSheet =
            "Ctrl+Shift+Space   Vào ô lệnh NEXUS> (tắt xuyên thấu, rõ 100 %)\n" +
            "Alt+Space          Hiện / ẩn HUD thụ động (game giữ focus)\n" +
            "Enter / Esc        Gửi lệnh / quay về game\n" +
            "Alt+Q              Đổi trang LEFT MFD\n" +
            "Alt+C / Alt+P      Copy thẻ #1 / Ghim kết quả (khi đang gõ lệnh)\n" +
            "Alt+Backspace      Ẩn khẩn cấp\n" +
            "F1                 Bật / tắt bảng này (chỉ khi Cockpit đang focus)\n" +
            "Kéo viền dưới ↕    Thả ra là bám 40 / 330 / 640 px\n" +
            "Nhấp đúp tiêu đề   Thu gọn 40 px ↔ Nửa 330 px (kéo tiêu đề: di chuyển)\n" +
            "/ui half|full|thu  Đổi độ cao  ·  /ui xuyen on|off  ·  /ui tuthu on|off\n" +
            "/watch test        Thử rung đồng hồ (webhook)  ·  /watch: trạng thái\n" +
            "/ask /g /calc /timer /note /copy /pin /img /snap /mem /usage /status";

        /// <summary>
        /// TASK-16 U1: sets the height to one of the 3 snap levels (no animation: one Height assignment). persist = the user
        /// chose it (double-click, grip, /ui), so it is saved; temp marks an automatic change that a later event undoes.
        /// </summary>
        private void ApplyLevel(string level, bool persist, string reason, string? temp)
        {
            level = CockpitLayout.Normalize(level);
            _effectiveLevel = level;
            _tempReason = temp;
            if (persist && _ui.HeightLevel != level) SaveUi(_ui with { HeightLevel = level });

            bool collapsed = level == CockpitLayout.Collapsed;
            Visibility panes = collapsed ? Visibility.Collapsed : Visibility.Visible;
            MainPanes.Visibility = panes;
            StatusLineText.Visibility = panes;
            HintBarText.Visibility = panes;
            TopBar.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 0, 6);
            TopBar.BorderThickness = collapsed ? new Thickness(0) : new Thickness(0, 0, 0, 1);
            if (collapsed) CheatSheetPanel.Visibility = Visibility.Collapsed;
            double max = _target != null ? Math.Max(CockpitLayout.CollapsedHeight, (_target.WorkHeight - 16) / DpiScale()) : double.MaxValue;
            Height = Math.Min(CockpitLayout.HeightOf(level), max);
            ClampIntoTarget();
            UpdateUiBadge();
            _lastLevelChange = new { level, reason, persist, temp, height = Height, atUtc = DateTime.UtcNow.ToString("o") };
            ArmAutoCollapse();
        }

        /// <summary>User toggle (double-click on the title bar or the grip): Collapsed ↔ Half, saved.</summary>
        private void ToggleLevelByUser(string reason) =>
            ApplyLevel(CockpitLayout.ToggleCollapsed(_effectiveLevel), persist: true, reason, null);

        private void SaveUi(CockpitUiState state)
        {
            _ui = state;
            string? error = _runtime.UiLayout.Save(state);
            if (error != null) AppendOutput($"[UI] Không lưu được {CockpitUiStore.FileName}: {error}");
        }

        /// <summary>One-shot idle timer (no periodic tick): armed only while PASSIVE, visible and not already at 40 px.</summary>
        private void ArmAutoCollapse()
        {
            _autoCollapseTimer.Stop();
            if (_commandMode || Visibility != Visibility.Visible || _effectiveLevel == CockpitLayout.Collapsed) return;
            bool savedCollapsed = CockpitLayout.Normalize(_ui.HeightLevel) == CockpitLayout.Collapsed;
            if (_ui.AutoCollapse || savedCollapsed) _autoCollapseTimer.Start();
        }

        private void OnAutoCollapseTick(object? sender, EventArgs e)
        {
            _autoCollapseTimer.Stop();
            if (_commandMode || Visibility != Visibility.Visible || _effectiveLevel == CockpitLayout.Collapsed) return;
            bool savedCollapsed = CockpitLayout.Normalize(_ui.HeightLevel) == CockpitLayout.Collapsed;
            if (!_ui.AutoCollapse && !savedCollapsed) return;
            _autoCollapseCount++;
            ApplyLevel(CockpitLayout.Collapsed, persist: false, "auto-idle", savedCollapsed ? null : "auto");
        }

        /// <summary>Mouse over the HUD / wheel scrolling an answer counts as activity (the user is reading).</summary>
        private void OnHudActivity(object sender, MouseEventArgs e)
        {
            if (_autoCollapseTimer.IsEnabled) ArmAutoCollapse();
        }

        /// <summary>
        /// TASK-16 U1 click-through: preference ON + PASSIVE → opacity 0.6 + WS_EX_TRANSPARENT | WS_EX_LAYERED, so clicks and
        /// the wheel reach the window behind (YouTube on the G272). Command mode (Ctrl+Shift+Space) is always clickable, 100 %.
        /// </summary>
        private void ApplyClickThroughState()
        {
            bool want = _ui.ClickThrough && !_commandMode;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) HudWindowInterop.SetClickThrough(hwnd, want);
            _clickThroughApplied = want;
            Opacity = want ? CockpitLayout.ClickThroughOpacity : 1.0;
            UpdateUiBadge();
        }

        private void UpdateUiBadge()
        {
            string level = _effectiveLevel switch
            {
                CockpitLayout.Collapsed => "THU GỌN 40",
                CockpitLayout.Full => "ĐẦY 640",
                _ => "NỬA 330"
            };
            UiStateBadgeText.Text = $"[{level}" +
                                    (_clickThroughApplied ? " · XUYÊN 60%" : string.Empty) +
                                    (_runtime.YouTube.Watching ? " · ▶ YouTube" : string.Empty) + "]";
        }

        /// <summary>TASK-16 B5: "Câu trả lời hiện tại: X thẻ"; the old detailed indicator (Alt+C / Alt+P hints) is the tooltip.</summary>
        private void SetCardLabel(IReadOnlyList<HudWidgetCard> cards)
        {
            _lastCardCount = cards.Count;
            WidgetIndicatorText.Text = WidgetCardExtractor.CardCountLabel(cards);
            WidgetIndicatorText.ToolTip = WidgetCardExtractor.Indicator(cards);
        }

        private void ToggleCheatSheet(string reason)
        {
            bool show = CheatSheetPanel.Visibility != Visibility.Visible;
            if (show && _effectiveLevel == CockpitLayout.Collapsed) ApplyLevel(CockpitLayout.Half, persist: false, reason, "command");
            CheatSheetPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTopBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                ToggleLevelByUser("double-click title");
                return;
            }
            try
            {
                DragMove(); // returns when the button is released
            }
            catch (InvalidOperationException)
            {
                return;
            }
            ClampIntoTarget();
            if (HudWindowInterop.TryGetWindowRect(new WindowInteropHelper(this).Handle, out int x, out int y, out _, out _))
                SaveUi(_ui with { LeftPx = x, TopPx = y });
        }

        private void OnGripPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            e.Handled = true; // no drag on a double-click
            ToggleLevelByUser("double-click grip");
        }

        private void OnGripDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _dragStartHeight = Height;
            _autoCollapseTimer.Stop();
        }

        private void OnGripDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double max = _target != null ? Math.Max(CockpitLayout.CollapsedHeight, (_target.WorkHeight - 16) / DpiScale()) : CockpitLayout.FullHeight;
            Height = Math.Clamp(Height + e.VerticalChange, CockpitLayout.CollapsedHeight, Math.Min(max, CockpitLayout.FullHeight + 40));
            bool showPanes = Height > CockpitLayout.CollapsedHeight + 30;
            MainPanes.Visibility = showPanes ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Release = snap to the nearest of 40 / 330 / 640 px in one step (no Storyboard, no animation).</summary>
        private void OnGripDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            string level = CockpitLayout.Snap(Height);
            ApplyLevel(level, persist: true, $"grip {Math.Round(_dragStartHeight)}→{Math.Round(Height)} px", null);
        }

        private void OnUiOptionChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitializedUi) return;
            if (ReferenceEquals(sender, ToggleAutoCollapse)) ApplyUiAction(ToggleAutoCollapse.IsChecked == true ? "autocollapse:on" : "autocollapse:off", "checkbox");
            else if (ReferenceEquals(sender, ToggleClickThrough)) ApplyUiAction(ToggleClickThrough.IsChecked == true ? "clickthrough:on" : "clickthrough:off", "checkbox");
        }

        /// <summary>Applies one normalised action from /ui, the pipe UI command or a checkbox (see CockpitLayout.ParseUiAction).</summary>
        private string ApplyUiAction(string action, string source)
        {
            switch (action)
            {
                case "toggle":
                    ToggleLevelByUser($"{source} toggle");
                    break;
                case "keys":
                    ToggleCheatSheet(source);
                    break;
                case "status":
                    break;
                case "autocollapse:on":
                case "autocollapse:off":
                    SaveUi(_ui with { AutoCollapse = action.EndsWith(":on", StringComparison.Ordinal) });
                    if (ToggleAutoCollapse.IsChecked != _ui.AutoCollapse) ToggleAutoCollapse.IsChecked = _ui.AutoCollapse;
                    ArmAutoCollapse();
                    break;
                case "clickthrough:on":
                case "clickthrough:off":
                    SaveUi(_ui with { ClickThrough = action.EndsWith(":on", StringComparison.Ordinal) });
                    if (ToggleClickThrough.IsChecked != _ui.ClickThrough) ToggleClickThrough.IsChecked = _ui.ClickThrough;
                    ApplyClickThroughState();
                    break;
                default:
                    if (action.StartsWith("level:", StringComparison.Ordinal))
                    {
                        ApplyLevel(action.Substring("level:".Length), persist: true, source, null);
                        break;
                    }
                    return $"unknown action {action}";
            }
            if (source != "checkbox" || action.StartsWith("clickthrough", StringComparison.Ordinal) || action.StartsWith("autocollapse", StringComparison.Ordinal))
                AppendOutput($"[UI] {action} ({source}) → {UiStateBadgeText.Text}");
            return "ok";
        }

        /// <summary>TASK-16 pipe "UI &lt;action&gt;" (Verify-Task16 E2/E3): same path as /ui, then the state.</summary>
        public string ApplyUiFromPipe(string? argument)
        {
            string? action = CockpitLayout.ParseUiAction(argument);
            string outcome = action == null ? $"unknown argument '{argument}'" : ApplyUiAction(action, "pipe");
            UpdateLayout();
            return GetUiStateJson(action, outcome);
        }

        /// <summary>TASK-16 U2: subscribe to window events when the auto-switch is active (no thread, no timer while idle).</summary>
        private void StartYouTubeWatch()
        {
            bool active = _runtime.YouTube.Configure();
            if (!active)
            {
                _winEvents?.Dispose();
                _winEvents = null;
                AppendOutput($"[U2] Tự đổi EQ K450 khi xem YouTube trên G272: tắt ({_runtime.YouTube.InactiveReason}).");
                return;
            }
            if (_youtubeDebounce == null)
            {
                _youtubeDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
                _youtubeDebounce.Tick += (_, _) =>
                {
                    _youtubeDebounce!.Stop();
                    _runtime.YouTube.CheckNow(_youtubeTrigger);
                };
            }
            DispatcherTimer debounce = _youtubeDebounce;
            debounce.Interval = TimeSpan.FromMilliseconds(_runtime.YouTube.DebounceMs);
            if (_winEvents == null && YouTubeG272Watcher.SimulatedTitle == null)
            {
                _winEvents = new WinEventSubscription(evt =>
                {
                    // Runs on this UI thread (out-of-context hook): only restart the one-shot debounce.
                    _youtubeTrigger = evt switch
                    {
                        0x0003 => "foreground",
                        0x000B => "move-end",
                        0x0016 or 0x0017 => "minimize",
                        0x8002 or 0x8003 => "show-hide",
                        _ => "title"
                    };
                    debounce.Stop();
                    debounce.Start();
                });
            }
            _youtubeTrigger = "start-up";
            debounce.Stop();
            debounce.Start();
            AppendOutput($"[U2] Theo dõi YouTube trên G272 bằng sự kiện cửa sổ ({_winEvents?.HookCount ?? 0} hook, gom {_runtime.YouTube.DebounceMs} ms).");
        }

        /// <summary>TASK-16 U2: watching state changed (UI thread). EQ already switched by the watcher.</summary>
        private void OnYouTubeChanged(bool watching, YouTubeCheck check, EqSwitchResult? eq)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnYouTubeChanged(watching, check, eq)));
                return;
            }
            bool collapsedNow = false;
            if (watching && !_commandMode && Visibility == Visibility.Visible && _ui.AutoCollapse && _effectiveLevel != CockpitLayout.Collapsed)
            {
                ApplyLevel(CockpitLayout.Collapsed, persist: false, "youtube", "youtube");
                collapsedNow = true;
            }
            else if (!watching && _tempReason == "youtube" && !_commandMode)
            {
                ApplyLevel(_ui.HeightLevel, persist: false, "youtube-ended", null);
            }
            UpdateUiBadge();
            string eqText = eq == null ? "EQ: tắt" : eq.Ok ? (eq.Changed ? $"EQ → {eq.IncludeLine}" : $"EQ giữ {eq.IncludeLine}") : $"EQ LỖI: {eq.Error}";
            AppendOutput(watching
                ? $"[U2] ▶ YouTube trên G272 ({check.Process}: {check.Title}) | {eqText}{(collapsedNow ? " | HUD thu về 40 px" : string.Empty)}"
                : $"[U2] ■ Rời YouTube | {eqText}");
            _lastYoutube = new
            {
                watching,
                check,
                eq,
                collapsedNow,
                level = _effectiveLevel,
                atUtc = DateTime.UtcNow.ToString("o")
            };
        }

        /// <summary>TASK-16 pipe "YOUTUBE &lt;title&gt; | off | real" (Verify-Task16 E4): simulate a browser title on the G272 and check once.</summary>
        public string SimulateYouTubeFromPipe(string? argument)
        {
            string arg = (argument ?? string.Empty).Trim();
            YouTubeG272Watcher.SimulatedTitle = arg.Equals("real", StringComparison.OrdinalIgnoreCase) ? null
                : arg.Length == 0 || arg.Equals("off", StringComparison.OrdinalIgnoreCase) ? string.Empty
                : arg;
            _runtime.YouTube.Configure();
            YouTubeCheck check = _runtime.YouTube.CheckNow("pipe");
            UpdateLayout();
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                simulatedTitle = YouTubeG272Watcher.SimulatedTitle,
                check,
                watching = _runtime.YouTube.Watching,
                lastSwitch = _runtime.YouTube.LastSwitch,
                eqDirectory = EqualizerApoSwitcher.ResolveDirectory(_commandProcessor.Config.Current),
                currentProfile = EqualizerApoSwitcher.CurrentProfile(EqualizerApoSwitcher.ResolveDirectory(_commandProcessor.Config.Current)),
                ui = System.Text.Json.JsonDocument.Parse(GetUiStateJson(null, null)).RootElement
            });
        }

        /// <summary>TASK-16 pipe UISTATE: everything Verify-Task16 E2 / E3 / E4 checks, read from the live window.</summary>
        public string GetUiStateJson(string? action = null, string? outcome = null)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long exStyle = HudWindowInterop.GetExStyle(hwnd);
            HudWindowInterop.TryGetWindowRect(hwnd, out int x, out int y, out int w, out int h);
            TargetMonitor? t = _target;
            bool insideTarget = t != null && Fits(t, x, y, w, h);
            WatchWebhookResult? hook = WatchWebhook.LastResult;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                action,
                outcome,
                level = _effectiveLevel,
                savedLevel = CockpitLayout.Normalize(_ui.HeightLevel),
                tempReason = _tempReason,
                height = Height,
                actualHeight = ActualHeight,
                expectedHeight = CockpitLayout.HeightOf(_effectiveLevel),
                rectPx = new { x, y, w, h },
                dpiScale = DpiScale(),
                monitor = t?.FriendlyName,
                monitorMatchedBy = t?.MatchedBy,
                workAreaPx = t == null ? null : new { x = t.WorkLeft, y = t.WorkTop, w = t.WorkWidth, h = t.WorkHeight },
                insideTarget,
                targetInfo = TargetMonitorInfo,
                opacity = Opacity,
                exStyle = $"0x{exStyle:X8}",
                transparent = HudWindowInterop.HasTransparent(exStyle),
                layered = HudWindowInterop.HasLayered(exStyle),
                clickThroughPref = _ui.ClickThrough,
                clickThroughApplied = _clickThroughApplied,
                autoCollapse = _ui.AutoCollapse,
                autoCollapseArmed = _autoCollapseTimer.IsEnabled,
                autoCollapseDelayMs = _autoCollapseDelayMs,
                autoCollapseCount = _autoCollapseCount,
                commandMode = _commandMode,
                inputFocused = CommandInputBox.IsKeyboardFocused,
                foregroundIsCockpit = HudWindowInterop.Foreground() == hwnd,
                visible = Visibility == Visibility.Visible,
                hudStyle = HudStyleInfo,
                mainPanesVisible = MainPanes.Visibility == Visibility.Visible,
                savedFile = _runtime.UiLayout.Path,
                saved = _runtime.UiLayout.Load(),
                lastLevelChange = _lastLevelChange,
                leftMfdMemory = LeftMfdMemoryText.Text,
                leftMfdText = LeftMfdContentText.Text,
                leftMfdHasWorkingSet = LeftMfdContentText.Text.Contains("Working Set", StringComparison.OrdinalIgnoreCase) ||
                                       LeftMfdMemoryText.Text.Contains("Working Set", StringComparison.OrdinalIgnoreCase),
                badge = LatencyBadgeText.Text,
                shownMemoryLabel = _shownMemoryLabel,
                badgeMatchesMfd = _shownMemoryLabel.Length > 0 && LatencyBadgeText.Text.Contains(_shownMemoryLabel, StringComparison.Ordinal) &&
                                  LeftMfdMemoryText.Text == _shownMemoryLabel,
                uiBadge = UiStateBadgeText.Text,
                cardLabel = WidgetIndicatorText.Text,
                cardCount = _lastCardCount,
                statusLine = StatusLineText.Text,
                statusLineVisible = StatusLineText.Visibility == Visibility.Visible,
                outputLines = _outputLines.Count,
                legacyOutputList = false,
                cheatSheetVisible = CheatSheetPanel.Visibility == Visibility.Visible,
                dispatchCardTitle = DispatchCardTitle.Text,
                aiHeader = AiResponseHeader.Text,
                aiVisible = AiResponseScroll.Visibility == Visibility.Visible,
                recentDispatches = new TextRange(RecentDispatchesText.ContentStart, RecentDispatchesText.ContentEnd).Text,
                recentShown = _recentShown,
                recentFilteredOut = _recentFilteredOut,
                hideTestRecords = CockpitDisplayFilter.HideTestRecords,
                hiddenTestSnapshots = _hiddenTestSnapshots,
                lastHiddenTest = _lastHiddenTest,
                youtube = new
                {
                    active = _runtime.YouTube.Active,
                    inactiveReason = _runtime.YouTube.InactiveReason,
                    watching = _runtime.YouTube.Watching,
                    passes = _runtime.YouTube.Passes,
                    hooks = _winEvents?.HookCount ?? 0,
                    eventsReceived = _winEvents?.Received ?? 0,
                    eventsAccepted = _winEvents?.Accepted ?? 0,
                    debounceMs = _runtime.YouTube.DebounceMs,
                    lastCheck = _runtime.YouTube.LastCheck,
                    lastSwitch = _runtime.YouTube.LastSwitch,
                    lastChange = _lastYoutube
                },
                webhook = new
                {
                    configured = WatchWebhook.ResolveUrl(_commandProcessor.Config.Current) != null,
                    host = WatchWebhook.HostOf(WatchWebhook.ResolveUrl(_commandProcessor.Config.Current)),
                    sentCount = WatchWebhook.SentCount,
                    lastResult = hook
                },
                atUtc = DateTime.UtcNow.ToString("o")
            });
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
            bool local = LatestDispatchFile.IsLocalEvent(snapshot.Event);
            bool localNews = local && snapshot.UpdatedAtUtc != _lastLocalUpdatedAt;
            if (local) _lastLocalUpdatedAt = snapshot.UpdatedAtUtc;
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
                qaLatencyMs = snapshot.QaLatencyMs,
                qaResponseChars = snapshot.QaResponse.Length,
                turnCount = snapshot.TurnCount,
                promptTokens = snapshot.PromptTokens,
                completionTokens = snapshot.CompletionTokens,
                totalTokens = snapshot.TotalTokens,
                sessionTotalTokens = snapshot.SessionTotalTokens,
                dailyTotalTokens = snapshot.DailyTotalTokens,
                cacheHit = snapshot.CacheHit,
                cache_hit = snapshot.CacheHit,
                promptTokensSaved = snapshot.PromptTokensSaved,
                prompt_tokens_saved = snapshot.PromptTokensSaved,
                completionTokensSaved = snapshot.CompletionTokensSaved,
                completion_tokens_saved = snapshot.CompletionTokensSaved,
                savedTotalTokens = snapshot.SavedTotalTokens,
                saved_total_tokens = snapshot.SavedTotalTokens,
                groundingSources = snapshot.GroundingSources ?? Array.Empty<string>(),
                grounding_sources = snapshot.GroundingSources ?? Array.Empty<string>(),
                localUtility = snapshot.LocalUtility,
                local_utility = snapshot.LocalUtility,
                localResult = local ? LatestDispatchFile.Shorten(snapshot.AiResponse, 300) : null,
                updatedAtUtc = snapshot.UpdatedAtUtc,
                receivedAtUtc = DateTime.UtcNow.ToString("o"),
                latencyMs
            };

            if (local)
            {
                // TASK-14: local utility results written by the other process (McpTools run_local_utility). Our own
                // results were already printed by RunCommand / OnLocalTimerExpired.
                if (localNews && snapshot.Source != _runtimeSource)
                {
                    AppendOutput($"[SYNC ← {snapshot.Source}] {snapshot.AiResponse} ({latencyMs?.ToString("F0") ?? "?"} ms)");
                    if (snapshot.Event == LatestDispatchFile.TimerExpiredEvent) RaiseTimerAlert(snapshot, snapshot.Source);
                }
                return;
            }

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

        /// <summary>TASK-14: a timer of this process expired (pool thread). The EXPIRED snapshot is already in latest_dispatch.json.</summary>
        private void OnLocalTimerExpired(LocalTimerInfo timer, LatestDispatchSnapshot snapshot, string? fileError)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                _lastLocalUpdatedAt = snapshot.UpdatedAtUtc; // the watcher will see our own write: not news
                RaiseTimerAlert(snapshot, _runtimeSource, fileError);
            }));
        }

        /// <summary>
        /// TASK-14 ADR-59 / ADR-05: show the expiry without taking focus. A hidden Cockpit comes up as Passive HUD
        /// (WS_EX_NOACTIVATE, ShowActivated=False) on the MSI G272; a visible one just updates the card. The foreground
        /// window is read before, right after, and once more after the render pass, so Gate 14 E3 can prove it never changed.
        /// </summary>
        private void RaiseTimerAlert(LatestDispatchSnapshot snapshot, string source, string? fileError = null)
        {
            long ticks = Stopwatch.GetTimestamp();
            IntPtr self = new WindowInteropHelper(this).Handle;
            IntPtr before = HudWindowInterop.Foreground();
            bool wasHidden = Visibility != Visibility.Visible;
            bool wasCommandMode = _commandMode;
            if (wasHidden) ShowPassiveCockpit(ticks, "TimerExpired");
            ApplySnapshot(snapshot);
            AppendOutput($"⏰ {snapshot.AiResponse}" + (fileError != null ? $" | latest_dispatch.json LỖI: {fileError}" : string.Empty));
            IntPtr after = HudWindowInterop.Foreground();
            double alertMs = Math.Round((Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency, 3);
            long count = ++_timerAlertCount;
            string expiredAt = snapshot.UpdatedAtUtc;

            object Build(IntPtr? afterRender) => new
            {
                count,
                message = snapshot.AiResponse,
                source,
                expiredAtUtc = expiredAt,
                alertAtUtc = DateTime.UtcNow.ToString("o"),
                cockpitWasHidden = wasHidden,
                cockpitWasCommandMode = wasCommandMode,
                shownPassive = wasHidden,
                hudStyle = HudStyleInfo,
                noActivate = HudStyleInfo.StartsWith("NOACTIVATE", StringComparison.Ordinal),
                visibleNow = Visibility == Visibility.Visible,
                foregroundBefore = $"0x{before.ToInt64():X}",
                foregroundAfter = $"0x{after.ToInt64():X}",
                foregroundAfterRender = afterRender.HasValue ? $"0x{afterRender.Value.ToInt64():X}" : null,
                foregroundIsCockpit = after == self || afterRender == self,
                foregroundUnchanged = before == after && (!afterRender.HasValue || before == afterRender.Value),
                afterRenderChecked = afterRender.HasValue,
                foregroundTitle = ForegroundWindowInfo.TitleOf(before),
                alertMs,
                fileError
            };

            _lastTimerAlert = Build(null);
            Dispatcher.InvokeAsync(() =>
            {
                // after layout + render of the newly shown HUD: focus must still be where it was
                if (count == _timerAlertCount) _lastTimerAlert = Build(HudWindowInterop.Foreground());
            }, DispatcherPriority.ContextIdle);
        }

        /// <summary>Last /timer expiry alert (pipe TIMERALERT, Verify-Task14 E3).</summary>
        public string GetTimerAlertJson() => System.Text.Json.JsonSerializer.Serialize(_lastTimerAlert);

        /// <summary>TASK-12 pipe VISIONSTATE: the last vision snapshot this Cockpit showed and how long after it was written.</summary>
        public string GetVisionStateJson() => System.Text.Json.JsonSerializer.Serialize(new
        {
            lastSync = _lastVisionSync,
            lastBurst = _vision.LastBurst,                      // TASK-12b: Private WS start / peak (100 ms samples) / after LOH compaction
            lastVisionCompact = MemorySlimming.LastVisionCompact,
            visionCompactCount = MemorySlimming.VisionCompactCount,
            card = new { title = DispatchCardTitle.Text, meta = DispatchCardMeta.Text, status = DispatchCardStatus.Text, header = AiResponseHeader.Text },
            visible = Visibility == Visibility.Visible
        });

        /// <summary>TASK-12: remembers a snapshot that carries an image (latency = now - updatedAtUtc written by the producer).</summary>
        private void NoteVisionSync(LatestDispatchSnapshot snapshot)
        {
            if (!snapshot.HasVisionAttachment) return;
            DateTime now = DateTime.UtcNow;
            double? latencyMs = DateTime.TryParse(snapshot.UpdatedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime written)
                ? Math.Round((now - written.ToUniversalTime()).TotalMilliseconds, 1)
                : null;
            _lastVisionSync = new
            {
                count = ++_visionSyncCount,
                dispatchId = snapshot.DispatchId,
                eventName = snapshot.Event,
                source = snapshot.Source,
                visionSource = snapshot.VisionSource,
                visionImageBytes = snapshot.VisionImageBytes,
                visionImageSha256 = snapshot.VisionImageSha256,
                cacheHit = snapshot.CacheHit,
                totalTokens = snapshot.TotalTokens,
                aiResponsePrefix = snapshot.AiResponse.Length > 160 ? snapshot.AiResponse.Substring(0, 160) : snapshot.AiResponse,
                updatedAtUtc = snapshot.UpdatedAtUtc,
                appliedAtUtc = now.ToString("o"),
                latencyMs,
                foreground = $"0x{HudWindowInterop.Foreground().ToInt64():X}"
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            _localUtilities.TimerExpired -= OnLocalTimerExpired;
            _runtime.YouTube.Changed -= OnYouTubeChanged;
            _winEvents?.Dispose();
            _winEvents = null;
            _youtubeDebounce?.Stop();
            _autoCollapseTimer.Stop();
            _pinsDebounce.Stop();
            _pinsWatcher?.Dispose();
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
            ApplyClickThroughState(); // TASK-16 U1: a saved click-through preference is ready for the first Passive show
            StartYouTubeWatch();      // TASK-16 U2
        }

        public double ShowAndFocusCockpit(long triggerTimestampTicks, string reason = "ToggleCockpit")
        {
            // Focus Restoration: remember who had focus (game / IDE) before the Cockpit takes it.
            IntPtr foreground = HudWindowInterop.Foreground();
            IntPtr self = new WindowInteropHelper(this).Handle;
            if (foreground != IntPtr.Zero && foreground != self) _previousForeground = foreground;
            _commandMode = true;
            _autoCollapseTimer.Stop();

            // Command mode: drop WS_EX_NOACTIVATE so the command box can take keyboard focus.
            ApplyHudMode(noActivate: false);
            // TASK-16 U1: Ctrl+Shift+Space turns click-through off (opacity 1.0) and needs the command box on screen.
            if (_clickThroughApplied) ApplyClickThroughState();
            if (_effectiveLevel == CockpitLayout.Collapsed) ApplyLevel(CockpitLayout.Half, persist: false, "command-mode", "command");
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
            ApplyClickThroughState();
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            double ms = RecordWakeup(triggerTimestampTicks, reason);
            ArmAutoCollapse();
            return ms;
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
            CheatSheetPanel.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Hidden;
            _autoCollapseTimer.Stop();
            ApplyClickThroughState();
            if (restore) HudWindowInterop.RestoreForeground(_previousForeground);
            ArmIdleTrim();
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
            CheatSheetPanel.Visibility = Visibility.Collapsed;
            ApplyClickThroughState(); // TASK-16 U1: the saved click-through preference comes back in PASSIVE
            bool restored = HudWindowInterop.RestoreForeground(_previousForeground);
            UpdateStatusBadge();
            ArmIdleTrim();
            ArmAutoCollapse();
            AppendOutput($"[{DateTime.Now:HH:mm:ss}] {reason}: Passive HUD, focus {(restored ? "trả về cửa sổ trước" : "không trả được (cửa sổ trước đã đóng?)")}.");
        }

        /// <summary>Runs a Cockpit command coming from the pipe ({"cmd":"COCKPIT","text":"..."}); used by Verify-Task04 E6.</summary>
        public string ExecuteFromPipe(string text)
        {
            CockpitCommandResult result = RunCommand(text, HudWindowInterop.Foreground());
            bool cacheHit = result.CacheHit || (result.Snapshot?.CacheHit == true);
            long? savedTokens = result.Snapshot?.SavedTotalTokens ?? result.CacheStats?.SavedTotalTokens;
            IReadOnlyList<string>? groundingSources = result.Snapshot?.GroundingSources ?? result.GroundingResult?.GroundingSources;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = result.Kind,
                aiPending = result.AiTask != null && !result.AiTask.IsCompleted,
                qaPending = result.QaTask != null && !result.QaTask.IsCompleted,
                refinePending = result.RefineTask != null && !result.RefineTask.IsCompleted,
                syncTiming = result.SyncTiming,
                usageStats = result.UsageStats,
                cacheStats = result.CacheStats,
                groundingResult = result.GroundingResult,
                cacheHit,
                cache_hit = cacheHit,
                savedTotalTokens = savedTokens,
                saved_total_tokens = savedTokens,
                groundingSources,
                grounding_sources = groundingSources,
                ok = result.Ok,
                message = result.Message,
                dispatchId = result.Dispatch?.DispatchId ?? result.Verdict?.Dispatch?.Id ?? result.Snapshot?.DispatchId,
                squadId = result.Dispatch?.SquadId ?? result.Verdict?.Dispatch?.SquadId,
                status = result.Snapshot?.Status,
                retryCount = result.Snapshot?.RetryCount,
                turnCount = result.Snapshot?.TurnCount,
                promptTokens = result.Snapshot?.PromptTokens,
                completionTokens = result.Snapshot?.CompletionTokens,
                totalTokens = result.Snapshot?.TotalTokens,
                sessionTotalTokens = result.Snapshot?.SessionTotalTokens,
                dailyTotalTokens = result.Snapshot?.DailyTotalTokens,
                escalatedNow = result.Verdict?.EscalatedNow,
                elapsedMs = result.ElapsedMs,
                // TASK-14 ADR-59
                localUtility = result.Local?.Utility,
                localAction = result.Local?.Action,
                localStatus = result.Local?.Status,
                localBlocked = result.Local?.Blocked,
                localComputeMs = result.Local?.ComputeMs,
                localData = result.Local?.Data,
                aiModel = result.Snapshot?.AiModel,
                eventName = result.Snapshot?.Event,
                // TASK-12 ADR-61
                visionSource = result.Vision?.Source,
                visionBlocked = result.Vision?.Blocked,
                visionBlockedBy = result.Vision?.Guard?.BlockedBy,
                visionGuardMs = result.Vision?.Guard?.ElapsedMs,
                visionGuard = result.Vision?.Guard,
                visionImage = result.Vision?.Image,
                visionMemoHit = result.Vision?.MemoHit,
                visionKey = result.Vision?.VisionKey,
                visionTimings = result.Vision?.Timings,
                hasVisionAttachment = result.Snapshot?.HasVisionAttachment,
                visionImageBytes = result.Snapshot?.VisionImageBytes,
                visionImageSha256 = result.Snapshot?.VisionImageSha256
            });
        }

        private CockpitCommandResult RunCommand(string text, IntPtr foregroundForBoost)
        {
            // TASK-07 Zero-Secret-Leak: the echo never shows "/apikey set <key>" or anything that looks like a key.
            AppendOutput($"NEXUS> {CockpitCommandProcessor.RedactForDisplay(text)}");
            if (ClipboardService.OwnerWindow == IntPtr.Zero) ClipboardService.OwnerWindow = new WindowInteropHelper(this).Handle; // TASK-13
            CockpitCommandResult result = _commandProcessor.Execute(text, foregroundForBoost);
            AppendOutput(result.Message);

            if (result.Snapshot != null) ApplySnapshot(result.Snapshot);
            if (result.Verdict?.EscalatedNow == true && result.Verdict.Dispatch != null) AppendEscalationAlert(result.Verdict.Dispatch.Id);
            if (result.Kind == "hide") HideCockpit("CommandHide");
            if (result.Kind == "reload") ApplyConfigToUi();
            if (result.Kind == "ui" && result.UiAction != null) ApplyUiAction(result.UiAction, "/ui");
            if (result.AiTask != null) _ = AwaitAiAsync(result.AiTask);
            if (result.QaTask != null) _ = AwaitQaAsync(result.QaTask);
            if (result.RefineTask != null) _ = AwaitRefineAsync(result.RefineTask);
            if (result.PinResult != null || result.Local != null) RefreshRightMfd(result.Kind); // TASK-13: pins / timers changed

            // SQLite reads after the caller has given focus back (TASK-07: /ask returns to the game at once).
            UpdateStatusBadge();
            ArmIdleTrim();
            ArmAutoCollapse();
            Dispatcher.InvokeAsync(() =>
            {
                RefreshRecentDispatches();
                RefreshTelemetrySnapshot();
            }, DispatcherPriority.Background);
            return result;
        }

        /// <summary>TASK-08: QA review finished in the background (UI thread continuation).</summary>
        private async System.Threading.Tasks.Task AwaitQaAsync(System.Threading.Tasks.Task<QaOutcome> qaTask)
        {
            try
            {
                QaOutcome outcome = await qaTask;
                if (outcome.Snapshot != null) ApplySnapshot(outcome.Snapshot);
                AppendOutput(outcome.Reviewed
                    ? $"[QA #{outcome.DispatchId}] {outcome.QaAgent}: {outcome.Verdict} → {DispatchVerdictService.StatusLabel(outcome.Status, outcome.RetryCount)} | {outcome.QaLatencyMs} ms | {outcome.TotalTokens} tok | key: {outcome.KeySource}"
                    : $"[QA #{outcome.DispatchId}] {(outcome.Error ?? outcome.QaResponse)}");
                if (outcome.EscalatedNow) AppendEscalationAlert(outcome.DispatchId);
                RefreshRecentDispatches();
            }
            catch (Exception ex)
            {
                AppendOutput($"[QA] Lỗi nền: {ex.GetType().Name}");
            }
        }

        /// <summary>TASK-09: /retry or /followup finished in the background (UI thread continuation).</summary>
        private async System.Threading.Tasks.Task AwaitRefineAsync(System.Threading.Tasks.Task<RefineOutcome> refineTask)
        {
            try
            {
                RefineOutcome outcome = await refineTask;
                if (outcome.Snapshot != null) ApplySnapshot(outcome.Snapshot);
                string tag = outcome.Kind == AgentConsultService.RetryKind ? "RETRY" : "FOLLOW-UP";
                AppendOutput(outcome.AiOk
                    ? $"[{tag} #{outcome.DispatchId}] lượt {outcome.TurnCount} OK | {outcome.AiModel} | {outcome.AiLatencyMs} ms | {outcome.TotalTokens} tok | {outcome.ContentsSent} lượt gửi đi | key: {outcome.KeySource}"
                    : $"[{tag} #{outcome.DispatchId}] KHÔNG CÓ CÂU TRẢ LỜI MỚI (giữ nguyên câu trả lời và nhận xét QA cũ): {outcome.Error}");
                RefreshRecentDispatches();
            }
            catch (Exception ex)
            {
                AppendOutput($"[REFINE] Lỗi nền: {ex.GetType().Name}");
            }
        }

        /// <summary>TASK-07: Gemini finished in the background; this continuation runs back on the UI thread.</summary>
        private async System.Threading.Tasks.Task AwaitAiAsync(System.Threading.Tasks.Task<ConsultOutcome> aiTask)
        {
            try
            {
                ConsultOutcome outcome = await aiTask;
                if (outcome.Snapshot != null) ApplySnapshot(outcome.Snapshot);
                AppendOutput($"[AI #{outcome.DispatchId}] {(outcome.AiOk ? "OK" : "KHÔNG CÓ CÂU TRẢ LỜI")} | " +
                             $"{(outcome.AiModel.Length > 0 ? outcome.AiModel : "-")} | {outcome.AiLatencyMs} ms | {outcome.TotalTokens} tok" +
                             (outcome.CacheHit ? $" | CACHE HIT (saved {outcome.SavedTotalTokens:N0})" : string.Empty) +
                             (outcome.HttpRetryCount > 0 ? $" | retries: {outcome.HttpRetryCount}" : string.Empty) +
                             $" | key: {outcome.KeySource}" +
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
            // TASK-16 B1: the user's real Cockpit never shows a selftest / Verify (mock) record in ACTIVE SQUAD.
            if (CockpitDisplayFilter.HideTestRecords && CockpitDisplayFilter.IsTestSnapshot(snapshot))
            {
                _hiddenTestSnapshots++;
                _lastHiddenTest = $"#{snapshot.DispatchId} {snapshot.Event} {snapshot.AiModel} {snapshot.UpdatedAtUtc}";
                return;
            }
            ArmIdleTrim(); // background AI / QA results and synced snapshots count as activity
            NoteVisionSync(snapshot); // TASK-12
            if (_isInitializedUi) SetCardLabel(WidgetCardExtractor.Extract(
                !string.IsNullOrWhiteSpace(snapshot.AiResponse) ? snapshot.AiResponse : snapshot.QaResponse,
                LatestDispatchFile.IsLocalEvent(snapshot.Event) ? snapshot.LocalUtility : null)); // TASK-13 / TASK-16 B5
            if (_isInitializedUi)
            {
                // TASK-16 U1: a new answer re-opens a HUD that only collapsed by itself (idle / YouTube).
                if (_tempReason is "auto" or "youtube" && (snapshot.AiResponse.Length > 0 || snapshot.QaResponse.Length > 0))
                    ApplyLevel(_ui.HeightLevel, persist: false, "new-answer", null);
                ArmAutoCollapse();
            }
            if (LatestDispatchFile.IsLocalEvent(snapshot.Event))
            {
                ApplyLocalSnapshot(snapshot);
                return;
            }
            Brush brush = StatusBrush(snapshot.Status);
            DispatchCardBorder.BorderBrush = brush;
            DispatchCardTitle.Foreground = brush;
            DispatchCardTitle.Text = $"ACTIVE SQUAD // {snapshot.SquadId}: {snapshot.SquadName ?? "-"}";
            DispatchCardAgents.Text = $"Primary: {snapshot.PrimaryAgent.Name}   QA: {snapshot.QaAgent.Name}   Gate: Reality Checker";
            DispatchCardMeta.Text =
                $"Dispatch ID: #{snapshot.DispatchId}   " +
                (snapshot.ElapsedMs is double ms ? $"Latency: {ms:F2} ms   " : string.Empty) +
                $"Mode: {snapshot.NexusMode}   Turn: {snapshot.TurnCount}   " +
                $"Tokens: {snapshot.TotalTokens} (Today: {snapshot.DailyTotalTokens:N0})   " +
                (snapshot.CacheHit ? $"[CACHE HIT · Saved: {snapshot.SavedTotalTokens:N0}]   " : string.Empty) +
                (snapshot.HasVisionAttachment ? $"[VISION {snapshot.VisionSource}: {snapshot.VisionImageBytes / 1024.0:F0} KB · {(snapshot.VisionImageSha256 ?? string.Empty).PadRight(12).Substring(0, 12)}]   " : string.Empty) +
                $"Nguồn: {snapshot.Source}";
            DispatchCardTask.Text = snapshot.TaskSummary.Length > 0 ? $"Task: {snapshot.TaskSummary}" : string.Empty;
            bool retrying = snapshot.Event == AgentConsultService.RetryKind;      // TASK-09
            bool following = snapshot.Event == AgentConsultService.FollowUpKind;
            bool thinking = ((snapshot.Event == "ask" || snapshot.Event == "ground" || snapshot.Event == VisionConsultService.VisionEvent) &&
                             snapshot.AiResponse.Length == 0) || retrying || following;
            bool reviewing = snapshot.Event == "qa";
            DispatchCardStatus.Foreground = thinking ? ThinkingBrush : reviewing ? QaBrush : brush;
            DispatchCardStatus.Text = retrying
                ? "[RETRYING... Primary Agent refining]"
                : following
                ? $"[FOLLOW-UP... {snapshot.PrimaryAgent.Name} turn {snapshot.TurnCount + 1}]"
                : thinking
                ? "[THINKING... Calling Gemini]"
                : reviewing
                    ? $"[QA REVIEWING... {snapshot.QaAgent.Name}]"
                    : $"[{DispatchVerdictService.StatusLabel(snapshot.Status, snapshot.RetryCount)}]";

            // TASK-08 QA box
            bool hasQa = snapshot.QaResponse.Length > 0 && !reviewing;
            QaResponseHeader.Visibility = hasQa || reviewing ? Visibility.Visible : Visibility.Collapsed;
            QaResponseHeader.Text = hasQa
                ? $"QA REVIEW // {snapshot.QaAgent.Name} · {snapshot.QaLatencyMs} ms"
                : $"QA REVIEW // {snapshot.QaAgent.Name} đang kiểm định...";
            QaResponseText.Text = hasQa ? snapshot.QaResponse : string.Empty;
            QaResponseScroll.Visibility = hasQa ? Visibility.Visible : Visibility.Collapsed;

            // TASK-07 / TASK-11 AI Response box: shown while thinking and once an answer (or diagnostic) is stored.
            bool hasAnswer = snapshot.AiResponse.Length > 0;
            AiResponseHeader.Visibility = thinking || hasAnswer ? Visibility.Visible : Visibility.Collapsed;
            AiResponseHeader.Text = retrying || following
                ? $"AI RESPONSE // lượt {snapshot.TurnCount + 1}: đang gọi Gemini..."
                : hasAnswer
                ? $"AI RESPONSE // {(snapshot.AiModel.Length > 0 ? snapshot.AiModel : "local")} · {snapshot.AiLatencyMs} ms" +
                  (snapshot.TurnCount > 1 ? $" · lượt {snapshot.TurnCount}" : string.Empty) +
                  (snapshot.TotalTokens > 0 ? $" · {snapshot.TotalTokens} tok" : string.Empty) +
                  (snapshot.CacheHit ? " · CACHE HIT" : string.Empty) +
                  (snapshot.GroundingSources is { Count: > 0 } ? $" · Grounded ({snapshot.GroundingSources.Count})" : string.Empty)
                : "AI RESPONSE // đang gọi Gemini...";
            AiResponseText.Text = snapshot.AiResponse;
            AiResponseScroll.Visibility = hasAnswer ? Visibility.Visible : Visibility.Collapsed;
            AiEmptyText.Visibility = hasAnswer || thinking ? Visibility.Collapsed : Visibility.Visible;
            if (hasAnswer) AiResponseScroll.ScrollToTop();
            DispatchCardEvidence.Text = snapshot.EvidenceNotes.Length > 0 ? $"Evidence: {snapshot.EvidenceNotes}" : string.Empty;
            DispatchCardEvidence.Visibility = snapshot.EvidenceNotes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            CurrentCardDispatchId = snapshot.DispatchId;
            CurrentCardStatus = snapshot.Status;
        }

        /// <summary>TASK-14: card for /calc, /timer, /note results (no dispatch, no agents, 0 tokens).</summary>
        private void ApplyLocalSnapshot(LatestDispatchSnapshot snapshot)
        {
            bool expired = snapshot.Event == LatestDispatchFile.TimerExpiredEvent;
            Brush brush = expired ? NeedsWorkBrush : snapshot.Status == "PASSED" ? LocalBrush : EscalatedBrush;
            string utility = (snapshot.LocalUtility ?? "local").ToUpperInvariant();
            DispatchCardBorder.BorderBrush = brush;
            DispatchCardTitle.Foreground = brush;
            DispatchCardTitle.Text = $"LOCAL UTILITY // {utility}";
            DispatchCardAgents.Text = $"Engine: {snapshot.PrimaryAgent.Name}   Model: {snapshot.AiModel}   Gemini: không gọi";
            DispatchCardMeta.Text =
                (snapshot.ElapsedMs is double ms && ms > 0 ? $"Latency: {ms:F3} ms   " : string.Empty) +
                $"Tokens: 0 (Today: {snapshot.DailyTotalTokens:N0})   Nguồn: {snapshot.Source}";
            DispatchCardTask.Text = snapshot.TaskSummary.Length > 0 ? $"Lệnh: {snapshot.TaskSummary}" : string.Empty;
            DispatchCardStatus.Foreground = brush;
            DispatchCardStatus.Text = expired ? "[TIMER EXPIRED]" : $"[{snapshot.Status}]";

            QaResponseHeader.Visibility = Visibility.Collapsed;
            QaResponseText.Text = string.Empty;
            QaResponseScroll.Visibility = Visibility.Collapsed;

            AiResponseHeader.Visibility = Visibility.Visible;
            AiResponseHeader.Text = $"LOCAL RESULT // {snapshot.AiModel} · 0 tok";
            AiResponseText.Text = snapshot.AiResponse;
            AiResponseScroll.Visibility = snapshot.AiResponse.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AiEmptyText.Visibility = snapshot.AiResponse.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (snapshot.AiResponse.Length > 0) AiResponseScroll.ScrollToTop();
            DispatchCardEvidence.Text = string.Empty;
            DispatchCardEvidence.Visibility = Visibility.Collapsed;
            CurrentCardDispatchId = snapshot.DispatchId;
            CurrentCardStatus = snapshot.Status;
        }

        private void RefreshRecentDispatches()
        {
            try
            {
                IReadOnlyList<RecentDispatch> rows = _memoryStore.GetRecentDispatches(CockpitDisplayFilter.HideTestRecords ? 15 : 5);
                _recentFilteredOut = CockpitDisplayFilter.HideTestRecords ? rows.Count(CockpitDisplayFilter.IsTestDispatch) : 0;
                if (CockpitDisplayFilter.HideTestRecords) rows = rows.Where(r => !CockpitDisplayFilter.IsTestDispatch(r)).Take(5).ToList(); // TASK-16 B1
                _recentShown = rows.Select(r => new { id = r.Id, aiModel = r.AiModel, task = r.TaskSummary.Length > 80 ? r.TaskSummary.Substring(0, 80) : r.TaskSummary }).ToArray();
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
                    RecentDispatchesText.Inlines.Add(new Run($"#{r.Id} {r.SquadId} ") { ToolTip = r.TaskSummary.Length > 0 ? r.TaskSummary : null });
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

        /// <summary>
        /// TASK-16 B2: ONE Private WS read (ADR-09 metric) feeds both the top badge and the LEFT MFD memory line, so the two
        /// always show the same text. The old "Working Set" line (shared pages included) is gone.
        /// </summary>
        private void UpdateStatusBadge()
        {
            string label = MemoryDiagnostics.HudLabel(MemoryDiagnostics.PrivateWorkingSetMb());
            _shownMemoryLabel = label;
            LeftMfdMemoryText.Text = label;
            LatencyBadgeText.Text =
                $"MODE: {_commandProcessor.DefaultMode} | HUD: {(_commandMode ? "COMMAND" : "PASSIVE")} | {label} | WAKE: {LastWakeupLatencyMs:F2}ms";
        }

        public void RefreshTelemetrySnapshot()
        {
            UpdateStatusBadge();
            var (journalMode, auditCount, memCount) = _memoryStore.GetWalTelemetrySummary();

            string mode = _mfdModes[_currentMfdIndex];
            LeftMfdHeaderText.Text = mode switch
            {
                "AGENT_SQUAD_283" => "LEFT MFD // AGENT SQUAD",
                "IGPU_GUARD_MSI_G272" => "LEFT MFD // iGPU & MSI G272",
                _ => "LEFT MFD // TELEMETRY & WAL"
            };
            if (mode == "TELEMETRY_WAL")
            {
                LeftMfdContentText.Text =
                    $"SQLite Mode : {journalMode} (5000ms)\n" +
                    $"Audit Logs  : {auditCount} records\n" +
                    $"Memories    : {memCount} records\n" +
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
                    "Primary GPU   : GTX 1660 Free";
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
            StartYouTubeWatch(); // TASK-16 U2: youtubeEqAutoSwitch / equalizerApoConfigDir may have changed
        }

        /// <summary>
        /// 3-tier monitor routing (Gate 2 decision 3): EDID friendly name containing every keyword of
        /// nexus_config.json "preferredMonitorKeywords" (env NEXUS_COCKPIT_MONITOR still overrides, as before)
        /// -> first non-primary monitor -> primary monitor, docked top-right as a HUD.
        /// TASK-16 B6: the window always lies inside the work area of that monitor (the MSI G272 when present, never spilling
        /// onto the LG UltraGear): width = min(1080, work width - 32 px), height = the current snap level, a saved position
        /// is used only when the whole window fits, otherwise top-centre + 16 px.
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
            _target = target;

            if (target is null || !HudWindowInterop.TryGetWindowSize(hwnd, out _, out _))
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Right - Width - 16.0;
                Top = workArea.Top + 16.0;
                TargetMonitorInfo = "Fallback: primary work area (top-right HUD)";
                return;
            }

            const int MarginPx = 16;
            double scale = DpiScale();
            Width = Math.Max(480, Math.Min(1080, (target.WorkWidth - 2 * MarginPx) / scale));
            Height = Math.Min(CockpitLayout.HeightOf(_effectiveLevel), Math.Max(CockpitLayout.CollapsedHeight, (target.WorkHeight - MarginPx) / scale));
            int widthPx = (int)Math.Round(Width * scale);
            int heightPx = (int)Math.Round(Height * scale);
            bool isPrimaryFallback = target.MatchedBy == "PrimaryFallback";
            int x = isPrimaryFallback
                ? target.WorkLeft + target.WorkWidth - widthPx - MarginPx
                : target.WorkLeft + Math.Max(0, (target.WorkWidth - widthPx) / 2);
            int y = target.WorkTop + MarginPx;
            if (_ui.LeftPx is int savedX && _ui.TopPx is int savedY && Fits(target, savedX, savedY, widthPx, heightPx))
            {
                x = savedX;
                y = savedY;
            }

            HudWindowInterop.MoveTo(hwnd, x, y);
            TargetMonitorInfo = isPrimaryFallback ? $"{target} [top-right HUD]" : target.ToString();
        }

        private static bool Fits(TargetMonitor t, int x, int y, int w, int h) =>
            x >= t.WorkLeft && y >= t.WorkTop && x + w <= t.WorkLeft + t.WorkWidth && y + h <= t.WorkTop + t.WorkHeight;

        private double DpiScale()
        {
            try { return Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX); }
            catch (InvalidOperationException) { return 1.0; }
        }

        /// <summary>TASK-16 B6: after a height change or a drag, pull the window back inside the target work area.</summary>
        private void ClampIntoTarget()
        {
            if (_target == null) return;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!HudWindowInterop.TryGetWindowRect(hwnd, out int x, out int y, out int w, out _)) return;
            int h = (int)Math.Round(Height * DpiScale());
            int maxX = _target.WorkLeft + Math.Max(0, _target.WorkWidth - w);
            int maxY = _target.WorkTop + Math.Max(0, _target.WorkHeight - h);
            int nx = Math.Clamp(x, _target.WorkLeft, maxX);
            int ny = Math.Clamp(y, _target.WorkTop, maxY);
            if (nx != x || ny != y) HudWindowInterop.MoveTo(hwnd, nx, ny);
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
                _runtime.YouTube.InvalidateMonitor();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(PositionOnTargetMonitor));
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// TASK-16 B3: the system log no longer scrolls in the centre pane. The newest line is the 1-line status bar; hovering
        /// it shows the last 10 lines. The last 200 lines stay in memory (pipe MEM counts them, as before).
        /// </summary>
        private void AppendOutput(string message)
        {
            _outputLines.Add(message);
            if (_outputLines.Count > 200) _outputLines.RemoveRange(0, _outputLines.Count - 200);
            string oneLine = message.Replace("\r", " ").Replace('\n', ' ');
            StatusLineText.Text = oneLine;
            StatusLineText.ToolTip = string.Join("\n", _outputLines.Skip(Math.Max(0, _outputLines.Count - 10)));
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            // TASK-16 B4: F1 only while this window has keyboard focus (KeyDown of the window): no RegisterHotKey.
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ToggleCheatSheet("F1");
                return;
            }
            if (e.Key == Key.Escape && CheatSheetPanel.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CheatSheetPanel.Visibility = Visibility.Collapsed;
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ReturnToPassive("Esc");
                return;
            }

            // TASK-13: Alt+C / Alt+P only inside the Cockpit window (command mode has keyboard focus). No RegisterHotKey:
            // the game and the IDE keep their own Alt+C / Alt+P.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_commandMode && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt && (key == Key.C || key == Key.P))
            {
                e.Handled = true;
                RunCommand(key == Key.C ? "/copy" : "/pin", HudWindowInterop.Foreground());
            }
        }

        private void OnCommandTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            CommandHintText.Visibility = CommandInputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnCommandInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && CheatSheetPanel.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CheatSheetPanel.Visibility = Visibility.Collapsed;
                return;
            }
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
