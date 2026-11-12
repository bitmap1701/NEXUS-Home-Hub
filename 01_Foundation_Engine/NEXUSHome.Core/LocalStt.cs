using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
        string AtUtc);

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
        public static int Run(string mode, Stream input, Stream output)
        {
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
    public sealed class LocalSttService : ISpeechToTextProvider
    {
        public const string SimulateMissingVariable = "NEXUS_STT_SIMULATE_MISSING";
        public const string NoRecognizerError = "no SAPI speech recognizer installed";
        public const string TextPrefix = "[STT ✓] "; // recognized text; every other message starts with "[STT] "

        public string Name => "LocalSttService (SAPI, worker)";

        public bool NeedsAudio => true;

        public LocalSttResult? Last { get; private set; }

        private SttWorkerClient.Pending? _pending;
        private string? _pendingError;

        /// <summary>Capture starts: start the worker now, so it loads while the user speaks (not when the simulation says it is missing).</summary>
        public void BeginSession()
        {
            _pending = null;
            _pendingError = null;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SimulateMissingVariable))) return;
            (_pending, _pendingError) = SttWorkerClient.Start("recognize");
        }

        /// <summary>Session over: a worker that got no audio (silence, failed capture) is told so and exits by itself.</summary>
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
            string? simulate = Environment.GetEnvironmentVariable(SimulateMissingVariable);
            if (!string.IsNullOrWhiteSpace(simulate))
                return Finish(t0, new LocalSttResult(false, null, "fallback", $"simulated missing {simulate.Trim()}", 0, null, null, null, 0, null, string.Empty),
                    $"[STT] chưa có bộ nhận dạng ({simulate.Trim()}, giả lập): giữ âm thanh trong RAM, không gửi đi · cục bộ · 0 token");
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
                return Finish(t0, new LocalSttResult(true, text.Length > 0 ? text : null, text.Length > 0 ? "text" : "no-text", null, 0,
                        reply.LoadMs, reply.WorkMs, reply.Engine, reply.Confidence, exit, string.Empty),
                    text.Length > 0 ? TextPrefix + text : "[STT] có giọng nói nhưng bộ nhận dạng không ra chữ · cục bộ · 0 token");
            }
            return Finish(t0, new LocalSttResult(false, null, "fallback", error ?? reply?.Error, 0, reply?.LoadMs, reply?.WorkMs, reply?.Engine, 0, exit, string.Empty),
                $"[STT] bộ nhận dạng cục bộ không dùng được ({(reply?.Missing == true ? "thiếu gói ngôn ngữ" : error ?? reply?.Error)}) · cục bộ · 0 token");
        }

        /// <summary>The recognized text of a transcript this provider produced, or null (silence, fallback, other providers).</summary>
        public static string? RecognizedText(string? transcript) =>
            transcript != null && transcript.StartsWith(TextPrefix, StringComparison.Ordinal) ? transcript.Substring(TextPrefix.Length) : null;

        public string DescribeStatus()
        {
            LocalSttResult? last = Last;
            string? simulate = Environment.GetEnvironmentVariable(SimulateMissingVariable);
            return $"[STT] {Name} · en-US dictation · worker {(VisionWorkerClient.ResolvePath() != null ? "sẵn sàng" : "không tìm thấy")}" +
                   (string.IsNullOrWhiteSpace(simulate) ? string.Empty : $" · giả lập thiếu: {simulate.Trim()}") +
                   (last == null ? " · chưa có phiên nào"
                       : $" · lần cuối: {last.Outcome} {last.TotalMs.ToString("0.#", CultureInfo.InvariantCulture)} ms" +
                         (last.Engine != null ? $" ({last.Engine})" : string.Empty) + (last.Error != null ? $" · {last.Error}" : string.Empty)) +
                   " · 0 token";
        }

        private string Finish(long t0, LocalSttResult partial, string transcript)
        {
            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            Last = partial with { TotalMs = ms, AtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            return transcript;
        }
    }
}
