using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-18b (ADR-73) TCP 5055 STT worker, end to end on this PC ─────────────────────────────

        /// <summary>
        /// TASK-18b "stt-worker-e2e" (loopback only, does not need the Nitro 5; 0 token):
        ///   E2  "McpTools --stt-worker serve --bind 127.0.0.1:&lt;free&gt;" with a stand-in CLI ("--stt-worker echo &lt;text&gt; {wav}", which
        ///       checks the temp WAV) → LocalSttService (provider lan) gets the Vietnamese UTF-8 sample &lt;= 500 ms; no temp WAV left
        ///   E3  empty / not-a-WAV / &gt; 2 MB / cut-off connections → refused &lt;= 100 ms, the worker keeps serving; two parallel requests
        ///       both answered (one recognition at a time); peer filter (loopback + 192.168.1.x only); a worker whose whisper-cli is
        ///       missing answers "ERR:" and LocalSttService falls back to SAPI &lt;= 250 ms
        /// </summary>
        private static (object Report, bool Pass) SttWorkerE2e(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            var steps = new Dictionary<string, object?>();
            string? exe = VisionWorkerClient.ResolvePath();
            string tempDir = Path.GetTempPath();
            int TempWavs() => Directory.GetFiles(tempDir, "nexus_ptt_*.wav").Length;
            int tempBefore = TempWavs();
            var logs = new ConcurrentQueue<string>();
            Process? worker = null, missing = null;
            short[] voice = Enumerable.Range(0, 24000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0))).ToArray();
            try
            {
                checks["exeFound"] = exe != null;
                if (exe == null) throw new InvalidOperationException(VisionWorkerClient.ExecutableName + " not found");

                int port = FreeLoopbackPort();
                string endpoint = $"127.0.0.1:{port}";
                worker = StartSttWorker(exe, endpoint, exe, $"{SttWorkerProtocol.Argument} echo \"{VietnameseSample}\" {{wav}}", logs, out bool ready);
                checks["e2.workerListening"] = ready;

                // E2: LocalSttService over the LAN contract
                SttConfig cfg = new("lan", endpoint, string.Empty, string.Empty, "vi-VN");
                var stt = new LocalSttService(() => cfg);
                var mic = new MicService(new CockpitUiStore(tempDir), stt);
                PttResult run = mic.Run(new ArrayPcmSource(voice), "test", voice.Length / 16000.0 + 0.2, CancellationToken.None);
                LocalSttResult? last = stt.Last;
                checks["e2.vietnamese"] = LocalSttService.RecognizedText(run.Transcript) == VietnameseSample && last is { Provider: "lan" };
                checks["e2.under500ms"] = last != null && last.TotalMs <= 500;
                checks["e2.ledgerZero"] = run.OutstandingBytesAfter == 0 && PcmBufferLedger.OutstandingBytes == 0;
                steps["e2"] = new { transcript = run.Transcript, stt = last };

                // E3: bad requests are refused fast and the worker keeps serving
                byte[] wav = SttBridge.ToWav(voice);
                var empty = Raw(endpoint, Array.Empty<byte>(), abort: false);
                checks["e3.emptyRefused"] = empty.Reply.StartsWith(SttLanWorker.ErrorPrefix, StringComparison.Ordinal) && empty.Ms <= 100;
                byte[] garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
                var notWav = Raw(endpoint, garbage, abort: false);
                checks["e3.notWavRefused"] = notWav.Reply.StartsWith(SttLanWorker.ErrorPrefix, StringComparison.Ordinal) && notWav.Ms <= 100;
                var tooBig = Raw(endpoint, new byte[SttLanWorker.MaxWavBytes + 256 * 1024], abort: false);
                checks["e3.oversizeRefused"] = (tooBig.Reply.StartsWith(SttLanWorker.ErrorPrefix, StringComparison.Ordinal) || tooBig.Error != null) && tooBig.Ms <= 100;
                var cut = Raw(endpoint, wav.Take(1000).ToArray(), abort: true);
                var (afterCut, afterCutError) = SttBridge.Lan(endpoint, wav);
                checks["e3.cutThenValid"] = afterCut == VietnameseSample;
                Task<(string? Text, string? Error)> p1 = Task.Run(() => SttBridge.Lan(endpoint, wav));
                Task<(string? Text, string? Error)> p2 = Task.Run(() => SttBridge.Lan(endpoint, wav));
                Task.WaitAll(new Task[] { p1, p2 }, 5000);
                checks["e3.parallelBothAnswered"] = p1.IsCompleted && p2.IsCompleted && p1.Result.Text == VietnameseSample && p2.Result.Text == VietnameseSample;
                checks["e3.peerFilter"] =
                    SttLanWorker.IsAllowedRemote(IPAddress.Loopback) && SttLanWorker.IsAllowedRemote(IPAddress.IPv6Loopback) &&
                    SttLanWorker.IsAllowedRemote(IPAddress.Parse("192.168.1.212")) && SttLanWorker.IsAllowedRemote(IPAddress.Parse("192.168.1.22")) &&
                    SttLanWorker.IsAllowedRemote(IPAddress.Parse("::ffff:192.168.1.192")) &&
                    !SttLanWorker.IsAllowedRemote(IPAddress.Parse("10.0.0.5")) && !SttLanWorker.IsAllowedRemote(IPAddress.Parse("26.10.20.30")) &&
                    !SttLanWorker.IsAllowedRemote(IPAddress.Parse("192.168.2.10")) && !SttLanWorker.IsAllowedRemote(IPAddress.Parse("8.8.8.8"));
                steps["e3"] = new { empty, notWav, tooBig, cut, afterCut, afterCutError, parallel = new[] { new { p1.Result.Text, p1.Result.Error }, new { p2.Result.Text, p2.Result.Error } } };

                // E3: whisper-cli missing → "ERR:" → SAPI
                int port2 = FreeLoopbackPort();
                string endpoint2 = $"127.0.0.1:{port2}";
                missing = StartSttWorker(exe, endpoint2, Path.Combine(tempDir, "nexus_missing", "whisper-cli.exe"), string.Empty, logs, out bool ready2);
                SttConfig cfg2 = new("auto", endpoint2, string.Empty, string.Empty, "vi-VN");
                var stt2 = new LocalSttService(() => cfg2);
                var mic2 = new MicService(new CockpitUiStore(tempDir), stt2);
                PttResult fb = mic2.Run(new ArrayPcmSource(voice), "test", voice.Length / 16000.0 + 0.2, CancellationToken.None);
                LocalSttResult? fbr = stt2.Last;
                checks["e3.missingCliFallsBackToSapi"] = ready2 && fbr is { Provider: "sapi" } &&
                    (fbr.Route ?? string.Empty).StartsWith("lan ✗ " + SttLanWorker.ErrorPrefix, StringComparison.Ordinal) && LocalSttService.RecognizedText(fb.Transcript) != VietnameseSample;
                checks["e3.fallbackUnder250ms"] = fbr != null && fbr.FallbackMs <= 250;
                steps["missingCli"] = new { transcript = fb.Transcript, stt = fbr };
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or SocketException or AggregateException or System.ComponentModel.Win32Exception)
            {
                checks["noException"] = false;
                steps["exception"] = ex.ToString();
            }
            finally
            {
                foreach (Process? p in new[] { worker, missing })
                {
                    if (p == null) continue;
                    try { if (!p.HasExited) p.Kill(); p.WaitForExit(2000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    p.Dispose();
                }
            }
            Thread.Sleep(200);
            int tempAfter = TempWavs();
            checks["e2.noTempWavLeft"] = tempAfter <= tempBefore;
            bool pass = checks.Values.All(v => v);
            return (new
            {
                scenario = "stt-worker-e2e",
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                steps,
                tempWav = new { before = tempBefore, after = tempAfter },
                workerLog = logs.ToArray(),
                tokens = 0
            }, pass);

            static int FreeLoopbackPort()
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }

            static RawReply Raw(string endpoint, byte[] body, bool abort)
            {
                var sw = Stopwatch.StartNew();
                int colon = endpoint.LastIndexOf(':');
                using var client = new TcpClient();
                try
                {
                    client.Connect(endpoint.Substring(0, colon), int.Parse(endpoint.Substring(colon + 1), System.Globalization.CultureInfo.InvariantCulture));
                    NetworkStream s = client.GetStream();
                    s.ReadTimeout = 2000;
                    try { s.Write(body, 0, body.Length); } catch (IOException) { } // the worker may stop reading an oversize body
                    if (abort)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Close(); // RST in the middle of the WAV
                        return new RawReply(string.Empty, "aborted", Math.Round(sw.Elapsed.TotalMilliseconds, 2));
                    }
                    try { client.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                    var reply = new MemoryStream();
                    var buffer = new byte[4096];
                    int n;
                    while ((n = s.Read(buffer, 0, buffer.Length)) > 0) reply.Write(buffer, 0, n);
                    return new RawReply(new UTF8Encoding(false).GetString(reply.ToArray()), null, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    return new RawReply(string.Empty, ex.GetType().Name, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
                }
            }
        }

        private sealed record RawReply(string Reply, string? Error, double Ms);

        /// <summary>Starts "McpTools --stt-worker serve" and waits (&lt;= 5 s) for its "listening" line on stderr.</summary>
        private static Process StartSttWorker(string exe, string bind, string cli, string cliArgs, ConcurrentQueue<string> logs, out bool ready)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };
            foreach (string a in new[] { SttWorkerProtocol.Argument, "serve", "--bind", bind, "--cli", cli, "--args", cliArgs, "--lang", "vi" }) info.ArgumentList.Add(a);
            var listening = new ManualResetEventSlim(false);
            Process process = new() { StartInfo = info };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                logs.Enqueue(e.Data);
                if (e.Data.Contains("listening on", StringComparison.Ordinal)) listening.Set();
            };
            process.Start();
            process.BeginErrorReadLine();
            ready = listening.Wait(5000);
            return process;
        }
    }
}
