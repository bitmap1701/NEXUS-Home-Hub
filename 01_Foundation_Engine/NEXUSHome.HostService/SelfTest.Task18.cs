using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-18 (ADR-71) local STT for /ptt ─────────────────────────────

        // About 2 s of speech = the default /ptt session (MicService.DefaultTestSeconds). SAPI recognizes ~3,3x faster than real
        // time, so the time grows with the audio: CI 987135e, a 4,47 s sample took 1758 ms (load 265 + recognize 1373).
        private const string PttSttSample = "Hello world, this is a test.";

        /// <summary>
        /// TASK-18 E2 "ptt-stt" (no microphone, no network, 0 token):
        ///   a  1 s of digital silence → VAD = False, the provider returns without starting the worker (&lt;= 5 ms)
        ///   b  an installed SAPI voice (worker "synth") reads <see cref="PttSttSample"/> into 16 kHz PCM, replayed through the same
        ///      MicService session as /ptt → VAD = True → worker "recognize" → non-empty text in &lt;= 1500 ms, PCM ledger 0 B,
        ///      this process grows &lt;= 2,5 MB (the recognizer lives and dies in the child)
        ///   c  NEXUS_STT_SIMULATE_MISSING = lang / worker → safe fallback text, no exception, &lt;= 300 ms
        /// On the CI runner (no SAPI recognizer / voice installed) b reports "runner has no SAPI" instead of failing.
        /// </summary>
        private static (object Report, bool Pass) PttStt(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            bool ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
            var stt = new LocalSttService();
            var mic = new MicService(new CockpitUiStore(Path.GetTempPath()), stt);
            string? oldSimulate = Environment.GetEnvironmentVariable(LocalSttService.SimulateMissingVariable);
            Environment.SetEnvironmentVariable(LocalSttService.SimulateMissingVariable, null);
            object? speechReport = null, synthReport = null;
            LocalSttResult? silenceResult = null;
            var fallbacks = new List<object>();
            try
            {
                // a: silence
                PttResult silence = mic.Run(new SyntheticPcmSource("silence"), "test", 1, CancellationToken.None);
                LocalSttResult? s = stt.Last;
                silenceResult = s;
                checks["a.silenceNoVad"] = silence.Ok && !silence.Vad.Vad && s is { Outcome: "silence" };
                checks["a.skipUnder5ms"] = s != null && s.TotalMs <= 5.0;
                checks["a.ledgerZero"] = silence.OutstandingBytesAfter == 0;

                // b: speech sample from an installed voice, replayed like a /ptt session
                var (samples, synthReply, synthError) = SttWorkerClient.Synthesize(PttSttSample);
                synthReport = new { ok = samples != null, samples = samples?.Length ?? 0, seconds = Math.Round((samples?.Length ?? 0) / 16000.0, 2), voice = synthReply?.Engine, error = synthError, missing = synthReply?.Missing };
                if (samples != null)
                {
                    long? before = SettledPrivateWs();
                    PttResult speech = mic.Run(new ArrayPcmSource(samples), "test", samples.Length / 16000.0 + 0.2, CancellationToken.None);
                    LocalSttResult? r = stt.Last;
                    long? after = SettledPrivateWs();
                    double? growthMb = before.HasValue && after.HasValue ? Math.Round((after.Value - before.Value) / 1048576.0, 2) : null;
                    string? text = LocalSttService.RecognizedText(speech.Transcript);
                    bool runnerWithoutSapi = ci && r is { Outcome: "fallback" };
                    checks["b.speechVad"] = speech.Ok && speech.Vad.Vad;
                    checks["b.textNotEmpty"] = runnerWithoutSapi || !string.IsNullOrWhiteSpace(text);
                    checks["b.under1500ms"] = runnerWithoutSapi || (r != null && r.TotalMs <= 1500);
                    checks["b.ledgerZero"] = speech.OutstandingBytesAfter == 0 && PcmBufferLedger.OutstandingBytes == 0;
                    checks["b.hostGrowthUnder2_5Mb"] = growthMb is double g && g <= 2.5;
                    speechReport = new { text, transcript = speech.Transcript, stt = r, vad = speech.Vad, growthMb, runnerWithoutSapi, speech.OutstandingBytesAfter };
                }
                else
                {
                    checks["b.sample"] = ci; // a PC without any SAPI voice cannot make the sample (TASK-18 probe: David + Zira en-US)
                }

                // c: missing language pack / worker → fallback, no exception
                short[] loud = samples ?? Enumerable.Range(0, 16000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0))).ToArray();
                foreach (string what in new[] { "lang", "worker" })
                {
                    Environment.SetEnvironmentVariable(LocalSttService.SimulateMissingVariable, what);
                    PttResult f = mic.Run(new ArrayPcmSource(loud), "test", loud.Length / 16000.0 + 0.2, CancellationToken.None);
                    LocalSttResult? fr = stt.Last;
                    checks[$"c.{what}Fallback"] = f.Ok && fr is { Outcome: "fallback" } && fr.TotalMs <= 300 && LocalSttService.RecognizedText(f.Transcript) == null;
                    fallbacks.Add(new { simulate = what, transcript = f.Transcript, stt = fr });
                }
                checks["c.status"] = stt.DescribeStatus().Contains("worker", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                checks["noException"] = false;
                speechReport ??= new { exception = ex.ToString() };
            }
            finally
            {
                Environment.SetEnvironmentVariable(LocalSttService.SimulateMissingVariable, oldSimulate);
            }
            bool pass = checks.Values.All(v => v);
            return (new
            {
                scenario = "ptt-stt",
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                runner = ci ? "github-actions" : "pc",
                sample = PttSttSample,
                silence = silenceResult,
                synth = synthReport,
                speech = speechReport,
                fallbacks,
                tokens = 0
            }, pass);

            static long? SettledPrivateWs()
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                return MemoryDiagnostics.PrivateWorkingSetBytes();
            }
        }

        /// <summary>TASK-18: replays a managed 16 kHz PCM array in 100 ms chunks, like the waveIn source (no unmanaged buffer).</summary>
        private sealed class ArrayPcmSource : IPcmSource
        {
            private readonly short[] _samples;

            public ArrayPcmSource(short[] samples) => _samples = samples;

            public string Name => "ArrayPcmSource (selftest)";

            public PcmCaptureStats Capture(TimeSpan duration, CancellationToken cancel, PcmChunkHandler onSamples)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int max = (int)Math.Min(_samples.Length, duration.TotalSeconds * 16000);
                int done = 0, buffers = 0;
                while (done < max && !cancel.IsCancellationRequested)
                {
                    int n = Math.Min(1600, max - done);
                    onSamples(_samples.AsSpan(done, n));
                    done += n;
                    buffers++;
                }
                double ms = Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3);
                return new PcmCaptureStats(true, null, done, buffers, 3200, 0, ms, 0);
            }
        }
    }
}
