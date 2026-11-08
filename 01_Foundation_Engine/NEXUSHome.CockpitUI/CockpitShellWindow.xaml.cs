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

        // TASK-14 ADR-59: /timer expiry -> Passive HUD (WS_EX_NOACTIVATE); evidence for Gate 14 E3 (pipe TIMERALERT).
        private readonly LocalUtilityService _localUtilities;

        // TASK-13a: one-shot idle memory trim (MemorySlimming), re-armed by activity; null when NEXUS_MEMORY_SLIMMING=0.
        private readonly DispatcherTimer? _idleTrimTimer;
        private readonly VaultGroundingEngine _grounding;
        private readonly string _runtimeSource;
        private string? _lastLocalUpdatedAt;
        private long _timerAlertCount;
        private object _lastTimerAlert = new { count = 0L };

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
            _memoryStore = runtime.Store;
            _agentRegistry = runtime.Registry;
            _commandProcessor = new CockpitCommandProcessor(runtime);
            _localUtilities = runtime.LocalUtilities;
            _runtimeSource = runtime.Source;
            _grounding = runtime.Grounding;
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
            _localUtilities.TimerExpired += OnLocalTimerExpired;
            if (MemorySlimming.Enabled)
            {
                _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(MemorySlimming.IdleTrimDelayMs)
                };
                _idleTrimTimer.Tick += OnIdleTrimTick;
            }
            LatestDispatchSnapshot? lastKnown = LatestDispatchFile.TryRead(_commandProcessor.LatestDispatchPath);
            if (lastKnown != null) ApplySnapshot(lastKnown); // show the last Dev-QA state after a restart
            _isInitializedUi = true;
            ArmIdleTrim(); // a freshly started host trims once after start-up too
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

        protected override void OnClosed(EventArgs e)
        {
            _localUtilities.TimerExpired -= OnLocalTimerExpired;
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
            bool restored = HudWindowInterop.RestoreForeground(_previousForeground);
            UpdateStatusBadge();
            ArmIdleTrim();
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
                eventName = result.Snapshot?.Event
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
            if (result.QaTask != null) _ = AwaitQaAsync(result.QaTask);
            if (result.RefineTask != null) _ = AwaitRefineAsync(result.RefineTask);

            // SQLite reads after the caller has given focus back (TASK-07: /ask returns to the game at once).
            UpdateStatusBadge();
            ArmIdleTrim();
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
            ArmIdleTrim(); // background AI / QA results and synced snapshots count as activity
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
                $"Nguồn: {snapshot.Source}";
            DispatchCardTask.Text = snapshot.TaskSummary.Length > 0 ? $"Task: {snapshot.TaskSummary}" : string.Empty;
            bool retrying = snapshot.Event == AgentConsultService.RetryKind;      // TASK-09
            bool following = snapshot.Event == AgentConsultService.FollowUpKind;
            bool thinking = ((snapshot.Event == "ask" || snapshot.Event == "ground") && snapshot.AiResponse.Length == 0) || retrying || following;
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
                $"{MemoryDiagnostics.HudLabel(MemoryDiagnostics.PrivateWorkingSetMb())} | WAKE: {LastWakeupLatencyMs:F2}ms | " +
                $"RENDER: {System.Windows.Media.RenderOptions.ProcessRenderMode}";
        }

        public void RefreshTelemetrySnapshot()
        {
            using var proc = Process.GetCurrentProcess();
            double workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            double? privateWsMb = MemoryDiagnostics.PrivateWorkingSetMb(); // TASK-13a: ADR-09 metric, not PrivateMemorySize64
            var (journalMode, auditCount, memCount) = _memoryStore.GetWalTelemetrySummary();

            string mode = _mfdModes[_currentMfdIndex];
            if (mode == "TELEMETRY_WAL")
            {
                LeftMfdContentText.Text =
                    $"SQLite Mode : {journalMode} (5000ms)\n" +
                    $"Audit Logs  : {auditCount} records\n" +
                    $"Memories    : {memCount} records\n" +
                    $"Working Set : {workingSetMb:F1} MB\n" +
                    $"Private WS  : {(privateWsMb is double pws ? $"{pws:F1} MB / 60 MB" : "n/a")}\n" +
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
