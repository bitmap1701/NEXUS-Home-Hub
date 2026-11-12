using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-18 (ADR-71): names and JSON shared by the STT worker and the HostService (platform-neutral).</summary>
    public static class SttWorkerProtocol
    {
        public const string Argument = "--stt-worker";
        public const string Name = "nexus-stt-worker/1";
        public const int SampleRate = 16000;

        internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    /// <summary>TASK-18: the one JSON line a <c>McpTools --stt-worker recognize | synth</c> child writes before it exits.</summary>
    public sealed record SttWorkerReply(
        string Protocol,
        bool Ok,
        string? Error,
        bool Missing,            // no SAPI recognizer / voice installed (language pack missing)
        string? Text,
        double Confidence,
        int Phrases,
        string? Engine,          // recognizer or voice name + culture
        double LoadMs,
        double WorkMs,
        string? PcmBase64);      // synth only: 16 kHz / 16-bit / mono PCM

    /// <summary>TASK-18: what one STT call did (kept for /ptt status and the selftest).</summary>
    public sealed record LocalSttResult(
        bool Ok,
        string? Text,
        string Outcome,          // "text" | "silence" | "no-text" | "fallback"
        string? Error,
        double TotalMs,
        double? WorkerLoadMs,
        double? WorkerWorkMs,
        string? Engine,
        double Confidence,
        int? ExitCode,
        string AtUtc,
        string? Provider = null,     // TASK-18a: "lan" | "cli" | "sapi" (the one that answered or was tried last)
        string? Route = null,        // TASK-18a: e.g. "lan ✗ ConnectionRefused 1,2 ms → sapi ✓ 240 ms"
        double FallbackMs = 0);      // TASK-18a: time spent on lan / cli before SAPI took over

    /// <summary>
    /// TASK-18 (ADR-71): the SAPI side, run by <c>NEXUSHome.McpTools --stt-worker</c> before any MCP or SQLite setup.
    /// "recognize": raw PCM on stdin → DictationGrammar on the first en-US (else first) installed recognizer → one JSON line.
    /// "synth": one text line on stdin → an installed voice speaks it into 16 kHz PCM (selftest sample, no microphone).
    /// The process exits after one job, so every SAPI native allocation goes back to Windows with it (the HostService
    /// never loads System.Speech). Exit 0 = ok, 2 = handled failure (described in the reply), 1 = crash.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class SttWorkerServer
    {
        public static int Run(string mode, Stream input, Stream output, string? argument = null, string? wavPath = null)
        {
            if (mode.Equals("echo", StringComparison.OrdinalIgnoreCase))
            {
                // TASK-18a / 18b selftest: a stand-in "cli" recognizer. Reads the WAV from stdin, or checks the {wav} file it was
                // given (exists, RIFF), and prints the given text as UTF-8.
                if (wavPath != null)
                {
                    byte[] head = new byte[4];
                    using (FileStream f = File.OpenRead(wavPath)) { if (f.Read(head, 0, 4) != 4) return 3; }
                    if (Encoding.ASCII.GetString(head) != "RIFF") return 3;
                }
                else input.CopyTo(Stream.Null);
                byte[] text = new UTF8Encoding(false).GetBytes((argument ?? string.Empty) + "\n");
                output.Write(text, 0, text.Length);
                output.Flush();
                return 0;
            }
            SttWorkerReply reply;
            try
            {
                reply = mode.Equals("synth", StringComparison.OrdinalIgnoreCase) ? Synthesize(input) : Recognize(input);
            }
            catch (Exception ex)
            {
                reply = new SttWorkerReply(SttWorkerProtocol.Name, false, $"{ex.GetType().Name}: {ex.Message}", false, null, 0, 0, null, 0, 0, null);
            }
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(reply, SttWorkerProtocol.Json);
            output.Write(line, 0, line.Length);
            output.WriteByte((byte)'\n');
            output.Flush();
            return reply.Ok ? 0 : 2;
        }

        private static SttWorkerReply Recognize(Stream input)
        {
            // TASK-18: the HostService starts this child when capture begins, so the recognizer loads and warms up (the
            // dictation model loads on the first Recognize, ~1 s on CI) while the user is still speaking; then stdin is read.
            long t0 = Stopwatch.GetTimestamp();
            var installed = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers();
            System.Speech.Recognition.RecognizerInfo? info = installed.FirstOrDefault(r => r.Culture.Name == "en-US") ?? installed.FirstOrDefault();
            if (info == null)
                return new SttWorkerReply(SttWorkerProtocol.Name, false, LocalSttService.NoRecognizerError + " (Windows speech language pack)", true, null, 0, 0, null, Ms(t0), 0, null);
            using var engine = new System.Speech.Recognition.SpeechRecognitionEngine(info);
            engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
            var format = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                SttWorkerProtocol.SampleRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono);
            using (var warm = new MemoryStream(new byte[SttWorkerProtocol.SampleRate / 2 * 2])) // 0,5 s of silence
            {
                engine.SetInputToAudioStream(warm, format);
                try { engine.Recognize(); } catch (InvalidOperationException) { } // end of the warm-up stream
            }
            double loadMs = Ms(t0);
            using var pcm = new MemoryStream();
            input.CopyTo(pcm); // blocks until the HostService closes stdin (end of capture); empty = the session was silent
            if (pcm.Length == 0)
                return new SttWorkerReply(SttWorkerProtocol.Name, true, null, false, string.Empty, 0, 0, $"{info.Name} ({info.Culture.Name})", loadMs, 0, null);
            pcm.Position = 0;
            engine.SetInputToAudioStream(pcm, format);
            long t1 = Stopwatch.GetTimestamp();
            var text = new StringBuilder();
            float confidence = 0;
            int phrases = 0;
            for (int i = 0; i < 20; i++) // one phrase per call; null (or "no audio input") at the end of the stream
            {
                System.Speech.Recognition.RecognitionResult? r;
                try { r = engine.Recognize(); }
                catch (InvalidOperationException) when (phrases > 0 || pcm.Position >= pcm.Length) { break; } // input stream consumed (CI 4f2f241)
                if (r == null) break;
                if (text.Length > 0) text.Append(' ');
                text.Append(r.Text);
                confidence = Math.Max(confidence, r.Confidence);
                phrases++;
            }
            return new SttWorkerReply(SttWorkerProtocol.Name, true, null, false, text.ToString(), Math.Round(confidence, 3), phrases,
                $"{info.Name} ({info.Culture.Name})", loadMs, Ms(t1), null);
        }

        private static SttWorkerReply Synthesize(Stream input)
        {
            string text;
            using (var reader = new StreamReader(input, new UTF8Encoding(false))) text = (reader.ReadLine() ?? string.Empty).Trim();
            long t0 = Stopwatch.GetTimestamp();
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            System.Speech.Synthesis.InstalledVoice? voice = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name == "en-US")
                ?? synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled);
            if (voice == null)
                return new SttWorkerReply(SttWorkerProtocol.Name, false, "no SAPI voice installed", true, null, 0, 0, null, Ms(t0), 0, null);
            synth.SelectVoice(voice.VoiceInfo.Name);
            using var pcm = new MemoryStream();
            synth.SetOutputToAudioStream(pcm, new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                SttWorkerProtocol.SampleRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            double loadMs = Ms(t0);
            long t1 = Stopwatch.GetTimestamp();
            synth.Speak(text);
            synth.SetOutputToNull();
            return new SttWorkerReply(SttWorkerProtocol.Name, true, null, false, text, 1, 1,
                $"{voice.VoiceInfo.Name} ({voice.VoiceInfo.Culture.Name})", loadMs, Ms(t1), Convert.ToBase64String(pcm.ToArray()));
        }

        private static double Ms(long startTicks) => Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }

    /// <summary>
    /// TASK-18: the HostService side. Starts our own short-lived McpTools child (no window), writes the job, reads one JSON
    /// line, waits for the exit. After <see cref="TimeoutMs"/> without an answer only that child is stopped.
    /// </summary>
    public static class SttWorkerClient
    {
        public const int TimeoutMs = 5000;

        /// <summary>A started child waiting for its stdin (TASK-18: started when capture begins).</summary>
        public sealed class Pending
        {
            internal Pending(Process process, Task<string?> stdout, Task<string> stderr) { Process = process; Stdout = stdout; Stderr = stderr; }
            internal Process Process { get; }
            internal Task<string?> Stdout { get; }
            internal Task<string> Stderr { get; }
        }

        public static (SttWorkerReply? Reply, string? Error, int? ExitCode, double Ms) Run(string mode, byte[] stdin)
        {
            var (pending, error) = Start(mode);
            return pending == null ? (null, error, null, 0) : Finish(pending, stdin);
        }

        /// <summary>Closes the stdin of a child nobody needs any more (silent or failed session): it answers "empty" and exits.</summary>
        public static void Abandon(Pending pending)
        {
            try { pending.Process.StandardInput.Close(); } catch (IOException) { } catch (InvalidOperationException) { }
            pending.Process.Dispose();
        }

        public static (Pending? Pending, string? Error) Start(string mode)
        {
            string? path = VisionWorkerClient.ResolvePath(); // same McpTools executable as the vision worker
            if (path == null) return (null, $"{VisionWorkerClient.ExecutableName} not found");
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
            };
            info.ArgumentList.Add(SttWorkerProtocol.Argument);
            info.ArgumentList.Add(mode);
            try
            {
                Process? process = Process.Start(info);
                if (process == null) return (null, "Process.Start returned null");
                return (new Pending(process, process.StandardOutput.ReadLineAsync(), process.StandardError.ReadToEndAsync()), null);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                return (null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Writes the job to a started child, closes its stdin and reads the one-line reply.</summary>
        public static (SttWorkerReply? Reply, string? Error, int? ExitCode, double Ms) Finish(Pending pending, byte[] stdin)
        {
            long t0 = Stopwatch.GetTimestamp();
            Process process = pending.Process;
            Task<string?> stdout = pending.Stdout;
            Task<string> stderr = pending.Stderr;
            try
            {
                try
                {
                    process.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                    process.StandardInput.Close();
                }
                catch (IOException) { } // the child already answered and exited (e.g. no recognizer installed): read its reply
                if (!stdout.Wait(TimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return (null, $"no answer within {TimeoutMs} ms (worker stopped)", null, Ms(t0));
                }
                process.WaitForExit(2000);
                int? exit = process.HasExited ? process.ExitCode : null;
                string? line = stdout.Result;
                if (string.IsNullOrWhiteSpace(line))
                {
                    string err = stderr.Wait(500) ? stderr.Result.Trim() : string.Empty;
                    return (null, $"no reply (exit {exit}){(err.Length > 0 ? ": " + (err.Length > 300 ? err.Substring(0, 300) : err) : string.Empty)}", exit, Ms(t0));
                }
                SttWorkerReply? reply = JsonSerializer.Deserialize<SttWorkerReply>(line, SttWorkerProtocol.Json);
                if (reply == null || reply.Protocol != SttWorkerProtocol.Name)
                    return (null, $"protocol mismatch ({reply?.Protocol ?? "none"}): rebuild McpTools", exit, Ms(t0));
                return (reply, reply.Ok ? null : reply.Error, exit, Ms(t0));
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or JsonException or AggregateException)
            {
                return (null, $"{ex.GetType().Name}: {ex.Message}", null, Ms(t0));
            }
            finally
            {
                process.Dispose();
            }
        }

        /// <summary>Selftest sample: an installed voice reads <paramref name="text"/> into 16 kHz mono PCM (null when no voice).</summary>
        public static (short[]? Samples, SttWorkerReply? Reply, string? Error) Synthesize(string text)
        {
            var (reply, error, _, _) = Run("synth", new UTF8Encoding(false).GetBytes(text + "\n"));
            if (reply?.PcmBase64 == null) return (null, reply, error ?? "no audio");
            byte[] bytes = Convert.FromBase64String(reply.PcmBase64);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return (samples, reply with { PcmBase64 = null }, null);
        }

        private static double Ms(long startTicks) => Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }

    /// <summary>
    /// TASK-18 (ADR-71): the /ptt speech-to-text provider. VAD = False → returns at once without starting anything (Spec 1,
    /// &lt;= 5 ms, 0 token). VAD = True → the kept 16 kHz PCM goes to a short-lived SAPI worker (en-US dictation), the text
    /// comes back and the worker exits (no recognizer in the HostService, Spec 2). No recognizer / no worker /
    /// NEXUS_STT_SIMULATE_MISSING = "lang" | "worker" → a safe fallback message, no exception (E3). 0 token, no network.
    /// </summary>
    /// <summary>
    /// TASK-18a (ADR-72): the out-of-process bridges for a Vietnamese (or any) recognizer.
    /// LAN: TCP connect &lt;= 200 ms, send one WAV (RIFF, 16 kHz / 16-bit / mono), half-close, read the UTF-8 answer to the end
    /// (&lt;= 2000 ms). CLI: start cliPath with cliArgs ("{wav}" = a temp WAV path, otherwise the WAV goes to stdin; "{lang}" =
    /// the language), read stdout as UTF-8 (&lt;= 3000 ms, then only that child is stopped). Empty answer = failure.
    /// </summary>
    public static class SttBridge
    {
        public const int LanConnectMs = 200;
        public const int LanReadMs = 2000;
        public const int CliTimeoutMs = 3000;

        public static byte[] ToWav(ReadOnlySpan<short> samples)
        {
            int dataBytes = samples.Length * 2;
            var wav = new byte[44 + dataBytes];
            void Ascii(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(wav, at);
            void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(wav, at);
            void I16(int at, short v) => BitConverter.GetBytes(v).CopyTo(wav, at);
            Ascii(0, "RIFF"); I32(4, 36 + dataBytes); Ascii(8, "WAVE");
            Ascii(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 1); I32(24, SttWorkerProtocol.SampleRate); I32(28, SttWorkerProtocol.SampleRate * 2);
            I16(32, 2); I16(34, 16);
            Ascii(36, "data"); I32(40, dataBytes);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(wav.AsSpan(44));
            return wav;
        }

        public static (string? Text, string? Error) Lan(string endpoint, byte[] wav)
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
                return (null, "lanEndpoint không phải host:port");
            string host = endpoint.Substring(0, colon);
            using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            try
            {
                Task connect = client.ConnectAsync(host, port);
                if (!connect.Wait(LanConnectMs)) return (null, $"không kết nối được trong {LanConnectMs} ms");
                using System.Net.Sockets.NetworkStream stream = client.GetStream();
                stream.WriteTimeout = LanReadMs;
                stream.ReadTimeout = LanReadMs;
                stream.Write(wav, 0, wav.Length);
                client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                var answer = new MemoryStream();
                var buffer = new byte[4096];
                long deadline = Stopwatch.GetTimestamp() + LanReadMs * Stopwatch.Frequency / 1000;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    answer.Write(buffer, 0, n);
                    if (Stopwatch.GetTimestamp() > deadline) return (null, $"không trả lời xong trong {LanReadMs} ms");
                }
                string text = new UTF8Encoding(false).GetString(answer.ToArray()).Trim();
                if (text.StartsWith(SttLanWorker.ErrorPrefix, StringComparison.Ordinal)) return (null, text); // TASK-18b worker refusal
                return text.Length > 0 ? (text, null) : (null, "trả lời rỗng");
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or AggregateException or ObjectDisposedException)
            {
                Exception inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                return (null, inner is System.Net.Sockets.SocketException se ? se.SocketErrorCode.ToString() : inner.GetType().Name);
            }
        }

        public static (string? Text, string? Error) Cli(string path, string args, string language, byte[] wav)
        {
            if (path.Length == 0) return (null, "chưa cấu hình");
            if (!File.Exists(path)) return (null, "không có file " + Path.GetFileName(path));
            string? temp = null;
            bool viaFile = args.Contains("{wav}", StringComparison.Ordinal);
            try
            {
                if (viaFile)
                {
                    temp = Path.Combine(Path.GetTempPath(), "nexus_ptt_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                    File.WriteAllBytes(temp, wav);
                }
                var info = new ProcessStartInfo(path, args.Replace("{wav}", "\"" + temp + "\"", StringComparison.Ordinal).Replace("{lang}", language, StringComparison.Ordinal))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = !viaFile,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false)
                };
                using Process? process = Process.Start(info);
                if (process == null) return (null, "Process.Start returned null");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!viaFile)
                {
                    try
                    {
                        process.StandardInput.BaseStream.Write(wav, 0, wav.Length);
                        process.StandardInput.Close();
                    }
                    catch (IOException) { } // the tool did not read stdin
                }
                if (!stdout.Wait(CliTimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return (null, $"không trả lời trong {CliTimeoutMs} ms (đã dừng tiến trình con)");
                }
                process.WaitForExit(500);
                string text = stdout.Result.Trim();
                if (text.Length > 0) return (text, null);
                return (null, $"stdout rỗng (exit {(process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?")})");
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or AggregateException)
            {
                return (null, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// TASK-18 / 18a (ADR-71 / 72): the /ptt speech-to-text provider. VAD = False → returns at once (0 token). VAD = True → the
    /// providers of nexus_config.json "stt" in order (auto: lan → cli → sapi, each only when configured; lan / cli: then sapi):
    /// the first non-empty text wins. SAPI = the TASK-18 short-lived worker (en-US), started when capture begins so it is
    /// warm if it is needed. Every failure is a message, never an exception. 0 token, no Gemini.
    /// </summary>
    public sealed class LocalSttService : ISpeechToTextProvider
    {
        public const string SimulateMissingVariable = "NEXUS_STT_SIMULATE_MISSING";
        public const string NoRecognizerError = "no SAPI speech recognizer installed";
        public const string TextPrefix = "[STT ✓] "; // recognized text; every other message starts with "[STT] "

        private readonly Func<SttConfig?> _config;

        public LocalSttService(Func<SttConfig?>? config = null) => _config = config ?? (() => null);

        public string Name => "LocalSttService (lan / cli / SAPI)";

        public bool NeedsAudio => true;

        public LocalSttResult? Last { get; private set; }

        private SttWorkerClient.Pending? _pending;
        private string? _pendingError;

        /// <summary>The providers tried for this config, in order.</summary>
        public static string[] Route(SttConfig c) => c.Provider switch
        {
            "sapi" => new[] { "sapi" },
            "lan" => new[] { "lan", "sapi" },
            "cli" => new[] { "cli", "sapi" },
            _ => new[] { c.LanEndpoint.Length > 0 ? "lan" : null, c.CliPath.Length > 0 ? "cli" : null, "sapi" }.OfType<string>().ToArray()
        };

        public static string DescribeRoute(SttConfig c) =>
            $"{c.Provider}: " + string.Join(" → ", Route(c).Select(p => p switch
            {
                "lan" => $"lan {(c.LanEndpoint.Length > 0 ? c.LanEndpoint : "(chưa cấu hình)")}",
                "cli" => $"cli {(c.CliPath.Length > 0 ? Path.GetFileName(c.CliPath) : "(chưa cấu hình)")}",
                _ => "sapi en-US"
            })) + $" · {c.Language}";

        /// <summary>Capture starts: start the SAPI worker now (it is the last fallback of every route), so it loads while the user speaks.</summary>
        public void BeginSession()
        {
            _pending = null;
            _pendingError = null;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SimulateMissingVariable))) return;
            (_pending, _pendingError) = SttWorkerClient.Start("recognize");
        }

        /// <summary>Session over: a worker that got no audio (silence, failed capture, lan / cli answered) exits by itself.</summary>
        public void EndSession()
        {
            SttWorkerClient.Pending? p = _pending;
            _pending = null;
            if (p != null) SttWorkerClient.Abandon(p);
        }

        public string Transcribe(VadSummary vad, ReadOnlySpan<short> audio)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (!vad.Vad)
                return Finish(t0, new LocalSttResult(true, null, "silence", null, 0, null, null, null, 0, null, string.Empty),
                    "[STT] im lặng (VAD = False): bỏ qua bộ nhận dạng · cục bộ · 0 token");
            SttConfig config = _config() ?? SttConfig.Default;
            var route = new List<string>();
            byte[]? wav = null;
            foreach (string provider in Route(config))
            {
                long tp = Stopwatch.GetTimestamp();
                if (provider is "lan" or "cli")
                {
                    wav ??= SttBridge.ToWav(audio);
                    var (text, error) = provider == "lan"
                        ? (config.LanEndpoint.Length > 0 ? SttBridge.Lan(config.LanEndpoint, wav) : ((string?)null, (string?)"chưa cấu hình"))
                        : SttBridge.Cli(config.CliPath, config.CliArgs, config.Language, wav);
                    route.Add($"{provider} {(text != null ? "✓" : "✗ " + error)} {Ms(tp)} ms");
                    if (text != null)
                    {
                        Array.Clear(wav);
                        return Finish(t0, new LocalSttResult(true, text, "text", null, 0, null, null, provider == "lan" ? config.LanEndpoint : Path.GetFileName(config.CliPath),
                            1, null, string.Empty, provider, string.Join(" → ", route), 0), TextPrefix + text);
                    }
                    continue;
                }
                double fallbackMs = Ms(t0);
                if (wav != null) Array.Clear(wav);
                string result = Sapi(audio, t0, route, fallbackMs);
                return result;
            }
            return Finish(t0, new LocalSttResult(false, null, "fallback", "no provider", 0, null, null, null, 0, null, string.Empty), "[STT] không có bộ nhận dạng nào · 0 token");
        }

        private string Sapi(ReadOnlySpan<short> audio, long t0, List<string> route, double fallbackMs)
        {
            string? simulate = Environment.GetEnvironmentVariable(SimulateMissingVariable);
            if (!string.IsNullOrWhiteSpace(simulate))
            {
                route.Add($"sapi ✗ giả lập thiếu {simulate.Trim()}");
                return Finish(t0, new LocalSttResult(false, null, "fallback", $"simulated missing {simulate.Trim()}", 0, null, null, null, 0, null, string.Empty,
                        "sapi", string.Join(" → ", route), fallbackMs),
                    $"[STT] chưa có bộ nhận dạng ({simulate.Trim()}, giả lập): giữ âm thanh trong RAM, không gửi đi · cục bộ · 0 token");
            }
            long ts = Stopwatch.GetTimestamp();
            byte[] pcm = new byte[audio.Length * 2];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio).CopyTo(pcm);
            SttWorkerClient.Pending? pending = _pending;
            _pending = null;
            var (reply, error, exit, _) = pending != null ? SttWorkerClient.Finish(pending, pcm)
                : _pendingError != null ? (null, _pendingError, null, 0) : SttWorkerClient.Run("recognize", pcm);
            Array.Clear(pcm);
            if (reply is { Ok: true })
            {
                string text = (reply.Text ?? string.Empty).Trim();
                route.Add($"sapi {(text.Length > 0 ? "✓" : "∅")} {Ms(ts)} ms");
                return Finish(t0, new LocalSttResult(true, text.Length > 0 ? text : null, text.Length > 0 ? "text" : "no-text", null, 0,
                        reply.LoadMs, reply.WorkMs, reply.Engine, reply.Confidence, exit, string.Empty, "sapi", string.Join(" → ", route), fallbackMs),
                    text.Length > 0 ? TextPrefix + text : "[STT] có giọng nói nhưng bộ nhận dạng không ra chữ · cục bộ · 0 token");
            }
            route.Add($"sapi ✗ {Ms(ts)} ms");
            return Finish(t0, new LocalSttResult(false, null, "fallback", error ?? reply?.Error, 0, reply?.LoadMs, reply?.WorkMs, reply?.Engine, 0, exit, string.Empty,
                    "sapi", string.Join(" → ", route), fallbackMs),
                $"[STT] bộ nhận dạng cục bộ không dùng được ({(reply?.Missing == true ? "thiếu gói ngôn ngữ" : error ?? reply?.Error)}) · cục bộ · 0 token");
        }

        /// <summary>The recognized text of a transcript this provider produced, or null (silence, fallback, other providers).</summary>
        public static string? RecognizedText(string? transcript) =>
            transcript != null && transcript.StartsWith(TextPrefix, StringComparison.Ordinal) ? transcript.Substring(TextPrefix.Length) : null;

        public string DescribeStatus()
        {
            LocalSttResult? last = Last;
            string? simulate = Environment.GetEnvironmentVariable(SimulateMissingVariable);
            return $"[STT] {DescribeRoute(_config() ?? SttConfig.Default)} · worker SAPI {(VisionWorkerClient.ResolvePath() != null ? "sẵn sàng" : "không tìm thấy")}" +
                   (string.IsNullOrWhiteSpace(simulate) ? string.Empty : $" · giả lập thiếu: {simulate.Trim()}") +
                   (last == null ? " · chưa có phiên nào"
                       : $" · lần cuối: {last.Outcome}{(last.Provider != null ? " qua " + last.Provider : string.Empty)} {last.TotalMs.ToString("0.#", CultureInfo.InvariantCulture)} ms" +
                         (last.Engine != null ? $" ({last.Engine})" : string.Empty) + (last.Error != null ? $" · {last.Error}" : string.Empty)) +
                   " · 0 token";
        }

        private static double Ms(long startTicks) => Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);

        private string Finish(long t0, LocalSttResult partial, string transcript)
        {
            Last = partial with { TotalMs = Ms(t0), AtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            return transcript;
        }
    }

    /// <summary>
    /// TASK-18b (ADR-73): the TCP 5055 STT worker ("McpTools --stt-worker serve"), the server side of the TASK-18a LAN contract:
    /// one connection = one WAV (RIFF, 16 kHz / 16-bit / mono) until the client half-closes, then the UTF-8 text and close.
    /// A refusal is "ERR: ..." (LocalSttService treats it as a failure and falls back to SAPI). Guards: only loopback and
    /// 192.168.1.x peers; WAV &lt;= 2 MB; 3 s read timeout per client; one recognition at a time (SemaphoreSlim(1,1), a
    /// request waits &lt;= 5 s); the CLI gets a temp WAV ("{wav}") that SttBridge.Cli always deletes.
    /// </summary>
    public static class SttLanWorker
    {
        public const string DefaultBind = "0.0.0.0:5055";
        public const string ErrorPrefix = "ERR:";
        public const int MaxWavBytes = 2 * 1024 * 1024;
        public const int ClientReadMs = 3000;
        public const int BusyWaitMs = 5000;
        /// <summary>whisper.cpp "whisper-cli": text only on stdout (-nt no timestamps, -np no progress / system prints).</summary>
        public const string DefaultWhisperArgs = "-f {wav} -l {lang} -nt -np";

        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Loopback (v4 / v6) or 192.168.1.0/24 (the home LAN: PC .212, Nitro 5 .22 / .192).</summary>
        public static bool IsAllowedRemote(System.Net.IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (System.Net.IPAddress.IsLoopback(address)) return true;
            byte[] b = address.GetAddressBytes();
            return b.Length == 4 && b[0] == 192 && b[1] == 168 && b[2] == 1;
        }

        /// <summary>Why a received body is not a WAV this worker accepts, or null.</summary>
        public static string? CheckWav(byte[] wav, int length)
        {
            if (length == 0) return "empty";
            if (length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return "not a WAV";
            if (BitConverter.ToInt16(wav, 22) != 1 || BitConverter.ToInt32(wav, 24) != SttWorkerProtocol.SampleRate || BitConverter.ToInt16(wav, 34) != 16)
                return "WAV must be 16 kHz / 16-bit / mono";
            if (BitConverter.ToInt32(wav, 40) != length - 44) return "truncated WAV";
            return null;
        }

        public static int Serve(string bind, string cliPath, string cliArgs, string language, TextWriter log, CancellationToken cancel)
        {
            int colon = bind.LastIndexOf(':');
            if (colon <= 0 || !System.Net.IPAddress.TryParse(bind.Substring(0, colon), out System.Net.IPAddress? ip) ||
                !int.TryParse(bind.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            {
                log.WriteLine($"[STT-WORKER] --bind \"{bind}\" must be <IPv4>:<port>, e.g. {DefaultBind}");
                return 1;
            }
            var listener = new System.Net.Sockets.TcpListener(ip, port);
            listener.Start();
            log.WriteLine($"[STT-WORKER] {SttWorkerProtocol.Name} listening on {listener.LocalEndpoint} · cli {(cliPath.Length > 0 ? cliPath : "(none)")} {cliArgs} · lang {language} · max {MaxWavBytes} B · peers loopback + 192.168.1.x");
            log.Flush();
            using CancellationTokenRegistration stop = cancel.Register(listener.Stop);
            while (!cancel.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (System.Net.Sockets.SocketException) when (cancel.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => Handle(client, cliPath, cliArgs, language, log));
            }
            log.WriteLine("[STT-WORKER] stopped");
            return 0;
        }

        private static void Handle(System.Net.Sockets.TcpClient client, string cliPath, string cliArgs, string language, TextWriter log)
        {
            long t0 = Stopwatch.GetTimestamp();
            string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
            string outcome;
            using (client)
            {
                try
                {
                    var remote = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
                    if (remote == null || !IsAllowedRemote(remote))
                    {
                        log.WriteLine($"[STT-WORKER] {peer} refused (only loopback and 192.168.1.x)"); // closed without an answer
                        log.Flush();
                        return;
                    }
                    using System.Net.Sockets.NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = ClientReadMs;
                    byte[] body = new byte[MaxWavBytes];
                    int length = 0, n;
                    bool tooBig = false;
                    var chunk = new byte[65536];
                    while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (length + n > MaxWavBytes) { tooBig = true; break; }
                        Buffer.BlockCopy(chunk, 0, body, length, n);
                        length += n;
                    }
                    string? bad = tooBig ? $"WAV > {MaxWavBytes} B" : CheckWav(body, length);
                    string reply;
                    if (bad != null) reply = $"{ErrorPrefix} {bad}";
                    else if (!Gate.Wait(BusyWaitMs)) reply = $"{ErrorPrefix} busy";
                    else
                    {
                        try
                        {
                            byte[] wav = new byte[length];
                            Buffer.BlockCopy(body, 0, wav, 0, length);
                            var (text, error) = SttBridge.Cli(cliPath, cliArgs.Length > 0 ? cliArgs : DefaultWhisperArgs, language, wav);
                            reply = text ?? $"{ErrorPrefix} whisper-cli: {error}";
                        }
                        finally { Gate.Release(); }
                    }
                    Array.Clear(body, 0, length);
                    byte[] bytes = new UTF8Encoding(false).GetBytes(reply);
                    stream.Write(bytes, 0, bytes.Length);
                    client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                    outcome = reply.StartsWith(ErrorPrefix, StringComparison.Ordinal) ? reply : $"ok {reply.Length} chars";
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
                {
                    outcome = $"dropped ({ex.GetType().Name})"; // client cut the connection / timed out: nothing to answer
                }
            }
            log.WriteLine($"[STT-WORKER] {peer} {outcome} {Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 1).ToString(CultureInfo.InvariantCulture)} ms");
            log.Flush();
        }
    }
}
