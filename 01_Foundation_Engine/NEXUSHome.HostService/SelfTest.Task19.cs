using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using NEXUSHome.CockpitUI;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-19 (ADR-74) Alt+V voice to the Cockpit command box ─────────────────────────────

        /// <summary>
        /// TASK-19 E2 "voice-input": the Cockpit window (off screen), /ptt reading a 3 s replayed source instead of the microphone
        /// and a stand-in recognizer that answers the Vietnamese sample. ToggleVoiceInput (what Alt+V calls) → "● REC" →
        /// ToggleVoiceInput again after ~60 ms (stop early) → the text is in the command box, caret at the end, "● REC" gone,
        /// whole flow &lt;= 300 ms, PCM ledger 0 B. 0 token, no microphone, no network.
        /// </summary>
        private static (object Report, bool Pass) VoiceInput(NexusRuntime runtime)
        {
            var (checks, report) = VoiceFlow(runtime, VietnameseSample, VietnameseSample, 300);
            bool pass = checks.Values.All(v => v);
            return (new { scenario = "voice-input", pass, failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(), checks, report, tokens = 0 }, pass);
        }

        /// <summary>
        /// TASK-19 / 20: Alt+V end to end in the Cockpit window (off screen): /ptt reads a 3 s replayed source instead of the
        /// microphone, a stand-in recognizer answers <paramref name="spoken"/>. ToggleVoiceInput (what Alt+V calls) → "● REC" →
        /// ToggleVoiceInput again after ~60 ms (stop early) → <paramref name="expectedBox"/> in the command box, caret at the end,
        /// "● REC" gone, the whole flow &lt;= <paramref name="budgetMs"/>, PCM ledger 0 B. 0 token, no microphone, no network.
        /// </summary>
        private static (Dictionary<string, bool> Checks, object? Report) VoiceFlow(NexusRuntime runtime, string spoken, string expectedBox, double budgetMs)
        {
            var checks = new Dictionary<string, bool>();
            var w = new CockpitShellWindow(runtime);
            w.WarmUpHidden();
            w.Left = -4000;
            w.Top = 0;
            w.ShowActivated = false;
            w.Show();
            w.ApplyUiFromPipe("full");
            Pump(300);
            ISpeechToTextProvider oldStt = runtime.Mic.Stt;
            short[] voice = Enumerable.Range(0, 48000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0))).ToArray();
            runtime.Mic.Stt = new StandInRecognizer(spoken);
            w.SetPttSourceOverride(() => new ArrayPcmSource(voice));
            object? report = null;
            // A key press runs inside WPF's DispatcherSynchronizationContext, so "await" in AwaitLocalAsync comes back to the UI
            // thread. This selftest calls ToggleVoiceInput directly: install the same context (CI 28506ef: without it the result
            // continued on the thread pool and never reached the command box).
            System.Threading.SynchronizationContext? oldContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try
            {
                var flow = Stopwatch.StartNew();
                string first = w.ToggleVoiceInput("Alt+V");
                VoiceInputSnapshot recording = w.VoiceInputState();
                Pump(60);
                string second = w.ToggleVoiceInput("Alt+V");
                VoiceInputSnapshot end = w.VoiceInputState();
                while (end.Active && flow.ElapsedMilliseconds < 3000)
                {
                    Pump(5);
                    end = w.VoiceInputState();
                }
                flow.Stop();
                PttResult? session = runtime.Mic.LastSession;
                checks["recStarted"] = first == "recording" && recording.Active && recording.RecIndicator;
                checks["stopEarly"] = second == "stopping" && session != null && session.CapturedMs < 3000;
                checks["textInBox"] = end.CommandText == expectedBox;
                checks["caretAtEnd"] = end.CaretAtEnd;
                checks["recGone"] = !end.Active && !end.RecIndicator;
                checks["withinBudget"] = end.LastMs is double ms && ms <= budgetMs;
                checks["pcmZero"] = session != null && session.OutstandingBytesAfter == 0 && PcmBufferLedger.OutstandingBytes == 0;
                report = new { spoken, expectedBox, budgetMs, first, second, recording, end, flowMs = flow.Elapsed.TotalMilliseconds, session };
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(oldContext);
                runtime.Mic.Stt = oldStt;
                w.SetPttSourceOverride(null);
                try { w.Close(); } catch (InvalidOperationException) { }
            }
            return (checks, report);

            static void Pump(int ms)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
        }

        // ───────────────────────────── TASK-20 (ADR-75) spoken local commands ─────────────────────────────

        /// <summary>
        /// TASK-20 E2 + E3 "voice-command-map": VoiceCommandMapper on spoken commands with and without diacritics (and with
        /// politeness words / punctuation) → the "/" command; free questions → null (kept as said). Each call &lt;= 1,0 ms and the
        /// whole table &lt;= 5,0 ms after one warm-up call (the cold call is reported). Then Alt+V end to end with the stand-in
        /// recognizer saying "Kiểm tra mạng." → the command box holds "/net" &lt;= 250 ms, PCM 0 B.
        /// </summary>
        private static (object Report, bool Pass) VoiceCommandMap(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            (string Spoken, string? Expected)[] table =
            {
                ("Kiểm tra mạng", "/net"), ("kiem tra mang", "/net"), ("Radar mạng.", "/net"), ("NEXUS, quét mạng đi!", "/net"),
                ("Kiểm tra cổng 8080", "/net port 8080"), ("kiem tra cong 5055", "/net port 5055"), ("Check port 443", "/net port 443"),
                ("Trạng thái mic", "/mic status"), ("trang thai micro", "/mic status"),
                ("Ngữ cảnh", "/ctx"), ("ngu canh", "/ctx"),
                ("Hẹn giờ 5 phút", "/timer 5m"), ("hen gio muoi phut", "/timer 10m"), ("Đặt hẹn giờ 90 giây nhé", "/timer 90s"),
                ("Báo cáo QA", "/qa"), ("bao cao qa", "/qa"), ("Hủy hẹn giờ", "/timer cancel all"),
                // free speech stays as said
                ("Xin chào NEXUS, hôm nay trời thế nào?", null), ("Giải thích giúp tôi Span<T> trong C#", null),
                ("mạng của tôi hôm qua bị chậm, tại sao vậy", null), ("Kiểm tra cổng tám nghìn", null), ("Hẹn giờ cho buổi họp chiều mai", null),
                ("Cộng 5", null), ("Hẹn giờ 30 tiếng", null),
                (VietnameseSample, null)
            };
            var cold = Stopwatch.StartNew();
            VoiceCommandMapper.TryMap("kiểm tra mạng");
            cold.Stop();
            // warm every path once (exact phrase, port, timer, no match) so the timed rows measure the mapper, not first-call JIT
            // (review 2026-11-12: on CI the first port row took 0,56 ms, every other row <= 0,0075 ms)
            foreach (string warm in new[] { "kiem tra cong 1", "hen gio 1 phut", "xin chao", "port 1" }) VoiceCommandMapper.TryMap(warm);
            var rows = new List<object>();
            double total = 0, worst = 0;
            int mapped = 0, kept = 0, wrong = 0;
            foreach (var (spoken, expected) in table)
            {
                long t0 = Stopwatch.GetTimestamp();
                VoiceCommandMatch? m = VoiceCommandMapper.TryMap(spoken);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                total += ms;
                worst = Math.Max(worst, ms);
                bool ok = m?.Command == expected;
                if (!ok) wrong++;
                else if (expected != null) mapped++;
                else kept++;
                rows.Add(new { spoken, expected, got = m?.Command, rule = m?.Rule, ms = Math.Round(ms, 4), ok });
            }
            checks["e2.allCorrect"] = wrong == 0;
            checks["e2.atLeast8Mapped"] = mapped >= 8;
            checks["e2.freeSpeechKept"] = kept >= 7;
            checks["e2.eachUnder1ms"] = worst <= 1.0;
            checks["e2.totalUnder5ms"] = total <= 5.0;
            var (flowChecks, flowReport) = VoiceFlow(runtime, "Kiểm tra mạng.", "/net", 250);
            foreach (var kv in flowChecks) checks["e3." + kv.Key] = kv.Value;
            bool pass = checks.Values.All(v => v);
            return (new
            {
                scenario = "voice-command-map",
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                table = new { mapped, kept, wrong, totalMs = Math.Round(total, 4), worstMs = Math.Round(worst, 4), coldFirstCallMs = Math.Round(cold.Elapsed.TotalMilliseconds, 3), rows },
                flow = flowReport,
                tokens = 0
            }, pass);
        }

        /// <summary>TASK-19: a recognizer that answers a fixed text the way LocalSttService does (recognized-text prefix).</summary>
        private sealed class StandInRecognizer : ISpeechToTextProvider
        {
            private readonly string _text;

            public StandInRecognizer(string text) => _text = text;

            public string Name => "StandInRecognizer (selftest)";

            public bool NeedsAudio => false;

            public string Transcribe(VadSummary vad, ReadOnlySpan<short> audio) => LocalSttService.TextPrefix + _text;
        }
    }
}
