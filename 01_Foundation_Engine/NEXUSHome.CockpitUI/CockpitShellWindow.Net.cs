using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NEXUSHomeHub.Core;

namespace NEXUSHome.CockpitUI
{
    /// <summary>
    /// TASK-17 (ADR-67) on the 2-column Cockpit of Gate 16c (Architect decision 3.8, 2026-11-12):
    ///   /net, /net status        → one Radar card in the conversation thread
    ///   /net port, /net ping     → one local line ("cục bộ · 0 token")
    ///   right column             → static "⚡ NITRO5 (.22)" line under the timer (changes only when a scan ends: 0 % idle CPU)
    ///   Alt+Q                    → the top of the right column flips THẺ ĐÃ GHIM n/5 ↔ MẠNG // /NET (latest scan)
    ///   F1 "3. Mic 16kHz VAD"    → one microphone stream while ticked (at most 30 s), VAD result as a local line
    /// Nothing here runs a timer or a loop; background work is the /net and /ptt tasks of the command processor.
    /// </summary>
    public partial class CockpitShellWindow
    {
        private bool _rightNetPage;
        private int _rightFlips;
        private int _localTasksDone;
        private string? _lastLocalTaskError;
        private int _radarCardsAdded;
        private object? _lastPttUi;

        /// <summary>A /net status, /net ping or /ptt result finished on a pool thread: back on the UI thread as a thread item.</summary>
        private async Task AwaitLocalAsync(Task<LocalUtilityResult> task)
        {
            LocalUtilityResult r;
            try
            {
                r = await task;
            }
            catch (Exception ex) // a background /net or /ptt failure is shown, never thrown on the UI thread
            {
                _lastLocalTaskError = ex.GetType().Name + ": " + ex.Message;
                AppendOutput($"[LOCAL] Lỗi nền: {_lastLocalTaskError}");
                return;
            }
            _localTasksDone++;
            AppendOutput(r.Message);
            if (r.Snapshot != null) ApplySnapshot(r.Snapshot);
            if (r.Utility == LocalUtilityService.Net) UpdateNetPanel();
            if (r.Utility == LocalUtilityService.Ptt)
            {
                _lastPttUi = new { r.Ok, r.Action, message = r.Message, atUtc = DateTime.UtcNow.ToString("o") };
                if (ToggleVoicePtt.IsChecked == true)
                {
                    _pttToggleQuiet = true; // the stream ended (30 s or an error): untick without starting a new one
                    ToggleVoicePtt.IsChecked = false;
                    _pttToggleQuiet = false;
                }
            }
            ArmIdleTrim();
        }

        private bool _pttToggleQuiet;

        /// <summary>F1 switch "3. Mic 16kHz VAD Stream": tick = open the chosen mic until unticked (max 30 s); untick = stop.</summary>
        private void OnVoicePttToggled()
        {
            if (_pttToggleQuiet) return;
            if (ToggleVoicePtt.IsChecked == true)
            {
                if (_runtime.Mic.Busy)
                {
                    AppendOutput("[PTT] Đang có một phiên thu âm khác.");
                    _pttToggleQuiet = true;
                    ToggleVoicePtt.IsChecked = false;
                    _pttToggleQuiet = false;
                    return;
                }
                CockpitCommandResult result = _commandProcessor.StartPtt(MicService.MaxSessionSeconds, "stream", "/ptt stream (F1)");
                AppendOutput(result.Message);
                if (result.LocalTask != null) _ = AwaitLocalAsync(result.LocalTask);
            }
            else
            {
                _commandProcessor.StopPtt();
            }
        }

        /// <summary>Right column: the static NITRO5 line and the MẠNG // /NET page from the newest scan (property sets only).</summary>
        private void UpdateNetPanel()
        {
            NetRadarSnapshot? s = _runtime.Network.Last;
            Nitro5LineText.Text = NetworkRadar.Nitro5Line(s);
            Nitro5LineText.Foreground = s == null ? VMuted : s.Nitro5Address != null ? VGreen : VAmber;
            NetPageText.Text = s == null ? "Chưa có lần dò nào: gõ /net" : NetworkRadar.Describe(s);
            _netPageReleased = false;
        }

        /// <summary>Alt+Q (global hotkey of HostService, only while the Cockpit is visible) and pipe RIGHTPAGE.</summary>
        public string FlipRightTop(string source)
        {
            _rightNetPage = !_rightNetPage;
            _rightFlips++;
            ApplyRightTopPage();
            AppendOutput($"[{DateTime.Now:HH:mm:ss}] {source}: cột phải → {(_rightNetPage ? "MẠNG // /NET" : "THẺ ĐÃ GHIM")}.");
            return _rightNetPage ? "net" : "pins";
        }

        private void ApplyRightTopPage()
        {
            if (_rightNetPage) UpdateNetPanel(); // TASK-17a: also rebuilds the page text released by the idle trim
            RightTopLabel.Text = _rightNetPage ? "MẠNG // /NET" : "THẺ ĐÃ GHIM";
            PinsCountText.Visibility = _rightNetPage ? Visibility.Collapsed : Visibility.Visible;
            WidgetIndicatorText.Visibility = _rightNetPage ? Visibility.Collapsed : Visibility.Visible;
            NetPagePanel.Visibility = _rightNetPage ? Visibility.Visible : Visibility.Collapsed;
            PinsScroll.Visibility = _rightNetPage ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>UISTATE "task17" block (Verify-Task17 E2 / E3 / E5).</summary>
        private object Task17State()
        {
            NetRadarSnapshot? s = _runtime.Network.Last;
            return new
            {
                rightTopPage = _rightNetPage ? "net" : "pins",
                rightTopLabel = RightTopLabel.Text,
                rightFlips = _rightFlips,
                nitro5Line = Nitro5LineText.Text,
                netPageText = NetPageText.Text,
                netPageVisible = NetPagePanel.Visibility == Visibility.Visible,
                pinsVisible = PinsScroll.Visibility == Visibility.Visible,
                radarCardsAdded = _radarCardsAdded,
                lastRadar = s == null ? null : new
                {
                    s.AtUtc, nic = s.ActiveNic?.Name, ipv4 = s.ActiveNic?.IPv4, gateway = s.ActiveNic?.Gateway,
                    skippedVirtual = s.SkippedVirtual.Count, s.Nitro5State, s.Nitro5Address, s.Nitro5RttMs, s.PingMs, s.TotalMs,
                    pings = s.Pings, s.TcpListening, s.TcpEstablished
                },
                localTasksDone = _localTasksDone,
                lastLocalTaskError = _lastLocalTaskError,
                mic = new { busy = _runtime.Mic.Busy, toggle = ToggleVoicePtt.IsChecked == true, lastPtt = _lastPttUi, outstandingBytes = PcmBufferLedger.OutstandingBytes },
                clipboard = new { ClipboardService.LastWorkerTimedOut, ClipboardService.WorkerTimeouts, ClipboardService.LastWriteMs },
                idleTrim = IdleTrimState(), // TASK-17a
                targetsSource = s?.TargetsSource
            };
        }

        // ───────────────────────── TASK-17a (ADR-69) M1a: what the idle [MEM] trim releases in the UI ─────────────────────────

        /// <summary>Card rows kept after the idle trim (the full 8 come back on the next wake, at Background priority).</summary>
        public const int ThreadIdlePool = 4;

        /// <summary>
        /// Diagnostics / A/B on Nhat Hoa's PC (Measure-CockpitFootprint, selftest ui-footprint trimX): which UI trims the idle
        /// [MEM] pass makes. "all" (default) | "off" | any of "net", "pool", "live" joined with '+'.
        /// </summary>
        public const string IdleTrimVariable = "NEXUS_UI_IDLETRIM";

        private bool _threadIdleShrunk;
        private bool _liveReleased;
        private bool _netPageReleased;
        private int _idleUiTrims;
        private object? _lastIdleUiTrim;

        private static bool IdleTrimEnabled(string part)
        {
            string mode = (Environment.GetEnvironmentVariable(IdleTrimVariable) ?? "all").Trim().ToLowerInvariant();
            if (mode is "" or "all") return true;
            if (mode == "off") return false;
            return Array.IndexOf(mode.Split('+'), part) >= 0;
        }

        /// <summary>
        /// Called by the idle [MEM] pass just before the GC (never on the WAKE path):
        ///   net  — the MẠNG // /NET page text when that page is not open (rebuilt from the last scan on Alt+Q);
        ///   pool — a visible PASSIVE Cockpit keeps the newest 4 card rows, the other views leave the panel and the pool;
        ///   live — a hidden Cockpit drops the rendered answer (inlines of the open card); the text stays and is rendered
        ///          again at Background priority when the Cockpit shows.
        /// </summary>
        private void ApplyIdleUiTrim()
        {
            bool net = false, pool = false, live = false;
            if (IdleTrimEnabled("net") && !_rightNetPage && NetPageText.Text.Length > 0)
            {
                NetPageText.Text = string.Empty;
                _netPageReleased = net = true;
            }
            if (IdleTrimEnabled("pool") && Visibility == Visibility.Visible && !_commandMode && !_threadExpanded && !_threadReleasedWhileHidden &&
                !_threadIdleShrunk && _threadPool.Count > ThreadIdlePool)
            {
                _threadIdleShrunk = pool = true;
                RenderThread();
            }
            if (IdleTrimEnabled("live") && Visibility != Visibility.Visible && !_liveReleased && AiResponseText.Inlines.Count > 0)
            {
                if (!ReferenceEquals(AiResponseScroll.Content, AiResponseText)) AiResponseScroll.Content = AiResponseText; // a history view goes too
                _historyIndex = -1;
                AiResponseText.Inlines.Clear();
                _liveMarkdown = null;
                _liveReleased = live = true;
            }
            if (net || pool || live) _idleUiTrims++;
            _lastIdleUiTrim = new { net, pool, live, pool_rows = _threadPool.Count, atUtc = DateTime.UtcNow.ToString("o") };
        }

        /// <summary>The open card shows the live answer again (rendered once; a no-op when it was not released).</summary>
        private void EnsureLiveRendered()
        {
            if (!_liveReleased) return;
            _liveReleased = false;
            _liveMarkdown = LightweightMarkdownFormatter.Render(AiResponseText, _liveAnswerText);
            if (_historyIndex < 0) ShowAnswerView(AiResponseText, _liveAnswerText, _liveMarkdown, _liveHasCode);
        }

        /// <summary>After a wake (Background priority, outside the WAKE timer): 8 card rows and the answer view come back.</summary>
        private void UndoIdleUiTrim()
        {
            EnsureLiveRendered();
            if (_threadIdleShrunk)
            {
                _threadIdleShrunk = false;
                RenderThread();
            }
        }

        private object IdleTrimState() => new
        {
            mode = Environment.GetEnvironmentVariable(IdleTrimVariable) ?? "all",
            poolShrunk = _threadIdleShrunk,
            liveReleased = _liveReleased,
            netPageReleased = _netPageReleased && NetPageText.Text.Length == 0,
            pool = _threadPool.Count,
            trims = _idleUiTrims,
            last = _lastIdleUiTrim
        };

        /// <summary>Pipe "PTTSTOP" / selftest: ends a running stream.</summary>
        public void StopPttFromPipe() => _commandProcessor.StopPtt();

        /// <summary>Called once the window exists (Loaded): the NITRO5 line shows "chưa dò" until the first /net.</summary>
        private void InitNetPanel() => Dispatcher.InvokeAsync(UpdateNetPanel, DispatcherPriority.Background);
    }
}
