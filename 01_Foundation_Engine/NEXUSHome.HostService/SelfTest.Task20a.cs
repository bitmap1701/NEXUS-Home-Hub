using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-20a (ADR-72 / 73) STT timeouts from real measurements ─────────────────────────────

        /// <summary>
        /// TASK-20a "stt-timeouts" (loopback only, 0 token). Real Nitro 5 numbers (2026-11-14): whisper small on CPU takes 2,4–3,6 s
        /// for a 3 s phrase; the old fixed 2000 ms (PC) / 3000 ms (worker) always fell back to SAPI.
        ///   E2a "McpTools --stt-worker serve" (default --timeout-ms 12000) with a stand-in CLI that answers the Vietnamese sample
        ///       after 2500 ms; LocalSttService with lanTimeoutMs 10000 → the Vietnamese UTF-8 text (provider lan), PCM 0 B
        ///   E2b the same slow worker with lanTimeoutMs 1000 → the PC stops waiting on time and falls back to SAPI:
        ///       lan step &lt;= 1000 + 350 ms, FallbackMs &lt;= 1350 ms, no exception, PCM 0 B
        ///   E3  "stt.lanTimeoutMs" in nexus_config.json + Reload: 4000 kept, 500 → 1000 and 99999 → 30000 with a warning,
        ///       "abc" / missing → 10000; each Reload &lt;= 50 ms; /ptt status (DescribeStatus) shows "lanTimeoutMs 4000"
        /// </summary>
        private static (object Report, bool Pass) SttTimeouts(NexusRuntime runtime)
        {
            const int SlowMs = 2500;
            var checks = new Dictionary<string, bool>();
            var steps = new Dictionary<string, object?>();
            string? exe = VisionWorkerClient.ResolvePath();
            string tempDir = Path.GetTempPath();
            var logs = new ConcurrentQueue<string>();
            Process? worker = null;
            short[] voice = Enumerable.Range(0, 24000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0))).ToArray();
            string configDir = Path.Combine(tempDir, "nexus_task20a_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // E3 first (no process needed): the config field and its clamping
                Directory.CreateDirectory(configDir);
                var config = new NexusConfigService(Path.Combine(configDir, NexusConfigService.FileName));
                string baseText = File.ReadAllText(config.ConfigPath, Encoding.UTF8);
                (NexusConfigLoadResult Load, double Ms) Write(JsonNode? lanTimeout, bool omit = false)
                {
                    JsonObject root = JsonNode.Parse(baseText)!.AsObject();
                    var stt = new JsonObject { ["provider"] = "lan", ["lanEndpoint"] = "192.168.1.22:5055", ["language"] = "vi-VN" };
                    if (!omit) stt["lanTimeoutMs"] = lanTimeout;
                    root["stt"] = stt;
                    File.WriteAllText(config.ConfigPath, root.ToJsonString(), new UTF8Encoding(false));
                    var sw = Stopwatch.StartNew();
                    NexusConfigLoadResult r = config.Reload();
                    return (r, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
                }
                bool Warned(NexusConfigLoadResult r) => r.Warnings.Any(w => w.Contains("lanTimeoutMs", StringComparison.Ordinal));
                var kept = Write(4000);
                int keptValue = config.Current.Stt?.LanTimeoutMs ?? -1;
                string status = new LocalSttService(() => config.Current.Stt).DescribeStatus();
                var low = Write(500);
                int lowValue = config.Current.Stt?.LanTimeoutMs ?? -1;
                var high = Write(99999);
                int highValue = config.Current.Stt?.LanTimeoutMs ?? -1;
                var text = Write("abc");
                int textValue = config.Current.Stt?.LanTimeoutMs ?? -1;
                var missing = Write(null, omit: true);
                int missingValue = config.Current.Stt?.LanTimeoutMs ?? -1;
                checks["e3.kept4000"] = kept.Load.Ok && keptValue == 4000 && !Warned(kept.Load);
                checks["e3.clampLow"] = low.Load.Ok && lowValue == SttConfig.MinLanTimeoutMs && Warned(low.Load);
                checks["e3.clampHigh"] = high.Load.Ok && highValue == SttConfig.MaxLanTimeoutMs && Warned(high.Load);
                checks["e3.notNumber"] = text.Load.Ok && textValue == SttConfig.DefaultLanTimeoutMs && Warned(text.Load);
                checks["e3.missingDefault"] = missing.Load.Ok && missingValue == SttConfig.DefaultLanTimeoutMs && !Warned(missing.Load);
                checks["e3.reloadUnder50ms"] = new[] { kept.Ms, low.Ms, high.Ms, text.Ms, missing.Ms }.All(ms => ms <= 50);
                checks["e3.statusShowsTimeout"] = status.Contains("lanTimeoutMs 4000", StringComparison.Ordinal);
                steps["e3"] = new
                {
                    kept = new { value = keptValue, kept.Ms, warnings = kept.Load.Warnings },
                    low = new { value = lowValue, low.Ms, warnings = low.Load.Warnings },
                    high = new { value = highValue, high.Ms, warnings = high.Load.Warnings },
                    text = new { value = textValue, text.Ms, warnings = text.Load.Warnings },
                    missing = new { value = missingValue, missing.Ms },
                    status
                };

                // E2: a worker whose recognizer answers after 2500 ms (slower than the old 2000 ms PC limit)
                checks["exeFound"] = exe != null;
                if (exe == null) throw new InvalidOperationException(VisionWorkerClient.ExecutableName + " not found");
                int port = FreeLoopbackPort();
                string endpoint = $"127.0.0.1:{port}";
                worker = StartSttWorker(exe, endpoint, exe, $"{SttWorkerProtocol.Argument} echo \"{VietnameseSample}\" {{wav}} {SlowMs}", logs, out bool ready);
                checks["e2.workerListening"] = ready;
                checks["e2.workerDefaultTimeout12s"] = logs.Any(l => l.Contains($"timeout {SttLanWorker.DefaultTimeoutMs} ms", StringComparison.Ordinal));

                // E2a: lanTimeoutMs 10000 waits for the slow answer
                SttConfig patient = new("lan", endpoint, string.Empty, string.Empty, "vi-VN", SttConfig.DefaultLanTimeoutMs);
                var stt = new LocalSttService(() => patient);
                var mic = new MicService(new CockpitUiStore(tempDir), stt);
                PttResult slow = mic.Run(new ArrayPcmSource(voice), "test", voice.Length / 16000.0 + 0.2, CancellationToken.None);
                LocalSttResult? slowStt = stt.Last;
                checks["e2a.vietnamese"] = LocalSttService.RecognizedText(slow.Transcript) == VietnameseSample && slowStt is { Provider: "lan" };
                checks["e2a.reallySlow"] = slowStt != null && slowStt.TotalMs >= SlowMs && slowStt.TotalMs <= SttConfig.DefaultLanTimeoutMs;
                checks["e2a.pcmZero"] = slow.OutstandingBytesAfter == 0 && PcmBufferLedger.OutstandingBytes == 0;
                steps["e2a"] = new { transcript = slow.Transcript, stt = slowStt };

                // E2b: lanTimeoutMs 1000 stops waiting on time and falls back to SAPI (the worker finishes later and finds the socket closed)
                SttConfig impatient = new("lan", endpoint, string.Empty, string.Empty, "vi-VN", SttConfig.MinLanTimeoutMs);
                var stt2 = new LocalSttService(() => impatient);
                var mic2 = new MicService(new CockpitUiStore(tempDir), stt2);
                PttResult cut = mic2.Run(new ArrayPcmSource(voice), "test", voice.Length / 16000.0 + 0.2, CancellationToken.None);
                LocalSttResult? cutStt = stt2.Last;
                string route = cutStt?.Route ?? string.Empty;
                double lanStepMs = LanStepMs(route);
                checks["e2b.fallbackToSapi"] = cutStt is { Provider: "sapi" } && route.StartsWith("lan ✗", StringComparison.Ordinal) &&
                                               route.Contains("lanTimeoutMs", StringComparison.Ordinal) && LocalSttService.RecognizedText(cut.Transcript) != VietnameseSample;
                checks["e2b.lanCutOnTime"] = lanStepMs >= SttConfig.MinLanTimeoutMs - 50 && lanStepMs <= SttConfig.MinLanTimeoutMs + 350;
                checks["e2b.fallbackUnder1350ms"] = cutStt != null && cutStt.FallbackMs <= SttConfig.MinLanTimeoutMs + 350;
                checks["e2b.pcmZero"] = cut.OutstandingBytesAfter == 0 && PcmBufferLedger.OutstandingBytes == 0;
                steps["e2b"] = new { transcript = cut.Transcript, stt = cutStt, lanStepMs };
                Thread.Sleep(SlowMs); // let the worker finish the abandoned request before it is stopped (its log shows "dropped" or "ok")
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException or AggregateException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                checks["noException"] = false;
                steps["exception"] = ex.ToString();
            }
            finally
            {
                if (worker != null)
                {
                    try { if (!worker.HasExited) worker.Kill(); worker.WaitForExit(2000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    worker.Dispose();
                }
                try { Directory.Delete(configDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            bool pass = checks.Values.All(v => v);
            return (new
            {
                scenario = "stt-timeouts",
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                steps,
                workerLog = logs.ToArray(),
                tokens = 0
            }, pass);

            static int FreeLoopbackPort()
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                l.Start();
                int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }

            // "lan ✗ không trả lời trong 1000 ms (lanTimeoutMs) 1003.2 ms → sapi ✓ …" → 1003.2 (the time of the lan step; "1003,2" on a vi-VN PC)
            static double LanStepMs(string route)
            {
                string first = route.Split(" → ")[0];
                string[] words = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return words.Length >= 2 && words[^1] == "ms" &&
                       double.TryParse(words[^2].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) ? ms : -1;
            }
        }
    }
}
