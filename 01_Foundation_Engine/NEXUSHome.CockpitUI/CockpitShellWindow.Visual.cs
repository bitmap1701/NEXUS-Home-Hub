using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NEXUSHomeHub.Core;

namespace NEXUSHome.CockpitUI
{
    /// <summary>
    /// TASK-16c (ADR-68) Cockpit UI v2 visual layer (design boards 1-6): the 44 px top bar chips and the 40 px single line, the
    /// conversation thread (a pool of at most 20 reused item views + the open card), the running card with its progress steps
    /// and Hủy (Esc), the "/" and "@" palettes, the lazy "next step" card and the clipboard image card, the right column
    /// (pinned cards, timer, alliance box) and the Alt+A alliance board. Nothing here runs on a timer while idle: the only
    /// timer ticks every 500 ms while a request is in flight and stops when it ends.
    /// </summary>
    public partial class CockpitShellWindow
    {
        public const int ThreadMax = 20;
        private const string LocalBadgeText = "cục bộ · 0 token";

        private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
        private static readonly Brush VAccent = MakeFrozen(Color.FromRgb(0x38, 0xBD, 0xF8));
        private static readonly Brush VAccent2 = MakeFrozen(Color.FromRgb(0x7D, 0xD3, 0xFC));
        private static readonly Brush VText = MakeFrozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
        private static readonly Brush VText2 = MakeFrozen(Color.FromRgb(0xCB, 0xD5, 0xE1));
        private static readonly Brush VMuted = MakeFrozen(Color.FromRgb(0x94, 0xA3, 0xB8));
        private static readonly Brush VDim = MakeFrozen(Color.FromRgb(0x64, 0x74, 0x8B));
        private static readonly Brush VGreen = MakeFrozen(Color.FromRgb(0x34, 0xD3, 0x99));
        private static readonly Brush VAmber = MakeFrozen(Color.FromRgb(0xFD, 0xE6, 0x8A));
        private static readonly Brush VRed = MakeFrozen(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly Brush VLine = MakeFrozen(Color.FromRgb(0x1E, 0x2A, 0x40));
        private static readonly Brush VLine3 = MakeFrozen(Color.FromRgb(0x33, 0x41, 0x55));
        private static readonly Brush VCard = MakeFrozen(Color.FromRgb(0x0F, 0x1A, 0x2E));
        private static readonly Brush VCardAlt = MakeFrozen(Color.FromRgb(0x11, 0x1A, 0x2B));
        private static readonly Brush VSelect = MakeFrozen(Color.FromRgb(0x16, 0x30, 0x4F));
        private static readonly Brush VInk = MakeFrozen(Color.FromRgb(0x0B, 0x12, 0x20));
        private static readonly Brush VPassBg = MakeFrozen(Color.FromRgb(0x0C, 0x24, 0x18));
        private static readonly Brush VPassLine = MakeFrozen(Color.FromRgb(0x14, 0x53, 0x2D));
        private static readonly Brush VFailBg = MakeFrozen(Color.FromRgb(0x2A, 0x11, 0x14));
        private static readonly Brush VFailLine = MakeFrozen(Color.FromRgb(0x7F, 0x1D, 0x1D));
        private static readonly Brush VFailText = MakeFrozen(Color.FromRgb(0xFC, 0xA5, 0xA5));
        private static readonly Brush VPendingBadge = MakeFrozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly Brush VNoteHead = MakeFrozen(Color.FromRgb(0xA7, 0xF3, 0xD0));
        private static readonly Brush VChecklistHead = MakeFrozen(Color.FromRgb(0xC4, 0xB5, 0xFD));

        private FontFamily? _uiFontCache;
        private FontFamily? _monoFontCache;
        /// <summary>Environment variable of the Gate 16c memory diagnosis: "classic" = Segoe UI + Consolas instead of the decision-1 fonts.</summary>
        public const string FontDiagnosticsVariable = "NEXUS_UI_FONTS";

        /// <summary>
        /// Gate 16c run 2 diagnosis only: with NEXUS_UI_FONTS=classic every font of the window becomes Segoe UI / Consolas (the fonts
        /// of v0.16.0), so the selftest ui-footprint can measure on Nhật Hòa's PC what Segoe UI Variable + Cascadia Mono cost.
        /// Without the variable nothing changes.
        /// </summary>
        private void ApplyFontDiagnostics()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(FontDiagnosticsVariable), "classic", StringComparison.OrdinalIgnoreCase)) return;
            var ui = new FontFamily("Segoe UI");
            var mono = new FontFamily("Consolas");
            Resources["UiFont"] = ui;
            Resources["UiDisplayFont"] = ui;
            Resources["MonoFont"] = mono;
            _uiFontCache = ui;
            _monoFontCache = mono;
            var stack = new Stack<object>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                object o = stack.Pop();
                if (o is DependencyObject d)
                {
                    FontFamily? current = d switch
                    {
                        Control c => c.FontFamily,
                        TextBlock t => t.FontFamily,
                        TextElement e => e.FontFamily,
                        _ => null
                    };
                    if (current != null)
                    {
                        string src = current.Source;
                        if (src.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)) d.SetValue(TextElement.FontFamilyProperty, mono);
                        else if (src.Contains("Variable", StringComparison.OrdinalIgnoreCase)) d.SetValue(TextElement.FontFamilyProperty, ui);
                    }
                    foreach (object child in LogicalTreeHelper.GetChildren(d)) stack.Push(child);
                }
            }
        }

        private FontFamily UiFontFamily => _uiFontCache ??= (TryFindResource("UiFont") as FontFamily) ?? new FontFamily("Segoe UI");
        private FontFamily MonoFontFamily => _monoFontCache ??= (TryFindResource("MonoFont") as FontFamily) ?? new FontFamily("Consolas");

        // ───────────────────────── Top bar chips + 40 px line (board 1, board 6) ─────────────────────────

        private string _segLevel = string.Empty;
        private bool? _segClickThrough;
        private TimeSpan _cpuLastTotal;
        private long _cpuLastTicks;
        private double _lastCpuPct;

        /// <summary>From UpdateUiBadge (also on the WAKE path): property sets only when something changed, no layout change.</summary>
        private void UpdateTopBarControls()
        {
            bool collapsed = _effectiveLevel == CockpitLayout.Collapsed;
            Visibility line = collapsed ? Visibility.Visible : Visibility.Hidden;
            if (CollapsedLine.Visibility != line) CollapsedLine.Visibility = line;
            if (_segLevel != _effectiveLevel)
            {
                _segLevel = _effectiveLevel;
                SetSeg(LevelCollapsedButton, _effectiveLevel == CockpitLayout.Collapsed);
                SetSeg(LevelHalfButton, _effectiveLevel == CockpitLayout.Half);
                SetSeg(LevelFullButton, _effectiveLevel == CockpitLayout.Full);
            }
            if (_segClickThrough != _ui.ClickThrough)
            {
                _segClickThrough = _ui.ClickThrough;
                ClickThroughButton.Background = _ui.ClickThrough ? VSelect : Brushes.Transparent;
                ClickThroughButton.Foreground = _ui.ClickThrough ? VAccent2 : VText2;
            }
        }

        private static void SetSeg(Button b, bool on)
        {
            b.Background = on ? VAccent2 : VCardAlt;
            b.Foreground = on ? VInk : VMuted;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        /// <summary>RAM / CPU / WAL chips and the COMMAND / PASSIVE badge (from UpdateStatusBadge: events only, never a timer).</summary>
        private void UpdateChips(double? privateWsMb)
        {
            string ram = privateWsMb is double mb ? mb.ToString("F1", Vi) : "-";
            RamChipText.Text = $"RAM {ram} / 60 MB";
            CollapsedRamText.Text = $" · RAM {ram} MB";
            _lastCpuPct = SampleCpuPercent();
            CpuChipText.Text = $"CPU {Math.Round(_lastCpuPct).ToString("0", Vi)}%";
            WalChipText.Text = $"WAL · {_commandProcessor.DefaultMode}";
            ModeBadgeText.Text = _commandMode ? "COMMAND" : "PASSIVE";
            ModeBadge.Background = _commandMode ? VAccent : VLine;
            ModeBadgeText.Foreground = _commandMode ? VInk : VMuted;
            UpdateCollapsedStatus();
        }

        private double SampleCpuPercent()
        {
            using Process self = Process.GetCurrentProcess();
            TimeSpan total = self.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            double pct = _lastCpuPct;
            if (_cpuLastTicks != 0)
            {
                double wall = (now - _cpuLastTicks) / (double)Stopwatch.Frequency;
                if (wall >= 0.25) pct = Math.Max(0, (total - _cpuLastTotal).TotalSeconds / (wall * Environment.ProcessorCount) * 100.0);
                else return pct; // too short to mean anything: keep the last figure and the old baseline
            }
            _cpuLastTotal = total;
            _cpuLastTicks = now;
            return pct;
        }

        private bool _unseenAnswer;

        /// <summary>The 40 px line: "● NEXUS · RAM x MB · status" (static colour change, no animation, never opens the window).</summary>
        private void UpdateCollapsedStatus()
        {
            string text;
            Brush brush;
            if (_running != null)
            {
                text = " · ● Gemini đang trả lời…";
                brush = VAccent2;
            }
            else if (_shift != null && !_shiftSeen)
            {
                text = $" · ● {_shift.Agent} xong ca: {_shift.Gate} {_shift.Status}";
                brush = _shift.Status.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ? VRed : VGreen;
            }
            else if (_unseenAnswer && _effectiveLevel == CockpitLayout.Collapsed)
            {
                text = " · ● câu trả lời mới (Ctrl+Shift+Space)";
                brush = VAmber;
            }
            else
            {
                text = " · sẵn sàng";
                brush = VMuted;
            }
            CollapsedStatusText.Text = text;
            CollapsedStatusText.Foreground = brush;
            CollapsedDot.Background = ReferenceEquals(brush, VMuted) ? VAccent : brush;
        }

        private void OnLevelButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string level) ApplyUiAction("level:" + level, "button");
        }

        private void OnClickThroughButtonClick(object sender, RoutedEventArgs e) =>
            ApplyUiAction(_ui.ClickThrough ? "clickthrough:off" : "clickthrough:on", "button");

        private void OnF1ButtonClick(object sender, RoutedEventArgs e) => ToggleCheatSheet("button");

        private void OnCommandBoxFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
            CommandBoxBorder.BorderBrush = CommandInputBox.IsKeyboardFocused ? VAccent : VLine3;

        /// <summary>The answer box never scrolls by itself: the wheel goes to the thread (no nested scrolling).</summary>
        private void OnAnswerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            ThreadScroll.ScrollToVerticalOffset(ThreadScroll.VerticalOffset - e.Delta * 0.4);
        }

        // ───────────────────────── Conversation thread: Card Pool <= 20 (board 1) ─────────────────────────

        private sealed class ThreadItem
        {
            public string Kind = "card";   // "card" (history answer) | "local" (/calc, /timer, /note one-liner)
            public long Id;
            public DateTime AtUtc;
            public string Title = string.Empty;
            public string Meta = string.Empty;
            public string QaLabel = string.Empty;
            public Brush QaBrush = VMuted;
            public string Result = string.Empty;
            public string Excerpt = string.Empty;
            public bool Expired;
            public int HistoryIndex = -1;
        }

        private sealed class ThreadView
        {
            public Border Root = null!;
            public TextBlock Time = null!;
            public TextBlock Title = null!;
            public TextBlock Result = null!;
            public Border Badge = null!;
            public StackPanel MetaRow = null!;
            public TextBlock Meta = null!;
            public TextBlock Qa = null!;
            public TextBlock Excerpt = null!;
            public ThreadItem? Item;
        }

        private readonly List<ThreadView> _threadPool = new();
        private readonly List<ThreadItem> _localLines = new();
        private IReadOnlyList<RecentDispatch>? _threadSourceOverride; // selftest only
        private int _threadShown;
        private long _threadRenders;

        private ThreadView CreateThreadView()
        {
            var v = new ThreadView();
            v.Time = new TextBlock { FontFamily = MonoFontFamily, FontSize = 11, Foreground = VDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            v.Title = new TextBlock { FontFamily = UiFontFamily, FontSize = 13, Foreground = VText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            v.Result = new TextBlock { FontFamily = MonoFontFamily, FontSize = 12, Foreground = VGreen, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
            v.Badge = new Border
            {
                BorderBrush = VLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = LocalBadgeText, FontFamily = UiFontFamily, FontSize = 10.5, Foreground = VMuted }
            };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(v.Time, 0);
            Grid.SetColumn(v.Title, 1);
            Grid.SetColumn(v.Result, 2);
            Grid.SetColumn(v.Badge, 3);
            head.Children.Add(v.Time);
            head.Children.Add(v.Title);
            head.Children.Add(v.Result);
            head.Children.Add(v.Badge);
            v.Meta = new TextBlock { FontFamily = UiFontFamily, FontSize = 11, Foreground = VMuted, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
            v.Qa = new TextBlock { FontFamily = UiFontFamily, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
            v.MetaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            v.MetaRow.Children.Add(v.Meta);
            v.MetaRow.Children.Add(v.Qa);
            v.Excerpt = new TextBlock
            {
                FontFamily = UiFontFamily, FontSize = 12.5, Foreground = VText2, TextWrapping = TextWrapping.Wrap, MaxHeight = 38,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0)
            };
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(v.MetaRow);
            stack.Children.Add(v.Excerpt);
            v.Root = new Border
            {
                Background = VCard, BorderBrush = VLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 6), Child = stack, Cursor = Cursors.Hand, Tag = v
            };
            v.Root.MouseLeftButtonUp += (sender, _) =>
            {
                if (sender is Border { Tag: ThreadView tv } && tv.Item is { HistoryIndex: >= 0 } item) ShowHistory(item.HistoryIndex, "click");
            };
            return v;
        }

        /// <summary>Thread = history answers (except the live one, which is the open card) + local one-liners, oldest first, last 20.</summary>
        private void RenderThread()
        {
            _threadRenders++;
            IReadOnlyList<RecentDispatch> history = _threadSourceOverride ?? _history;
            var items = new List<ThreadItem>(history.Count + _localLines.Count);
            for (int i = 0; i < history.Count; i++)
            {
                RecentDispatch r = history[i];
                if (_threadSourceOverride == null && r.Id == _liveDispatchId) continue;
                (string qa, Brush qaBrush) = QaLabel(r.Status, r.RetryCount, false);
                items.Add(new ThreadItem
                {
                    Kind = "card",
                    Id = r.Id,
                    AtUtc = ParseUtc(r.UpdatedAtUtc.Length > 0 ? r.UpdatedAtUtc : r.TimestampUtc),
                    Title = OneLine(r.TaskSummary.Length > 0 ? r.TaskSummary : $"#{r.Id}"),
                    Meta = string.Join(" · ", new[] { r.SquadId, r.PrimaryAgent, r.AiModel }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    QaLabel = qa,
                    QaBrush = qaBrush,
                    Excerpt = PlainExcerpt(r.AiResponse, 220),
                    HistoryIndex = _threadSourceOverride == null ? i : -1
                });
            }
            items.AddRange(_localLines);
            List<ThreadItem> shown = items.OrderBy(x => x.AtUtc).ToList();
            if (shown.Count > ThreadMax) shown = shown.GetRange(shown.Count - ThreadMax, ThreadMax);

            while (_threadPool.Count < shown.Count && _threadPool.Count < ThreadMax)
            {
                ThreadView v = CreateThreadView();
                _threadPool.Add(v);
                HistoryPanel.Children.Add(v.Root);
            }
            for (int i = 0; i < _threadPool.Count; i++)
            {
                ThreadView v = _threadPool[i];
                if (i >= shown.Count)
                {
                    v.Item = null;
                    v.Root.Visibility = Visibility.Collapsed;
                    continue;
                }
                ThreadItem it = shown[i];
                v.Item = it;
                v.Root.Visibility = Visibility.Visible;
                v.Time.Text = FormatLocalTime(it.AtUtc);
                bool local = it.Kind == "local";
                v.Title.Text = it.Title;
                v.Title.FontWeight = local ? FontWeights.Normal : FontWeights.SemiBold;
                v.Title.Foreground = local ? VText2 : VText;
                v.Result.Text = local ? it.Result : string.Empty;
                v.Result.Foreground = it.Expired ? VAmber : VGreen;
                v.Result.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
                v.Badge.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
                v.MetaRow.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
                v.Meta.Text = it.Meta;
                v.Qa.Text = it.QaLabel;
                v.Qa.Foreground = it.QaBrush;
                v.Excerpt.Text = it.Excerpt;
                v.Excerpt.Visibility = local || it.Excerpt.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                // local results: a thin one-line row without a card frame (board 1)
                v.Root.Background = local ? Brushes.Transparent : VCard;
                v.Root.BorderThickness = local ? new Thickness(0) : new Thickness(1);
                v.Root.Padding = local ? new Thickness(2, 2, 2, 2) : new Thickness(14, 8, 14, 8);
                v.Root.Cursor = local ? Cursors.Arrow : Cursors.Hand;
            }
            _threadShown = shown.Count;
            HighlightThread();
        }

        /// <summary>The thread item that is open in the card gets the accent border (click / Up / Down).</summary>
        private void HighlightThread()
        {
            foreach (ThreadView v in _threadPool)
            {
                if (v.Item == null || v.Item.Kind == "local") continue;
                v.Root.BorderBrush = v.Item.HistoryIndex >= 0 && v.Item.HistoryIndex == _historyIndex ? VAccent : VLine;
            }
        }

        /// <summary>TASK-14 local result → one line in the thread (board 1: "03:02 /calc sqrt(144) + 2^8 = 268 · cục bộ · 0 token").</summary>
        private void AddLocalLine(LatestDispatchSnapshot snapshot)
        {
            string result = snapshot.AiResponse.Replace("\r", " ").Replace('\n', ' ').Trim();
            int close = result.IndexOf(']');
            if (result.StartsWith('[') && close > 0 && close < 24) result = result.Substring(close + 1).Trim(); // "[CALC] …" → "…"
            var item = new ThreadItem
            {
                Kind = "local",
                Id = snapshot.DispatchId,
                AtUtc = ParseUtc(snapshot.UpdatedAtUtc),
                Title = OneLine(snapshot.TaskSummary.Length > 0 ? snapshot.TaskSummary : "/" + (snapshot.LocalUtility ?? "local")),
                Result = result.Length > 120 ? result.Substring(0, 120) + "…" : result,
                Expired = snapshot.Event == LatestDispatchFile.TimerExpiredEvent
            };
            if (_localLines.Count > 0 && _localLines[^1].Title == item.Title && _localLines[^1].Result == item.Result && _localLines[^1].AtUtc == item.AtUtc) return;
            _localLines.Add(item);
            if (_localLines.Count > ThreadMax) _localLines.RemoveRange(0, _localLines.Count - ThreadMax);
            RenderThread();
            ScrollThreadToEnd();
        }

        private void ScrollThreadToEnd() =>
            Dispatcher.InvokeAsync(() => ThreadScroll.ScrollToEnd(), DispatcherPriority.Loaded);

        /// <summary>Puts the top of the open card at the top of the thread view (after the layout pass, off the WAKE path).</summary>
        private void BringOpenCardIntoView() =>
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!DispatchCardBorder.IsVisible) return;
                    Point p = DispatchCardBorder.TransformToAncestor(ThreadPanel).Transform(new Point(0, 0));
                    ThreadScroll.ScrollToVerticalOffset(Math.Max(0, p.Y));
                }
                catch (InvalidOperationException)
                {
                    // not connected to the thread yet
                }
            }, DispatcherPriority.Loaded);

        private static DateTime ParseUtc(string? s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t.ToUniversalTime() : DateTime.UtcNow;

        private static string FormatLocalTime(DateTime utc)
        {
            DateTime local = utc.ToLocalTime();
            return local.Date == DateTime.Now.Date ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        }

        private static string OneLine(string s)
        {
            string t = s.Replace("\r", " ").Replace('\n', ' ').Trim();
            return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
        }

        /// <summary>A plain one-paragraph excerpt of a markdown answer (code fences, **, `, # and list markers removed).</summary>
        private static string PlainExcerpt(string markdown, int max)
        {
            if (string.IsNullOrEmpty(markdown)) return string.Empty;
            var sb = new StringBuilder();
            bool inFence = false;
            foreach (string raw in markdown.Replace("\r", string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
                if (inFence || line.Length == 0) continue;
                line = line.TrimStart('#', ' ').Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = line.Substring(2);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);
                if (sb.Length >= max) break;
            }
            string text = sb.ToString();
            return text.Length > max ? text.Substring(0, max).TrimEnd() + "…" : text;
        }

        private static (string Label, Brush Brush) QaLabel(string? status, int retryCount, bool thinking)
        {
            if (thinking) return ("đang chạy", VAccent2);
            return status switch
            {
                "PASSED" => ("QA PASSED", VGreen),
                "NEEDS_WORK" => (retryCount > 0 ? $"QA NEEDS WORK r{retryCount}" : "QA NEEDS WORK", VAmber),
                "ESCALATED" => ("ESCALATED", VRed),
                "CANCELLED" => ("[ĐÃ HỦY]", VRed),
                _ => ("chưa QA", VDim)
            };
        }

        // ───────────────────────── The open card (board 1) ─────────────────────────

        private (string Time, string Question, string Meta, string Qa, Brush QaBrush) _liveCard = (string.Empty, "Chưa có câu hỏi nào", string.Empty, string.Empty, VDim);

        /// <summary>Fills the card header from a Gemini snapshot (own command or the McpTools watcher) and remembers it as the live card.</summary>
        private void ApplyCardFromSnapshot(LatestDispatchSnapshot s, bool thinking, bool reviewing)
        {
            string model = s.AiModel.Length > 0 ? s.AiModel : (thinking ? "Gemini" : string.Empty);
            string meta = string.Join(" · ", new[] { s.SquadId, s.PrimaryAgent.Name, model }.Where(x => !string.IsNullOrWhiteSpace(x)));
            (string qa, Brush brush) = reviewing ? ($"QA {s.QaAgent.Name} đang chấm…", VAccent2) : QaLabel(s.Status, s.RetryCount, thinking);
            if (s.CacheHit && !thinking) qa += " · CACHE HIT";
            _liveCard = (FormatLocalTime(ParseUtc(s.UpdatedAtUtc)), s.TaskSummary.Length > 0 ? OneLine(s.TaskSummary) : $"#{s.DispatchId}", meta, qa, brush);
            if (_historyIndex < 0) ShowCardHeader(_liveCard);
            if (s.AiResponse.Length > 0)
            {
                _unseenAnswer = !_commandMode;
                UpdateCollapsedStatus();
            }
        }

        private void ShowCardHeader((string Time, string Question, string Meta, string Qa, Brush QaBrush) c)
        {
            CardTimeText.Text = c.Time;
            CardQuestionText.Text = c.Question;
            CardMetaText.Text = c.Meta;
            CardQaText.Text = c.Qa;
            CardQaText.Foreground = c.QaBrush;
        }

        private void ShowCardHeaderForHistory(RecentDispatch r)
        {
            (string qa, Brush brush) = QaLabel(r.Status, r.RetryCount, false);
            ShowCardHeader((FormatLocalTime(ParseUtc(r.UpdatedAtUtc.Length > 0 ? r.UpdatedAtUtc : r.TimestampUtc)),
                OneLine(r.TaskSummary.Length > 0 ? r.TaskSummary : $"#{r.Id}"),
                string.Join(" · ", new[] { r.SquadId, r.PrimaryAgent, r.AiModel }.Where(x => !string.IsNullOrWhiteSpace(x))) + $" · lịch sử ↓",
                qa, brush));
        }

        private long ShownDispatchId => _historyIndex >= 0 && _historyIndex < _history.Count ? _history[_historyIndex].Id : _liveIsLocal ? 0 : _liveDispatchId;

        private void OnCardActionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string action }) return;
            long id = ShownDispatchId;
            switch (action)
            {
                case "copy":
                {
                    HudWidgetCard? code = WidgetCardExtractor.Extract(_shownAnswerText).FirstOrDefault(c => c.Type == "code");
                    ClipboardCopyResult copy = ClipboardService.Copy(code?.Content ?? _shownAnswerText);
                    AppendOutput($"[COPY] Đã chép {(code != null ? "khối code #1" : "câu trả lời")} ({copy.Text.Length} ký tự).");
                    break;
                }
                case "pin":
                    RunCommand("/pin", HudWindowInterop.Foreground());
                    break;
                case "follow":
                    if (id <= 0) { AppendOutput("[HỎI TIẾP] Chưa có câu trả lời Gemini để hỏi tiếp."); break; }
                    if (!_commandMode) ShowAndFocusCockpit(Stopwatch.GetTimestamp(), "card follow-up");
                    CommandInputBox.Text = $"/f #{id} ";
                    CommandInputBox.CaretIndex = CommandInputBox.Text.Length;
                    break;
                case "retry":
                    if (id <= 0) { AppendOutput("[CHẠY LẠI] Chưa có câu trả lời Gemini để chạy lại."); break; }
                    RunCommand($"/retry #{id}", _previousForeground);
                    break;
            }
        }

        private void OnSuggestionChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string key } && int.TryParse(key, out int k)) RunLazyAction(k, "click");
        }

        /// <summary>Board 5: the number keys as buttons under the answer, plus the 4 card actions.</summary>
        private void UpdateSuggestionChips()
        {
            Button[] chips = { SuggestionChip1, SuggestionChip2, SuggestionChip3, SuggestionChip4 };
            for (int i = 0; i < chips.Length; i++)
            {
                (int Key, string Label, string Action) item = _suggestions.FirstOrDefault(x => x.Key == i + 1);
                if (item.Action == null)
                {
                    chips[i].Visibility = Visibility.Collapsed;
                    continue;
                }
                chips[i].Content = $"{item.Key}  {item.Label}";
                chips[i].Visibility = Visibility.Visible;
            }
            bool hasAnswer = _shownAnswerText.Length > 0;
            CardActionsPanel.Visibility = hasAnswer ? Visibility.Visible : Visibility.Collapsed;
            bool gemini = ShownDispatchId > 0;
            CardFollowButton.IsEnabled = gemini;
            CardRetryButton.IsEnabled = gemini;
        }

        // ───────────────────────── Running card + Hủy Esc (board 1, decision 3) ─────────────────────────

        private sealed class RunningRequest
        {
            public System.Threading.CancellationTokenSource? Cts;
            public long DispatchId;
            public readonly Stopwatch Watch = Stopwatch.StartNew();
            public bool HasQa;
            public bool AiDone;
            public string Kind = string.Empty;
            public string Text = string.Empty;
        }

        private RunningRequest? _running;
        private DispatcherTimer? _runningTimer;
        private readonly HashSet<long> _cancelledDispatchIds = new();
        private long _cancelCount;
        private object? _lastCancel;
        private object? _lastRunning;

        /// <summary>Starts the running state when a command left a Gemini request in flight (not for cache hits / local results).</summary>
        private void BeginRunning(CockpitCommandResult result, string text)
        {
            bool aiPending = result.AiTask is { IsCompleted: false } || result.RefineTask is { IsCompleted: false };
            bool qaOnly = !aiPending && result.Kind == "qa" && result.QaTask is { IsCompleted: false };
            if (!result.Ok || (!aiPending && !qaOnly)) return;
            EndRunning("replaced");
            long id = result.Dispatch?.DispatchId ?? result.Snapshot?.DispatchId ?? result.Vision?.Package?.DispatchId ?? 0;
            _running = new RunningRequest
            {
                Cts = result.Cancellation,
                DispatchId = id,
                HasQa = result.QaTask != null,
                AiDone = qaOnly,
                Kind = result.Kind,
                Text = text
            };
            _historyIndex = -1;
            string model = GeminiAgentClient.MockEnabled ? GeminiAgentClient.MockModel : _commandProcessor.Config.Current.GeminiModel;
            DispatchPackage? p = result.Dispatch ?? result.Vision?.Package;
            _liveCard = (DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), OneLine(text),
                string.Join(" · ", new[] { p?.SquadId, p?.PrimaryAgent.Name, model }.Where(x => !string.IsNullOrWhiteSpace(x))), "đang chạy", VAccent2);
            ShowCardHeader(_liveCard);
            double dispatchMs = result.SyncTiming is { DispatchMs: > 0 } st ? st.DispatchMs : result.ElapsedMs;
            StepDispatchText.Text = $"✓ Điều phối {dispatchMs.ToString("0.#", Vi)} ms";
            bool image = result.Vision?.Image != null;
            StepImageText.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            StepImageLine.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            if (image) StepImageText.Text = $"✓ Nén ảnh {(result.Vision!.Image!.Bytes / 1024.0).ToString("0.#", Vi)} KB";
            SetStep(StepGeminiText, qaOnly ? "✓ Gemini" : "● Gemini đang trả lời", qaOnly ? VGreen : VAccent2, !qaOnly);
            SetStep(StepQaText, _running.HasQa ? (qaOnly ? "● QA đang chấm" : "○ QA") : "○ QA (không bật)", qaOnly ? VAccent2 : VMuted, qaOnly);
            ProgressPanel.Visibility = Visibility.Visible;
            DispatchCardBorder.BorderBrush = VAccent;
            CardElapsedText.Text = "0,0 s";
            CardElapsedText.Visibility = Visibility.Visible;
            CardCancelButton.Visibility = Visibility.Visible;
            if (_runningTimer == null)
            {
                _runningTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
                _runningTimer.Tick += (_, _) =>
                {
                    if (_running == null) { _runningTimer!.Stop(); return; }
                    CardElapsedText.Text = $"{_running.Watch.Elapsed.TotalSeconds.ToString("0.0", Vi)} s";
                };
            }
            _runningTimer.Start();
            _lastRunning = new { kind = _running.Kind, dispatchId = id, hasQa = _running.HasQa, image, cancellable = _running.Cts != null, atUtc = DateTime.UtcNow.ToString("o") };
            UpdateCollapsedStatus();
            BringOpenCardIntoView();
        }

        private static void SetStep(TextBlock step, string text, Brush brush, bool bold)
        {
            step.Text = text;
            step.Foreground = brush;
            step.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;
        }

        /// <summary>The Gemini part of the running request finished (answer, error or cancel).</summary>
        private void OnRunningAiFinished(long dispatchId, bool ok, int latencyMs)
        {
            if (_running == null || (_running.DispatchId != 0 && dispatchId != 0 && _running.DispatchId != dispatchId)) return;
            _running.AiDone = true;
            SetStep(StepGeminiText, ok ? $"✓ Gemini {latencyMs} ms" : "✕ Gemini lỗi", ok ? VGreen : VRed, false);
            if (_running.HasQa && ok)
            {
                SetStep(StepQaText, "● QA đang chấm", VAccent2, true);
                return;
            }
            EndRunning(ok ? "done" : "error");
        }

        private void OnRunningQaFinished(long dispatchId)
        {
            if (_running == null || (_running.DispatchId != 0 && dispatchId != 0 && _running.DispatchId != dispatchId)) return;
            EndRunning("done");
        }

        /// <summary>Back to a normal card: the 500 ms timer stops, so an idle Cockpit runs nothing again (0 % CPU).</summary>
        private void EndRunning(string reason)
        {
            _runningTimer?.Stop();
            if (_running == null) return;
            _lastRunning = new { kind = _running.Kind, dispatchId = _running.DispatchId, reason, seconds = Math.Round(_running.Watch.Elapsed.TotalSeconds, 2), atUtc = DateTime.UtcNow.ToString("o") };
            _running = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            DispatchCardBorder.BorderBrush = VLine;
            CardElapsedText.Visibility = Visibility.Collapsed;
            CardCancelButton.Visibility = Visibility.Collapsed;
            UpdateCollapsedStatus();
        }

        private void OnCancelRunningClick(object sender, RoutedEventArgs e) => CancelRunning("button");

        /// <summary>
        /// Decision 3: Hủy (button) or Esc while a card is running. The request's CancellationTokenSource aborts the HTTP call
        /// (the stored answer becomes "[ĐÃ HỦY] …"); whatever arrives later for this dispatch is ignored by the card.
        /// </summary>
        private bool CancelRunning(string source)
        {
            RunningRequest? r = _running;
            if (r == null) return false;
            long t0 = Stopwatch.GetTimestamp();
            bool tokenCancelled = false;
            try
            {
                if (r.Cts != null && !r.Cts.IsCancellationRequested)
                {
                    r.Cts.Cancel();
                    tokenCancelled = true;
                }
            }
            catch (ObjectDisposedException)
            {
                tokenCancelled = false;
            }
            if (r.DispatchId > 0) _cancelledDispatchIds.Add(r.DispatchId);
            _cancelCount++;
            SetStep(StepGeminiText, "✕ Đã hủy", VRed, true);
            AiResponseHeader.Text = "AI RESPONSE // ĐÃ HỦY";
            SetLiveAnswer(GeminiAgentClient.CancelledMessage, AiResponseHeader.Text, r.DispatchId, isLocal: false);
            AiResponseScroll.Visibility = Visibility.Visible;
            AiEmptyText.Visibility = Visibility.Collapsed;
            _liveCard = (_liveCard.Time, _liveCard.Question, _liveCard.Meta, "[ĐÃ HỦY]", VRed);
            EndRunning("cancelled");
            ShowCardHeader(_liveCard);
            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            _lastCancel = new
            {
                source, dispatchId = r.DispatchId, kind = r.Kind, tokenCancelled, hadToken = r.Cts != null, ms,
                afterSeconds = Math.Round(r.Watch.Elapsed.TotalSeconds, 2), cardStatus = CardQaText.Text, count = _cancelCount, atUtc = DateTime.UtcNow.ToString("o")
            };
            AppendOutput($"[HỦY] #{r.DispatchId} {r.Kind}: đã hủy sau {r.Watch.Elapsed.TotalSeconds.ToString("0.0", Vi)} s ({source}); phản hồi đến muộn sẽ bị bỏ qua.");
            return true;
        }

        /// <summary>A snapshot of a cancelled dispatch arriving later (watcher / background task): not shown.</summary>
        private bool IsCancelledSnapshot(LatestDispatchSnapshot s) =>
            s.DispatchId > 0 && _cancelledDispatchIds.Contains(s.DispatchId) && !LatestDispatchFile.IsLocalEvent(s.Event);

        public string CancelFromPipe()
        {
            bool cancelled = CancelRunning("pipe");
            return System.Text.Json.JsonSerializer.Serialize(new { cancelled, lastCancel = _lastCancel, running = _running != null, card = CardQaText.Text, answer = _shownAnswerText });
        }

        // ───────────────────────── "/" and "@" palettes (board 2) ─────────────────────────

        private sealed class PaletteRowView
        {
            public Border Root = null!;
            public TextBlock A = null!;
            public TextBlock B = null!;
            public TextBlock C = null!;
            public int SelectIndex = -1;
        }

        private const int PaletteRowMax = 24; // the rows scroll inside the palette (Half 330 px has little room)
        private readonly List<PaletteRowView> _paletteRows = new();
        private readonly List<string> _paletteFills = new();
        private readonly List<string> _recentCommands = new();
        private PaletteQuery? _paletteQuery;
        private int _paletteSelected;
        private object? _lastPaletteAccept;
        private bool _paletteSuppress;

        private PaletteRowView CreatePaletteRow()
        {
            var row = new PaletteRowView
            {
                A = new TextBlock { FontFamily = MonoFontFamily, FontSize = 12.5, Foreground = VAccent2, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                B = new TextBlock { FontFamily = UiFontFamily, FontSize = 12.5, Foreground = VText2, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                C = new TextBlock { FontFamily = MonoFontFamily, FontSize = 11.5, Foreground = VMuted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            Grid.SetColumn(row.B, 1);
            Grid.SetColumn(row.C, 2);
            grid.Children.Add(row.A);
            grid.Children.Add(row.B);
            grid.Children.Add(row.C);
            row.Root = new Border { Padding = new Thickness(14, 6, 14, 6), Child = grid, Background = Brushes.Transparent, Tag = row, Cursor = Cursors.Hand };
            row.Root.MouseLeftButtonUp += (sender, _) =>
            {
                if (sender is Border { Tag: PaletteRowView r } && r.SelectIndex >= 0)
                {
                    _paletteSelected = r.SelectIndex;
                    PaletteAccept("click");
                }
            };
            _paletteRows.Add(row);
            PaletteRows.Children.Add(row.Root);
            return row;
        }

        /// <summary>Text changed: show / filter / hide the palette (no timer: it follows the keystrokes).</summary>
        private void UpdatePalette()
        {
            if (_paletteSuppress) return;
            PaletteQuery? q = CockpitPalette.Detect(CommandInputBox.Text);
            if (q == null)
            {
                ClosePalette();
                return;
            }
            bool modeChanged = _paletteQuery?.Mode != q.Mode || _paletteQuery?.Query != q.Query;
            _paletteQuery = q;
            if (modeChanged) _paletteSelected = 0;
            _paletteFills.Clear();
            var rows = new List<(string A, string B, string C, bool Header)>();
            if (q.Mode == CockpitPalette.SlashMode)
            {
                PaletteHeaderText.Text = "GÕ \"/\" · DANH SÁCH LỆNH, LỌC THEO CHỮ, TAB ĐỂ CHỌN";
                PaletteFooterText.Text = "↑↓ chọn · Tab (hoặc Enter) điền lệnh · Esc đóng";
                (IReadOnlyList<PaletteCommand> recent, IReadOnlyList<PaletteCommand> all) = CockpitPalette.FilterCommands(q.Query, _recentCommands, CockpitPalette.Commands.Count);
                if (recent.Count > 0)
                {
                    rows.Add(("VỪA DÙNG", string.Empty, string.Empty, true));
                    foreach (PaletteCommand c in recent) { rows.Add((c.Name, c.Description, c.Example, false)); _paletteFills.Add(c.Name); }
                }
                if (all.Count > 0)
                {
                    rows.Add(("TẤT CẢ", string.Empty, string.Empty, true));
                    foreach (PaletteCommand c in all) { rows.Add((c.Name, c.Description, c.Example, false)); _paletteFills.Add(c.Name); }
                }
            }
            else
            {
                IReadOnlyList<PaletteAgent> agents = CockpitPalette.FilterAgents(_agentRegistry.GetAll(), q.Query, 8);
                PaletteHeaderText.Text = $"GÕ \"@{q.Query}\" · CHỌN 1 TRONG {_agentRegistry.Count} AGENT";
                PaletteFooterText.Text = "↑↓ chọn · Tab / Enter gắn @agent vào đầu câu · Esc đóng";
                foreach (PaletteAgent a in agents) { rows.Add((a.Name, a.Division, a.Id, false)); _paletteFills.Add(a.Id); }
            }
            if (_paletteFills.Count == 0) rows.Add(("(không khớp)", "gõ tiếp hoặc xóa bớt chữ", string.Empty, true));
            if (rows.Count > PaletteRowMax) rows = rows.GetRange(0, PaletteRowMax);
            while (_paletteRows.Count < rows.Count) CreatePaletteRow();
            int selectable = 0;
            for (int i = 0; i < _paletteRows.Count; i++)
            {
                PaletteRowView v = _paletteRows[i];
                if (i >= rows.Count)
                {
                    v.Root.Visibility = Visibility.Collapsed;
                    v.SelectIndex = -1;
                    continue;
                }
                (string a, string b, string c, bool header) = rows[i];
                v.Root.Visibility = Visibility.Visible;
                v.A.Text = a;
                v.B.Text = b;
                v.C.Text = c;
                v.A.FontFamily = header ? UiFontFamily : (q.Mode == CockpitPalette.AgentMode ? UiFontFamily : MonoFontFamily);
                v.A.FontSize = header ? 10.5 : 12.5;
                v.A.Foreground = header ? VMuted : (q.Mode == CockpitPalette.AgentMode ? VText : VAccent2);
                v.A.FontWeight = header || q.Mode == CockpitPalette.AgentMode ? FontWeights.SemiBold : FontWeights.Normal;
                v.Root.Padding = header ? new Thickness(14, 4, 14, 2) : new Thickness(14, 6, 14, 6);
                v.SelectIndex = header ? -1 : selectable++;
                v.Root.Cursor = header ? Cursors.Arrow : Cursors.Hand;
            }
            _paletteSelected = _paletteFills.Count == 0 ? 0 : Math.Clamp(_paletteSelected, 0, _paletteFills.Count - 1);
            HighlightPalette();
            CommandPalette.Visibility = Visibility.Visible;
        }

        private void HighlightPalette()
        {
            foreach (PaletteRowView v in _paletteRows)
            {
                bool on = v.SelectIndex >= 0 && v.SelectIndex == _paletteSelected;
                v.Root.Background = on ? VSelect : Brushes.Transparent;
                if (on && v.Root.IsVisible) v.Root.BringIntoView();
            }
        }

        private void ClosePalette()
        {
            _paletteQuery = null;
            _paletteFills.Clear();
            if (CommandPalette.Visibility != Visibility.Collapsed) CommandPalette.Visibility = Visibility.Collapsed;
        }

        private bool PaletteOpen => CommandPalette.Visibility == Visibility.Visible && _paletteQuery != null;

        private void PaletteMove(int delta)
        {
            if (_paletteFills.Count == 0) return;
            _paletteSelected = (_paletteSelected + delta + _paletteFills.Count) % _paletteFills.Count;
            HighlightPalette();
        }

        /// <summary>Tab / Enter / click: "/" fills the command and a space; "@" puts "@&lt;agentId&gt; " at the start of the line.</summary>
        private bool PaletteAccept(string source)
        {
            PaletteQuery? q = _paletteQuery;
            if (q == null || _paletteFills.Count == 0) return false;
            string fill = _paletteFills[Math.Clamp(_paletteSelected, 0, _paletteFills.Count - 1)];
            string text = CommandInputBox.Text;
            string next;
            if (q.Mode == CockpitPalette.SlashMode)
            {
                next = fill + " ";
            }
            else
            {
                string before = text.Substring(0, Math.Min(q.TokenStart, text.Length)).Trim();
                next = $"@{fill} " + (before.Length > 0 ? before + " " : string.Empty);
            }
            _paletteSuppress = true;
            try
            {
                CommandInputBox.Text = next;
                CommandInputBox.CaretIndex = next.Length;
            }
            finally
            {
                _paletteSuppress = false;
            }
            ClosePalette();
            _lastPaletteAccept = new { source, mode = q.Mode, query = q.Query, fill, text = next, atUtc = DateTime.UtcNow.ToString("o") };
            return true;
        }

        /// <summary>Pipe "PALETTE &lt;text&gt;" types into the command box; "PALETTE key:down|up|tab|enter|esc" presses a key.</summary>
        public string PaletteFromPipe(string? argument)
        {
            string arg = argument ?? string.Empty;
            if (arg.StartsWith(' ')) arg = arg.Substring(1);
            long t0 = Stopwatch.GetTimestamp();
            string outcome = "ok";
            if (arg.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
            {
                switch (arg.Substring(4).Trim().ToLowerInvariant())
                {
                    case "down": PaletteMove(1); break;
                    case "up": PaletteMove(-1); break;
                    case "tab":
                    case "enter": outcome = PaletteAccept("pipe") ? "accepted" : "nothing to accept"; break;
                    case "esc": ClosePalette(); break;
                    default: outcome = "unknown key"; break;
                }
            }
            else
            {
                CommandInputBox.Text = arg;
                CommandInputBox.CaretIndex = arg.Length;
            }
            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            UpdateLayout();
            return System.Text.Json.JsonSerializer.Serialize(new { argument = arg, outcome, ms, box = CommandInputBox.Text, palette = PaletteState() });
        }

        private object PaletteState() => new
        {
            visible = CommandPalette.Visibility == Visibility.Visible,
            mode = _paletteQuery?.Mode,
            query = _paletteQuery?.Query,
            header = PaletteHeaderText.Text,
            rows = _paletteRows.Where(r => r.Root.Visibility == Visibility.Visible)
                .Select(r => new { a = r.A.Text, b = r.B.Text, c = r.C.Text, selectable = r.SelectIndex >= 0, selected = r.SelectIndex >= 0 && r.SelectIndex == _paletteSelected }).ToArray(),
            pooledRows = _paletteRows.Count,
            selectedFill = _paletteFills.Count > 0 ? _paletteFills[Math.Clamp(_paletteSelected, 0, _paletteFills.Count - 1)] : null,
            recent = _recentCommands.ToArray(),
            lastAccept = _lastPaletteAccept
        };

        // ───────────────────────── Lazy mode (board 5) ─────────────────────────

        private string? _lazyReason;

        /// <summary>The big "VIỆC TIẾP THEO CỦA ANH" card: on an empty thread, after a shift report, or on request.</summary>
        private void ShowLazyNextStep(AllianceSnapshot a, string reason)
        {
            _lazyReason = reason;
            LazyTitleText.Text = a.NextStepLabel;
            string gate = a.GateState switch
            {
                "pass" => "Gate ALL PASS",
                "pending" => "Gate chưa đủ bằng chứng",
                _ => "chưa có bàn giao"
            };
            LazySubText.Text = $"{a.TaskId ?? "TASK-?"}{(a.Adr != null ? $" ({a.Adr})" : string.Empty)} · {gate} · {a.ClipboardText.Length} ký tự sẵn để dán";
            int stage = a.NextStep switch
            {
                AllianceStatus.StepImplement => 0,
                AllianceStatus.StepVerify => 1,
                _ => 2
            };
            TextBlock[] steps = { LazyStep1, LazyStep2, LazyStep3, LazyStep4 };
            string[] names = { "Claude viết code", "Claude Code chạy Verify", "Gemini duyệt", "Merge + tag" };
            for (int i = 0; i < steps.Length; i++)
            {
                if (i < stage) SetStep(steps[i], "✓ " + names[i], VGreen, false);
                else if (i == stage) SetStep(steps[i], "● " + names[i], VAmber, true);
                else SetStep(steps[i], "○ " + names[i], VMuted, false);
            }
            LazyNextCard.Visibility = Visibility.Visible;
        }

        private void HideLazyNextStep(string reason)
        {
            if (LazyNextCard.Visibility == Visibility.Collapsed) return;
            LazyNextCard.Visibility = Visibility.Collapsed;
            _lazyReason = "hidden: " + reason;
        }

        private void OnLazyCloseClick(object sender, RoutedEventArgs e) => HideLazyNextStep("button");

        private void OnLazyAltEnterClick(object sender, RoutedEventArgs e) => CopyAllianceNextStep("button");

        // ───────────────────────── Clipboard image card (board 5) ─────────────────────────

        private static readonly string[] ImagePrompts = { "Lỗi gì đây, sửa sao?", "Tóm tắt nội dung ảnh", "Dịch chữ trong ảnh sang tiếng Việt" };
        private object? _lastImageAsk;

        private void ShowImageHint() => ImageHintCard.Visibility = Visibility.Visible;

        private bool ImageHintVisible => ImageHintCard.Visibility == Visibility.Visible;

        private void OnImageAskClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string n } && int.TryParse(n, out int k)) RunImageAsk(k, "click");
        }

        /// <summary>Enter / 2 / 3 on the image card = /img with that question (the user's own action: one Gemini call).</summary>
        private string RunImageAsk(int n, string source)
        {
            if (n < 1 || n > ImagePrompts.Length) return "no such prompt";
            string prompt = ImagePrompts[n - 1];
            ImageHintCard.Visibility = Visibility.Collapsed;
            ResetCommandHint();
            CockpitCommandResult r = RunCommand($"/img {prompt}", _previousForeground);
            _lastImageAsk = new { n, prompt, source, ok = r.Ok, kind = r.Kind, atUtc = DateTime.UtcNow.ToString("o") };
            if (_commandMode && source.StartsWith("key", StringComparison.Ordinal)) ReturnToPassive($"image {n}");
            return r.Message;
        }

        // ───────────────────────── Right column alliance box + Alt+A board (board 1, board 3) ─────────────────────────

        private AllianceBoard? _lastBoard;
        private object? _lastBoardCopy;
        private readonly List<ShiftState> _shiftFeed = new();
        private readonly List<Border> _feedViews = new();

        private void RefreshAllianceBox(AllianceSnapshot a)
        {
            string state = a.NextStep switch
            {
                AllianceStatus.StepImplement => "đang triển khai",
                AllianceStatus.StepVerify => "chờ Claude Code Verify",
                _ => a.GateState == "pass" ? "chờ Gemini duyệt" : "chờ Gemini"
            };
            AllianceTaskText.Text = $"{a.TaskId ?? "TASK-?"} · {state}";
            AllianceNextText.Text = "Việc kế tiếp: " + a.NextStepLabel;
        }

        private void NoteShiftFeed(ShiftState s)
        {
            if (_shiftFeed.Count > 0 && _shiftFeed[0].AtUtc == s.AtUtc && _shiftFeed[0].Gate == s.Gate) return;
            _shiftFeed.Insert(0, s);
            if (_shiftFeed.Count > 3) _shiftFeed.RemoveRange(3, _shiftFeed.Count - 3);
        }

        /// <summary>Board 3, filled only when the board opens or a copy key is pressed (file reads, 0 token).</summary>
        private void FillAllianceBoard(AllianceBoard b)
        {
            _lastBoard = b;
            AllianceSnapshot s = b.Snapshot;
            AllianceSourceText.Text = $"Đọc từ current_task.md và {s.HandoffFile ?? "gate<N>_handoff.md"} · chỉ đọc file, 0 token ({b.ElapsedMs.ToString("0.#", Vi)} ms)";
            AllianceBranchText.Text = s.SpecBranch ?? s.GitBranch ?? string.Empty;
            AllianceGateTitleText.Text = b.GateTitle;
            (string badge, Brush bg) = s.GateState switch
            {
                "pass" => ("ALL PASS", VGreen),
                "pending" => ("CHƯA ALL PASS", VPendingBadge),
                _ => ("CHƯA CÓ BÀN GIAO", VMuted)
            };
            AllianceGateBadgeText.Text = badge;
            AllianceGateBadge.Background = bg;
            Border[] boxes = { AllianceE1, AllianceE2, AllianceE3, AllianceE4, AllianceE5 };
            TextBlock[] texts = { AllianceE1Text, AllianceE2Text, AllianceE3Text, AllianceE4Text, AllianceE5Text };
            for (int i = 0; i < boxes.Length; i++)
            {
                bool? pass = i < b.Checks.Count ? b.Checks[i].Pass : null;
                boxes[i].Background = pass == true ? VPassBg : pass == false ? VFailBg : VCardAlt;
                boxes[i].BorderBrush = pass == true ? VPassLine : pass == false ? VFailLine : VLine;
                texts[i].Text = $"E{i + 1} " + (pass == true ? "✓" : pass == false ? "✗" : "·");
                texts[i].Foreground = pass == true ? VGreen : pass == false ? VFailText : VMuted;
            }
            AllianceNextStepText.Text = s.NextStepLabel;
            AllianceGeminiPreview.Text = Preview(b.GeminiMessage);
            AllianceClaudeCodePreview.Text = Preview(b.ClaudeCodePrompt);
            AllianceClosedText.Text = b.ClosedGates.Count == 0 ? "(chưa có)" : string.Join("   ", b.ClosedGates.Skip(Math.Max(0, b.ClosedGates.Count - 6)));
            MemberGeminiState.Text = s.NextStep == AllianceStatus.StepReview ? "Đến lượt" : "Chờ";
            MemberGeminiState.Foreground = s.NextStep == AllianceStatus.StepReview ? VAmber : VMuted;
            MemberClaudeState.Text = s.NextStep == AllianceStatus.StepImplement ? "Đến lượt" : "Chờ đặc tả / bản vá";
            MemberClaudeState.Foreground = s.NextStep == AllianceStatus.StepImplement ? VAmber : VMuted;
            ShiftState? cc = _shiftFeed.FirstOrDefault(x => x.Agent.Contains("Claude Code", StringComparison.OrdinalIgnoreCase)) ?? s.Shift;
            MemberClaudeCodeState.Text = s.NextStep == AllianceStatus.StepVerify ? "Đến lượt"
                : cc != null ? $"Xong ca {FormatLocalTime(ParseUtc(cc.AtUtc))}" : "Chờ";
            MemberClaudeCodeState.Foreground = s.NextStep == AllianceStatus.StepVerify ? VAmber : VMuted;
            if (s.Shift != null) NoteShiftFeed(s.Shift);
            RenderFeed();
            RefreshAllianceBox(s);

            static string Preview(string text)
            {
                string t = text.Replace("\r", string.Empty).Replace('\n', ' ').Trim();
                return t.Length > 170 ? t.Substring(0, 170) + "…" : t;
            }
        }

        private void RenderFeed()
        {
            while (_feedViews.Count < Math.Min(3, _shiftFeed.Count))
            {
                var title = new TextBlock { FontFamily = UiFontFamily, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var body = new TextBlock { FontFamily = UiFontFamily, FontSize = 11.5, Foreground = VText2, TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
                var panel = new StackPanel();
                panel.Children.Add(title);
                panel.Children.Add(body);
                var border = new Border { Background = VCardAlt, BorderBrush = VLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), Child = panel };
                _feedViews.Add(border);
                AllianceFeedPanel.Children.Add(border);
            }
            for (int i = 0; i < _feedViews.Count; i++)
            {
                Border v = _feedViews[i];
                if (i >= _shiftFeed.Count) { v.Visibility = Visibility.Collapsed; continue; }
                ShiftState s = _shiftFeed[i];
                var panel = (StackPanel)v.Child;
                var title = (TextBlock)panel.Children[0];
                var body = (TextBlock)panel.Children[1];
                bool failed = s.Status.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || s.Status.Contains("ESCALATED", StringComparison.OrdinalIgnoreCase);
                title.Text = $"{FormatLocalTime(ParseUtc(s.AtUtc))}  {s.Gate} · {s.Status}";
                title.Foreground = failed ? VFailText : VGreen;
                body.Text = s.Summary;
                v.Visibility = Visibility.Visible;
            }
            AllianceFeedEmptyText.Visibility = _shiftFeed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAllianceOpenClick(object sender, RoutedEventArgs e) => ToggleAlliancePanel("button");

        private void OnAllianceCopyClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string kind }) CopyAllianceText(kind, "button");
        }

        /// <summary>Alt+G = the message for Gemini (section 5), Alt+K = the Verify prompt for Claude Code (section 2).</summary>
        private string CopyAllianceText(string kind, string source)
        {
            long t0 = Stopwatch.GetTimestamp();
            AllianceBoard b = AllianceStatus.ReadBoard(_runtime.HandoffDirectory, _runtime.ConfigDirectory);
            FillAllianceBoard(b);
            bool gemini = kind is "gemini" or "g";
            string text = gemini ? b.GeminiMessage : b.ClaudeCodePrompt;
            ClipboardCopyResult copy = ClipboardService.Copy(text);
            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            _lastBoardCopy = new { kind = gemini ? "gemini" : "prompt", source, chars = text.Length, systemClipboard = copy.SystemClipboard, ms, atUtc = DateTime.UtcNow.ToString("o") };
            AppendOutput($"[LIÊN MINH] Đã chép {(gemini ? "lời nhắn cho Gemini (Alt+G)" : "prompt cho Claude Code (Alt+K)")}: {text.Length} ký tự. Dán bằng Ctrl+V.");
            return text;
        }

        private object? BoardState() => _lastBoard == null ? null : new
        {
            gateTitle = _lastBoard.GateTitle,
            badge = AllianceGateBadgeText.Text,
            checks = _lastBoard.Checks.Select(c => new { c.Id, c.Pass }).ToArray(),
            checksFile = _lastBoard.ChecksFile,
            geminiChars = _lastBoard.GeminiMessage.Length,
            promptChars = _lastBoard.ClaudeCodePrompt.Length,
            closed = _lastBoard.ClosedGates,
            nextStep = _lastBoard.Snapshot.NextStep,
            members = new[] { MemberGeminiState.Text, MemberClaudeState.Text, MemberClaudeCodeState.Text, MemberAntigravityState.Text },
            feed = _shiftFeed.Count,
            readMs = _lastBoard.ElapsedMs
        };

        // ───────────────────────── Pins (right column cards, board 1) ─────────────────────────

        private string[] _pinSlots = new string[5];

        /// <summary>
        /// Card view of the 5 pin slots: "#1 code · csharp" + 2 preview lines. <see cref="_pinSlots"/> keeps the TASK-13 text of
        /// each slot ("#id [type:lang] title\npreview"), which pipe PINSTATE returns as before (Verify-Task12 / 13).
        /// </summary>
        private void RenderPinCards(IReadOnlyList<MfdPin> pins, int max)
        {
            Border[] cards = { PinCard1, PinCard2, PinCard3, PinCard4, PinCard5 };
            TextBlock[] heads = { PinHead1, PinHead2, PinHead3, PinHead4, PinHead5 };
            TextBlock[] bodies = { MfdPin1Text, MfdPin2Text, MfdPin3Text, MfdPin4Text, MfdPin5Text };
            var slots = new string[cards.Length];
            for (int i = 0; i < cards.Length; i++)
            {
                if (i < pins.Count)
                {
                    MfdPin p = pins[i];
                    string preview = WidgetCardExtractor.PreviewAfterTitle(p.Title, p.Content);
                    slots[i] = $"#{p.PinId} [{p.Type}{(p.Language != null ? ":" + p.Language : string.Empty)}] {p.Title}" +
                               (preview.Length > 0 ? "\n" + preview : string.Empty);
                    heads[i].Text = $"#{p.PinId} {p.Type}{(p.Language != null ? " · " + p.Language : string.Empty)}";
                    heads[i].Foreground = p.Type switch { "code" => VAmber, "note" => VNoteHead, "checklist" => VChecklistHead, _ => VAccent2 };
                    bodies[i].Text = p.Title + (preview.Length > 0 ? "\n" + preview : string.Empty);
                    cards[i].Visibility = Visibility.Visible;
                }
                else
                {
                    slots[i] = i < max && i == pins.Count ? "(ô trống: Alt+P hoặc /pin để ghim)" : string.Empty;
                    heads[i].Text = string.Empty;
                    bodies[i].Text = string.Empty;
                    cards[i].Visibility = Visibility.Collapsed;
                }
            }
            _pinSlots = slots;
            PinsCountText.Text = $"{pins.Count}/{max}";
            PinEmptyText.Visibility = pins.Count < max ? Visibility.Visible : Visibility.Collapsed;
        }

        // ───────────────────────── UISTATE "visual" + headless self-check (selftest ui-v2-visual) ─────────────────────────

        private object VisualState()
        {
            var body = (FrameworkElement)MainPanes.Children[0];
            return new
            {
                topBarHeight = Math.Round(TopBar.ActualHeight, 1),
                topRowHeight = Math.Round(((Grid)ChromeBorder.Child).RowDefinitions[0].ActualHeight, 1),
                columns = MainPanes.ColumnDefinitions.Count,
                conversationWidth = Math.Round(body.ActualWidth, 1),
                rightColumnWidth = Math.Round(MainPanes.ColumnDefinitions[1].ActualWidth, 1),
                rightColumnDefinedWidth = MainPanes.ColumnDefinitions[1].Width.Value,
                collapsedLineVisible = CollapsedLine.Visibility == Visibility.Visible,
                collapsedLine = "● NEXUS" + CollapsedRamText.Text + CollapsedStatusText.Text,
                levelButtons = new { collapsed = LevelCollapsedButton.Background == VAccent2, half = LevelHalfButton.Background == VAccent2, full = LevelFullButton.Background == VAccent2 },
                chips = new { ram = RamChipText.Text, cpu = CpuChipText.Text, wal = WalChipText.Text, mode = ModeBadgeText.Text },
                fonts = new { ui = AiResponseText.FontFamily.Source, mono = MonoFontFamily.Source },
                thread = new
                {
                    pool = HistoryPanel.Children.Count,
                    shown = _threadShown,
                    max = ThreadMax,
                    localLines = _localLines.Count,
                    renders = _threadRenders,
                    items = _threadPool.Where(v => v.Item != null).Select(v => new { kind = v.Item!.Kind, title = v.Item.Title, result = v.Item.Result, badge = v.Item.Kind == "local" ? LocalBadgeText : null }).ToArray()
                },
                card = new
                {
                    time = CardTimeText.Text, question = CardQuestionText.Text, meta = CardMetaText.Text, qa = CardQaText.Text,
                    border = (DispatchCardBorder.BorderBrush as SolidColorBrush)?.Color.ToString(),
                    actionsVisible = CardActionsPanel.Visibility == Visibility.Visible,
                    chips = new[] { SuggestionChip1, SuggestionChip2, SuggestionChip3, SuggestionChip4 }.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Content?.ToString()).ToArray(),
                    progressVisible = ProgressPanel.Visibility == Visibility.Visible,
                    steps = new[] { StepDispatchText.Text, StepImageText.Visibility == Visibility.Visible ? StepImageText.Text : null, StepGeminiText.Text, StepQaText.Text },
                    elapsed = CardElapsedText.Visibility == Visibility.Visible ? CardElapsedText.Text : null,
                    cancelVisible = CardCancelButton.Visibility == Visibility.Visible
                },
                running = _running == null ? null : new { kind = _running.Kind, dispatchId = _running.DispatchId, seconds = Math.Round(_running.Watch.Elapsed.TotalSeconds, 2), cancellable = _running.Cts != null },
                runningTimerEnabled = _runningTimer?.IsEnabled ?? false,
                lastRunning = _lastRunning,
                lastCancel = _lastCancel,
                cancelledDispatches = _cancelledDispatchIds.Count,
                palette = PaletteState(),
                lazy = new { visible = LazyNextCard.Visibility == Visibility.Visible, reason = _lazyReason, title = LazyTitleText.Text, steps = new[] { LazyStep1.Text, LazyStep2.Text, LazyStep3.Text, LazyStep4.Text } },
                imageCardVisible = ImageHintVisible,
                lastImageAsk = _lastImageAsk,
                f1 = new { visible = CheatSheetPanel.Visibility == Visibility.Visible, switchesInF1 = IsInside(ToggleGameBoost, CheatSheetPanel) && IsInside(ToggleClickThrough, CheatSheetPanel) },
                rightColumn = new { pinsCount = PinsCountText.Text, timer = MfdTimerText.Text, allianceTask = AllianceTaskText.Text, allianceNext = AllianceNextText.Text },
                board = BoardState(),
                lastBoardCopy = _lastBoardCopy,
                mfdStripVisible = MfdInfoStrip.Visibility == Visibility.Visible
            };
        }

        private static bool IsInside(DependencyObject child, DependencyObject ancestor)
        {
            DependencyObject? d = child;
            while (d != null)
            {
                if (ReferenceEquals(d, ancestor)) return true;
                d = LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        /// <summary>
        /// TASK-16c selftest "ui-v2-visual": the real window class, never shown (Measure / Arrange at 1080 × 330 only). Checks
        /// the 2 columns (right 300 px), the 44 px top bar, the single 40 px line, the Card Pool (30 items → 20 views), the local
        /// "cục bộ · 0 token" line, the running card + Hủy (token cancelled, timer stopped), the "/" and "@" palettes, the lazy and
        /// image cards, the F1 switches and the alliance board. Leaves the window as it found it (then the caller closes it).
        /// </summary>
        public (object Report, bool Pass) RunVisualSelfCheck(IReadOnlyList<AgentProfile>? sampleAgents = null)
        {
            var checks = new Dictionary<string, bool>();
            var timings = new Dictionary<string, double>();
            double Lap(long t0) => Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            void Layout(double h)
            {
                ChromeBorder.Measure(new Size(1080, h));
                ChromeBorder.Arrange(new Rect(0, 0, 1080, h));
                ChromeBorder.UpdateLayout();
            }

            // E2 layout: Half
            ApplyLevel(CockpitLayout.Half, persist: false, "selftest", null);
            Layout(CockpitLayout.HalfHeight);
            checks["e2.twoColumns"] = MainPanes.ColumnDefinitions.Count == 2 && Math.Abs(MainPanes.ColumnDefinitions[1].ActualWidth - 300) < 0.6;
            checks["e2.topBar44"] = Math.Abs(TopBar.ActualHeight - 44) < 0.6;
            checks["e2.halfNoCollapsedLine"] = CollapsedLine.Visibility == Visibility.Hidden;
            checks["e2.fonts"] = AiResponseText.FontFamily.Source.Contains("Segoe UI Variable", StringComparison.Ordinal) &&
                                 MonoFontFamily.Source.Contains("Cascadia Mono", StringComparison.Ordinal);
            checks["e2.opaque"] = (ChromeBorder.Background as SolidColorBrush)?.Color.A >= 0xF8;

            // E2 collapsed: one line, body clipped (window 40 px, chrome stays at Half)
            ApplyLevel(CockpitLayout.Collapsed, persist: false, "selftest", null);
            checks["e2.collapsedOneLine"] = CollapsedLine.Visibility == Visibility.Visible && Math.Abs(Height - CockpitLayout.CollapsedHeight) < 0.6 &&
                                            Math.Abs(ChromeBorder.Height - CockpitLayout.HalfHeight) < 0.6 && MainPanes.Visibility == Visibility.Visible &&
                                            CollapsedRamText.Text.Contains("RAM", StringComparison.Ordinal);
            ApplyLevel(CockpitLayout.Half, persist: false, "selftest", null);
            checks["e2.levelButtons"] = LevelHalfButton.Background == VAccent2 && LevelCollapsedButton.Background != VAccent2;

            // E2 Card Pool: 30 history answers + local lines → never more than 20 views
            var fake = Enumerable.Range(1, 30).Select(i => new RecentDispatch(900000 + i, DateTime.UtcNow.AddMinutes(-60 + i).ToString("o"), "Micro", "Squad1",
                "Backend Architect", "Reality Checker", $"Câu hỏi thử {i}", "PASSED", 0, string.Empty, DateTime.UtcNow.AddMinutes(-60 + i).ToString("o"),
                $"Trả lời **{i}** với `code` ```cs\nvar x = {i};\n```", "gemini-mock-verifier")).ToList();
            List<ThreadItem> savedLocal = _localLines.ToList();
            long tPool = Stopwatch.GetTimestamp();
            _threadSourceOverride = fake;
            RenderThread();
            timings["threadRender30Ms"] = Lap(tPool);
            int poolAfter30 = HistoryPanel.Children.Count;
            RenderThread();
            checks["e2.cardPool20"] = poolAfter30 == ThreadMax && HistoryPanel.Children.Count == ThreadMax && _threadShown == ThreadMax &&
                                      _threadPool[ThreadMax - 1].Item?.Title == "Câu hỏi thử 30";
            string later = DateTime.UtcNow.AddMinutes(5).ToString("o");
            var localSnap = new LatestDispatchSnapshot(LatestDispatchFile.LocalUtilityEvent, "selftest", 0, "PASSED", 0, string.Empty, later, later,
                "/calc sqrt(144) + 2^8", "Micro", "Local", null, new AgentName("local-calc", "LocalCalculator"), new AgentName("-", "-"), "-", null, null, null,
                AiResponse: "[CALC] sqrt(144) + 2^8 = 268 (Hex: 0x10C | Bin: 0b100001100)", AiModel: "local-calc", LocalUtility: "calc");
            AddLocalLine(localSnap);
            ThreadView? lastView = _threadPool.LastOrDefault(v => v.Item != null);
            checks["e2.localLine"] = lastView?.Item?.Kind == "local" && lastView.Badge.Visibility == Visibility.Visible &&
                                     lastView.Result.Text.StartsWith("sqrt(144) + 2^8 = 268", StringComparison.Ordinal) && HistoryPanel.Children.Count == ThreadMax;
            _threadSourceOverride = null;
            _localLines.Clear();
            _localLines.AddRange(savedLocal);
            RenderThread();

            // E2 running card + Hủy Esc (decision 3)
            var cts = new CancellationTokenSource();
            var never = new System.Threading.Tasks.TaskCompletionSource<ConsultOutcome>();
            var fakeResult = new CockpitCommandResult("ask", true, "[ASK #0] selftest", null, null, 1.5, AiTask: never.Task, Cancellation: cts);
            BeginRunning(fakeResult, "/ask selftest Hủy");
            bool runningShown = ProgressPanel.Visibility == Visibility.Visible && CardCancelButton.Visibility == Visibility.Visible &&
                                (_runningTimer?.IsEnabled ?? false) && (DispatchCardBorder.BorderBrush as SolidColorBrush)?.Color == Color.FromRgb(0x38, 0xBD, 0xF8);
            long tCancel = Stopwatch.GetTimestamp();
            bool cancelled = CancelRunning("selftest");
            timings["cancelMs"] = Lap(tCancel);
            checks["e2.runningCard"] = runningShown;
            checks["e2.cancelEsc"] = cancelled && cts.IsCancellationRequested && _running == null && !(_runningTimer?.IsEnabled ?? false) &&
                                     CardQaText.Text == "[ĐÃ HỦY]" && CardCancelButton.Visibility == Visibility.Collapsed &&
                                     _shownAnswerText.StartsWith("[ĐÃ HỦY]", StringComparison.Ordinal);
            never.TrySetCanceled();

            // E3 palettes
            long tSlash = Stopwatch.GetTimestamp();
            CommandInputBox.Text = "/";
            timings["slashOpenMs"] = Lap(tSlash);
            bool slashOpen = PaletteOpen && _paletteFills.Count >= 10;
            _recentCommands.Clear();
            CockpitPalette.NoteRecent(_recentCommands, "/img lỗi gì?");
            CommandInputBox.Text = "/c";
            CommandInputBox.Text = "/ca";
            bool calcFirst = _paletteFills.Count > 0 && _paletteFills[0] == "/calc";
            PaletteAccept("selftest");
            checks["e3.slashPalette"] = slashOpen && calcFirst && CommandInputBox.Text == "/calc " && !PaletteOpen;
            CommandInputBox.Text = "/i";
            bool recentShown = _paletteRows.Any(r => r.Root.Visibility == Visibility.Visible && r.A.Text == "VỪA DÙNG");
            checks["e3.recentGroup"] = recentShown && _paletteFills.FirstOrDefault() == "/img";
            CommandInputBox.Text = string.Empty;
            IReadOnlyList<AgentProfile> agents = sampleAgents ?? Array.Empty<AgentProfile>();
            IReadOnlyList<PaletteAgent> sec = CockpitPalette.FilterAgents(agents, "sec", 8);
            checks["e3.agentFilter"] = agents.Count == 0 || (sec.Count > 0 && sec.All(a =>
                a.Name.Contains("sec", StringComparison.OrdinalIgnoreCase) || a.Id.Contains("sec", StringComparison.OrdinalIgnoreCase) || a.Division.Contains("sec", StringComparison.OrdinalIgnoreCase)));
            CommandInputBox.Text = "@";
            bool agentPalette = PaletteOpen && _paletteQuery?.Mode == CockpitPalette.AgentMode;
            CommandInputBox.Text = string.Empty;
            checks["e3.agentPaletteOpens"] = agentPalette && !PaletteOpen;

            // E3 lazy card + image card + alliance board + F1 switches
            AllianceSnapshot a = AllianceStatus.Read(_runtime.HandoffDirectory, _runtime.ConfigDirectory);
            ShowLazyNextStep(a, "selftest");
            bool lazyShown = LazyNextCard.Visibility == Visibility.Visible && LazyTitleText.Text.Length > 0 && LazyStep1.Text.Length > 0;
            CopyAllianceNextStep("selftest");
            checks["e3.lazyNextStep"] = lazyShown && LazyNextCard.Visibility == Visibility.Collapsed;
            ShowImageHint();
            bool imageShown = ImageHintVisible;
            ResetCommandHint();
            _imageHintShown = true;
            ResetCommandHint();
            checks["e3.imageCard"] = imageShown && !ImageHintVisible;
            bool boardWasOpen = AlliancePanel.Visibility == Visibility.Visible;
            if (!boardWasOpen) ToggleAlliancePanel("selftest");
            bool boardFilled = AlliancePanel.Visibility == Visibility.Visible && AllianceGateTitleText.Text.StartsWith("Gate", StringComparison.Ordinal) &&
                               _lastBoard != null && _lastBoard.Checks.Count == 5;
            string g = CopyAllianceText("gemini", "selftest");
            string k = CopyAllianceText("prompt", "selftest");
            checks["e3.allianceBoard"] = boardFilled && g.Length > 0 && k.Contains("Verify-Task", StringComparison.Ordinal) && ClipboardService.LastCopiedText == k;
            ToggleAlliancePanel("selftest");
            ToggleCheatSheet("selftest");
            checks["e3.f1Switches"] = CheatSheetPanel.Visibility == Visibility.Visible && IsInside(ToggleGameBoost, CheatSheetPanel) &&
                                      IsInside(ToggleClickThrough, CheatSheetPanel) && IsInside(ToggleVoicePtt, CheatSheetPanel);
            ToggleCheatSheet("selftest");
            Layout(CockpitLayout.HalfHeight);

            bool pass = checks.Values.All(v => v);
            return (new
            {
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                timings,
                sampleAgents = agents.Count,
                agentHits = sec.Select(x => x.Name).ToArray(),
                visual = VisualState()
            }, pass);
        }
    }
}
