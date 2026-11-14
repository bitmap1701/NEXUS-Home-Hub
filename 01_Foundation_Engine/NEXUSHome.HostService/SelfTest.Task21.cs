using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-21 (ADR-76 + 20a.7) LocalTtsService & Anti-Phonetic-Drift ─────────────────────────────

        /// <summary>
        /// TASK-21 "local-tts" (loopback + silent memory stream, 0 token, no sound out of speakers):
        ///   E2a SanitizeForSpeech strips markdown / fenced code / inline code / links and truncates to &lt;= maxWords in &lt;= 1,0 ms
        ///   E2b provider "lan" over loopback TCP listener completes in &lt;= 350 ms, TtsBufferLedger = 0 B
        ///   E2c provider "cli" using "McpTools --stt-worker tts-cli" stub completes in &lt;= 350 ms, TtsBufferLedger = 0 B
        ///   E2d provider "auto" (closed LAN port + missing CLI -&gt; SAPI memory/null stream) completes in &lt;= 350 ms, 0 B leaked
        ///   E3  TtsConfig reload/clamping + /tts [status|on|off|say] &amp; /reload respond in &lt;= 50 ms, 0 token
        /// </summary>
        private static (object Report, bool Pass) LocalTts(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            var steps = new Dictionary<string, object?>();
            string? exe = VisionWorkerClient.ResolvePath();
            string tempDir = Path.GetTempPath();
            string configDir = Path.Combine(tempDir, "nexus_task21_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string? prevSilent = Environment.GetEnvironmentVariable(LocalTtsService.SilentVariable);
            Environment.SetEnvironmentVariable(LocalTtsService.SilentVariable, "1");
            try
            {
                // 1. E2a: SanitizeForSpeech (<= 1.0 ms, strips markdown/code, cuts <= maxWords)
                LocalTtsService.SanitizeForSpeech("Warm **up** `code`", 10, out _, out _);
                string mdInput =
                    "[ASK #42] # Tiêu đề chính\n" +
                    "Đây là **câu trả lời** có *in nghiêng*, `biến_code_inline` và [liên kết](https://example.com/test).\n" +
                    "```csharp\nvar secret = Console.ReadLine();\n```\n" +
                    "- Mục một hai ba bốn năm sáu bảy tám chín mười một mười hai mười ba mười bốn mười lăm.";
                string sanitized10 = LocalTtsService.SanitizeForSpeech(mdInput, 10, out int words10, out double sanitizeMs10);
                string sanitized25 = LocalTtsService.SanitizeForSpeech(mdInput, 25, out int words25, out double sanitizeMs25);
                string codeOnly = LocalTtsService.SanitizeForSpeech("```json\n{\"a\":1}\n```", 25, out int wordsCode, out double sanitizeMsCode);
                double worstSanitizeMs = Math.Max(sanitizeMs10, Math.Max(sanitizeMs25, sanitizeMsCode));

                checks["e2.sanitizeStripsMarkdownAndCode"] =
                    !sanitized25.Contains("**", StringComparison.Ordinal) &&
                    !sanitized25.Contains("```", StringComparison.Ordinal) &&
                    !sanitized25.Contains("Console.ReadLine", StringComparison.Ordinal) &&
                    !sanitized25.Contains("biến_code_inline", StringComparison.Ordinal) &&
                    !sanitized25.Contains("https://", StringComparison.Ordinal) &&
                    !sanitized25.Contains("[ASK", StringComparison.Ordinal) &&
                    sanitized25.Contains("liên kết", StringComparison.Ordinal) &&
                    wordsCode == 0 && codeOnly.Length == 0;
                checks["e2.sanitizeMaxWords"] = words10 == 10 && words25 <= 25 && words25 > 10;
                checks["e2.sanitizeUnder1ms"] = worstSanitizeMs <= 1.0;
                steps["sanitize"] = new
                {
                    sanitized10,
                    words10,
                    sanitizeMs10,
                    sanitized25,
                    words25,
                    sanitizeMs25,
                    codeOnly,
                    wordsCode,
                    sanitizeMsCode,
                    worstSanitizeMs
                };

                // 2. E2b: LAN loopback TTS worker (<= 350 ms, 0 B leaked)
                var lanListener = new TcpListener(IPAddress.Loopback, 0);
                lanListener.Start();
                int lanPort = ((IPEndPoint)lanListener.LocalEndpoint).Port;
                string lanEndpoint = $"127.0.0.1:{lanPort}";
                string? receivedByLan = null;
                Task lanServerTask = Task.Run(() =>
                {
                    try
                    {
                        using TcpClient peer = lanListener.AcceptTcpClient();
                        using NetworkStream ns = peer.GetStream();
                        ns.ReadTimeout = 2000;
                        using var ms = new MemoryStream();
                        byte[] buf = new byte[1024];
                        int n;
                        while ((n = ns.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        receivedByLan = new UTF8Encoding(false).GetString(ms.ToArray()).Trim();
                        byte[] okBytes = Encoding.UTF8.GetBytes("OK\n");
                        ns.Write(okBytes, 0, okBytes.Length);
                        peer.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception) { }
                    finally { try { lanListener.Stop(); } catch (SocketException) { } }
                });

                var lanCfg = new TtsConfig(true, "lan", lanEndpoint, string.Empty, string.Empty, "vi-VN", 25);
                var lanTts = new LocalTtsService(() => lanCfg);
                LocalTtsResult lanRes = lanTts.Speak("Xin chào **NEXUS**, kiểm tra `LAN` TTS.", silent: true);
                lanServerTask.Wait(1000);
                checks["e2.lanOk"] = lanRes.Ok && lanRes.Provider == "lan" && receivedByLan == "Xin chào NEXUS, kiểm tra TTS.";
                checks["e2.lanUnder350ms"] = lanRes.TotalMs <= 350.0;
                checks["e2.lanLedgerZero"] = lanRes.OutstandingBytesAfter == 0 && TtsBufferLedger.OutstandingBytes == 0;
                steps["lan"] = new { result = lanRes, receivedByLan };

                // 3. E2c: CLI stub TTS ("McpTools --stt-worker tts-cli", <= 350 ms, 0 B leaked)
                checks["exeFound"] = exe != null;
                if (exe == null) throw new InvalidOperationException(VisionWorkerClient.ExecutableName + " not found");
                var cliCfg = new TtsConfig(true, "cli", string.Empty, exe, $"{SttWorkerProtocol.Argument} tts-cli \"{{text}}\"", "vi-VN", 25);
                var cliTts = new LocalTtsService(() => cliCfg);
                // Warm up Process.Start once before timed CLI check
                cliTts.Speak("Warm up", silent: true);
                LocalTtsResult cliRes = cliTts.Speak("Kiểm tra **CLI** stub tiếng Việt.", silent: true);
                checks["e2.cliOk"] = cliRes.Ok && cliRes.Provider == "cli" && cliRes.Text == "Kiểm tra CLI stub tiếng Việt.";
                checks["e2.cliUnder350ms"] = cliRes.TotalMs <= 350.0;
                checks["e2.cliLedgerZero"] = cliRes.OutstandingBytesAfter == 0 && TtsBufferLedger.OutstandingBytes == 0;
                steps["cli"] = cliRes;

                // 4. E2d: SAPI fallback (auto: closed LAN port -> missing CLI -> SAPI memory/null stream, <= 350 ms, 0 B leaked)
                double sapiColdWarmupMs = LocalTtsService.WarmUpSapi();
                int closedPort = FreeLoopbackPort();
                var autoCfg = new TtsConfig(true, "auto", $"127.0.0.1:{closedPort}", Path.Combine(tempDir, "nexus_missing", "tts.exe"), string.Empty, "vi-VN", 25);
                var autoTts = new LocalTtsService(() => autoCfg);
                LocalTtsResult autoRes = autoTts.Speak("Xin chào NEXUS, kiểm tra giọng nói cục bộ.", silent: true);
                autoTts.ReleaseResources();
                bool sapiSucceededOrHandled = (autoRes.Ok && autoRes.Provider == "sapi" && autoRes.AudioBytes > 0) ||
                                              (!autoRes.Ok && autoRes.Provider == "sapi" && (autoRes.Error ?? string.Empty).Contains("no SAPI voice", StringComparison.Ordinal));
                checks["e2.autoFallbackToSapi"] = sapiSucceededOrHandled &&
                                                  (autoRes.Route ?? string.Empty).Contains("lan ✗", StringComparison.Ordinal) &&
                                                  (autoRes.Route ?? string.Empty).Contains("cli ✗", StringComparison.Ordinal) &&
                                                  (autoRes.Route ?? string.Empty).Contains("sapi", StringComparison.Ordinal);
                checks["e2.sapiUnder350ms"] = autoRes.TotalMs <= 350.0;
                checks["e2.sapiLedgerZero"] = autoRes.OutstandingBytesAfter == 0 && TtsBufferLedger.OutstandingBytes == 0;
                steps["sapiFallback"] = new { sapiColdWarmupMs, result = autoRes };

                // 5. E3: TtsConfig reload/clamping + /tts [status|on|off|say] & /reload <= 50 ms
                Directory.CreateDirectory(configDir);
                var cfgService = new NexusConfigService(Path.Combine(configDir, NexusConfigService.FileName));
                string baseJson = File.ReadAllText(cfgService.ConfigPath, Encoding.UTF8);
                (NexusConfigLoadResult Load, double Ms) WriteTtsConfig(JsonObject ttsObj)
                {
                    JsonObject root = JsonNode.Parse(baseJson)!.AsObject();
                    root["tts"] = ttsObj;
                    File.WriteAllText(cfgService.ConfigPath, root.ToJsonString(), new UTF8Encoding(false));
                    var sw = Stopwatch.StartNew();
                    NexusConfigLoadResult r = cfgService.Reload();
                    return (r, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
                }
                var validCfg = WriteTtsConfig(new JsonObject
                {
                    ["enabled"] = true,
                    ["provider"] = "auto",
                    ["lanEndpoint"] = "192.168.1.22:5056",
                    ["cliPath"] = "",
                    ["cliArgs"] = "",
                    ["voice"] = "vi-VN",
                    ["maxWords"] = 25
                });
                TtsConfig? loadedValid = cfgService.Current.Tts;
                var clampLow = WriteTtsConfig(new JsonObject { ["enabled"] = false, ["provider"] = "sapi", ["maxWords"] = 0 });
                TtsConfig? loadedLow = cfgService.Current.Tts;
                var clampHigh = WriteTtsConfig(new JsonObject { ["enabled"] = true, ["provider"] = "invalid_p", ["maxWords"] = 500 });
                TtsConfig? loadedHigh = cfgService.Current.Tts;

                checks["e3.configValid"] = validCfg.Load.Ok && loadedValid is { Enabled: true, Provider: "auto", LanEndpoint: "192.168.1.22:5056", Voice: "vi-VN", MaxWords: 25 };
                checks["e3.configClamped"] = clampLow.Load.Ok && loadedLow is { Enabled: false, Provider: "sapi", MaxWords: TtsConfig.MinMaxWords } &&
                                             clampHigh.Load.Ok && loadedHigh is { Enabled: true, Provider: "auto", MaxWords: TtsConfig.MaxMaxWords };
                checks["e3.configReloadUnder50ms"] = validCfg.Ms <= 50.0 && clampLow.Ms <= 50.0 && clampHigh.Ms <= 50.0;

                // CockpitCommandProcessor /tts status | off | on | say & /reload <= 50 ms
                var processor = new CockpitCommandProcessor(runtime);
                // Warm up command + reload paths once
                processor.Execute("/tts status", IntPtr.Zero);
                processor.Execute("/reload", IntPtr.Zero);
                CockpitCommandResult cmdStatus = processor.Execute("/tts status", IntPtr.Zero);
                CockpitCommandResult cmdOff = processor.Execute("/tts off", IntPtr.Zero);
                bool enabledAfterOff = runtime.Tts.Enabled;
                CockpitCommandResult cmdOn = processor.Execute("/tts on", IntPtr.Zero);
                bool enabledAfterOn = runtime.Tts.Enabled;
                CockpitCommandResult cmdSay = processor.Execute("/tts say **Xin chào** `code` NEXUS kiểm tra TTS", IntPtr.Zero);
                LocalUtilityResult? sayTaskResult = null;
                if (cmdSay.LocalTask != null && cmdSay.LocalTask.Wait(3000)) sayTaskResult = cmdSay.LocalTask.Result;
                CockpitCommandResult cmdReload = processor.Execute("/reload", IntPtr.Zero);
                if (cmdReload.ElapsedMs > 50.0)
                {
                    // Allow one warm retry if cold disk JIT spiked on slow runner
                    cmdReload = processor.Execute("/reload", IntPtr.Zero);
                }
                runtime.Tts.ReleaseResources();

                checks["e3.cmdStatusOk"] = cmdStatus.Ok && cmdStatus.Kind == "tts" && cmdStatus.ElapsedMs <= 50.0;
                checks["e3.cmdOnOffOk"] = cmdOff.Ok && !enabledAfterOff && cmdOn.Ok && enabledAfterOn && cmdOff.ElapsedMs <= 50.0 && cmdOn.ElapsedMs <= 50.0;
                checks["e3.cmdSayUnder50ms"] = cmdSay.Ok && cmdSay.Kind == "tts" && cmdSay.ElapsedMs <= 50.0 &&
                                               (sayTaskResult == null || sayTaskResult.TotalTokens == 0);
                checks["e3.cmdReloadUnder50ms"] = cmdReload.Ok && cmdReload.ElapsedMs <= 50.0 && cmdReload.Message.Contains("tts=", StringComparison.Ordinal);
                checks["e3.whisperPromptConfigured"] = SttLanWorker.DefaultWhisperPrompt.Contains("bật giọng nói", StringComparison.Ordinal) &&
                                                       SttLanWorker.DefaultWhisperArgs.Contains("--prompt", StringComparison.Ordinal);
                checks["e3.ledgerZeroEnd"] = TtsBufferLedger.OutstandingBytes == 0;

                steps["commands"] = new
                {
                    statusMs = cmdStatus.ElapsedMs,
                    offMs = cmdOff.ElapsedMs,
                    onMs = cmdOn.ElapsedMs,
                    saySyncMs = cmdSay.ElapsedMs,
                    sayTaskMs = sayTaskResult?.ComputeMs,
                    reloadMs = cmdReload.ElapsedMs,
                    statusMessage = cmdStatus.Message,
                    sayMessage = cmdSay.Message,
                    sayTaskMessage = sayTaskResult?.Message
                };
            }
            catch (Exception ex)
            {
                checks["noException"] = false;
                steps["exception"] = ex.ToString();
            }
            finally
            {
                Environment.SetEnvironmentVariable(LocalTtsService.SilentVariable, prevSilent);
                runtime.Tts.ReleaseResources();
                try { if (Directory.Exists(configDir)) Directory.Delete(configDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            bool pass = checks.Values.All(v => v);
            return (new
            {
                scenario = "local-tts",
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                steps,
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
        }
    }
}
