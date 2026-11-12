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
            runtime.Mic.Stt = new StandInRecognizer(VietnameseSample);
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
                checks["textInBox"] = end.CommandText == VietnameseSample;
                checks["caretAtEnd"] = end.CaretAtEnd;
                checks["recGone"] = !end.Active && !end.RecIndicator;
                checks["under300ms"] = end.LastMs is double ms && ms <= 300;
                checks["pcmZero"] = session != null && session.OutstandingBytesAfter == 0 && PcmBufferLedger.OutstandingBytes == 0;
                report = new { first, second, recording, end, flowMs = flow.Elapsed.TotalMilliseconds, session };
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(oldContext);
                runtime.Mic.Stt = oldStt;
                w.SetPttSourceOverride(null);
                try { w.Close(); } catch (InvalidOperationException) { }
            }
            bool pass = checks.Values.All(v => v);
            return (new { scenario = "voice-input", pass, failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(), checks, report, tokens = 0 }, pass);

            static void Pump(int ms)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
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
