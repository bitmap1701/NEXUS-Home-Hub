using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-21 (ADR-76): tracks temporary buffers allocated during TTS synthesis; must be 0 B after each call.</summary>
    public static class TtsBufferLedger
    {
        private static long _outstanding;
        private static long _allocations;

        public static long OutstandingBytes => Interlocked.Read(ref _outstanding);

        public static long Allocations => Interlocked.Read(ref _allocations);

        public static void Add(int bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _outstanding, bytes);
            Interlocked.Increment(ref _allocations);
        }

        public static void Release(int bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _outstanding, -bytes);
        }
    }

    /// <summary>TASK-21 (ADR-76): outcome of one LocalTtsService call (kept for /tts status and --selftest local-tts).</summary>
    public sealed record LocalTtsResult(
        bool Ok,
        string Text,
        int WordCount,
        string Outcome,          // "spoken" | "disabled" | "empty" | "fallback"
        string? Error,
        double SanitizeMs,
        double TotalMs,
        string? Provider,        // "lan" | "cli" | "sapi"
        string? Route,           // e.g. "lan ✗ ConnectionRefused 1.2 ms → cli ✗ ... → sapi ✓ 28.4 ms"
        double FallbackMs,
        string? Voice,
        int AudioBytes,
        long OutstandingBytesAfter,
        string AtUtc);

    /// <summary>
    /// TASK-21 (ADR-76): out-of-process bridges for local TTS.
    /// LAN: TCP connect &lt;= 200 ms, send UTF-8 sanitized text, half-close, read acknowledgement or WAV bytes (&lt;= 3000 ms).
    /// CLI: start cliPath with cliArgs ("{text}", "{voice}", or UTF-8 text on stdin), wait &lt;= 3000 ms.
    /// </summary>
    public static class TtsBridge
    {
        public const int LanConnectMs = 200;
        public const int LanReadMs = 3000;
        public const int CliTimeoutMs = 3000;

        public static (bool Ok, int Bytes, string? Error) Lan(string endpoint, string text, string voice, int readMs = LanReadMs)
        {
            long started = Stopwatch.GetTimestamp();
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
                return (false, 0, "lanEndpoint không phải host:port");
            string host = endpoint.Substring(0, colon);
            byte[] payload = new UTF8Encoding(false).GetBytes(text + "\n");
            TtsBufferLedger.Add(payload.Length);
            var buffer = new byte[4096];
            TtsBufferLedger.Add(buffer.Length);
            using var client = new TcpClient { NoDelay = true };
            try
            {
                Task connect = client.ConnectAsync(host, port);
                if (!connect.Wait(LanConnectMs)) return (false, 0, $"không kết nối được trong {LanConnectMs} ms");
                using NetworkStream stream = client.GetStream();
                stream.WriteTimeout = readMs;
                stream.ReadTimeout = readMs;
                stream.Write(payload, 0, payload.Length);
                client.Client.Shutdown(SocketShutdown.Send);
                using var answer = new MemoryStream();
                long deadline = started + readMs * Stopwatch.Frequency / 1000;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    answer.Write(buffer, 0, n);
                    if (Stopwatch.GetTimestamp() > deadline) return (false, 0, $"không trả lời xong trong {readMs} ms");
                }
                byte[] replyBytes = answer.ToArray();
                if (replyBytes.Length == 0) return (false, 0, "trả lời rỗng");
                string replyText = new UTF8Encoding(false).GetString(replyBytes).Trim();
                if (replyText.StartsWith(SttLanWorker.ErrorPrefix, StringComparison.Ordinal))
                    return (false, 0, replyText);
                return (true, replyBytes.Length, null);
            }
            catch (Exception ex) when (ex is SocketException or IOException or AggregateException or ObjectDisposedException)
            {
                Exception inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                return (false, 0, inner is SocketException se ? se.SocketErrorCode.ToString() : inner.GetType().Name);
            }
            finally
            {
                Array.Clear(payload, 0, payload.Length);
                TtsBufferLedger.Release(payload.Length);
                Array.Clear(buffer, 0, buffer.Length);
                TtsBufferLedger.Release(buffer.Length);
            }
        }

        public static (bool Ok, string? Output, string? Error) Cli(string path, string args, string voice, string text, int timeoutMs = CliTimeoutMs)
        {
            if (path.Length == 0) return (false, null, "chưa cấu hình");
            if (!File.Exists(path)) return (false, null, "không có file " + Path.GetFileName(path));
            bool viaArg = args.Contains("{text}", StringComparison.Ordinal);
            string safeText = text.Replace("\"", "'", StringComparison.Ordinal);
            string formattedArgs = args
                .Replace("{text}", safeText, StringComparison.Ordinal)
                .Replace("{voice}", voice, StringComparison.Ordinal);
            byte[] stdinBytes = viaArg ? Array.Empty<byte>() : new UTF8Encoding(false).GetBytes(text + "\n");
            TtsBufferLedger.Add(stdinBytes.Length);
            try
            {
                var info = new ProcessStartInfo(path, formattedArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = !viaArg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false)
                };
                using Process? process = Process.Start(info);
                if (process == null) return (false, null, "Process.Start returned null");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!viaArg)
                {
                    try
                    {
                        process.StandardInput.BaseStream.Write(stdinBytes, 0, stdinBytes.Length);
                        process.StandardInput.Close();
                    }
                    catch (IOException) { }
                }
                if (!stdout.Wait(timeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return (false, null, $"không trả lời trong {timeoutMs} ms (đã dừng tiến trình con)");
                }
                process.WaitForExit(500);
                string output = stdout.Result.Trim();
                if (output.StartsWith(SttLanWorker.ErrorPrefix, StringComparison.Ordinal))
                    return (false, null, output);
                if (process.HasExited && process.ExitCode == 0)
                    return (true, output.Length > 0 ? output : "ok", null);
                string err = stderr.Wait(200) ? stderr.Result.Trim() : string.Empty;
                return (false, null, $"exit {(process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?")}{(err.Length > 0 ? ": " + err : string.Empty)}");
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or AggregateException)
            {
                return (false, null, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (stdinBytes.Length > 0)
                {
                    Array.Clear(stdinBytes, 0, stdinBytes.Length);
                    TtsBufferLedger.Release(stdinBytes.Length);
                }
            }
        }
    }

    /// <summary>
    /// TASK-21 (ADR-76): local voice feedback service (`LocalTtsService`).
    /// Reads "tts" from nexus_config.json (hot-reloaded on /reload). SanitizeForSpeech strips markdown/code and truncates
    /// to &lt;= maxWords (&lt;= 1,0 ms). Route "auto" tries lan (&lt;= 200 ms connect) → cli → sapi (System.Speech.Synthesis,
    /// disposed immediately after speaking or when [MEM] trims). 0 token, no cloud API.
    /// </summary>
    public sealed class LocalTtsService
    {
        public const string SilentEnvironmentVariable = "NEXUS_TTS_SILENT";
        public const string SilentVariable = SilentEnvironmentVariable;
        public const string SimulateMissingVariable = "NEXUS_TTS_SIMULATE_MISSING";

        private readonly Func<TtsConfig?> _config;
        private readonly object _sapiGate = new();
        private bool? _enabledOverride;
        private IDisposable? _activeSynth;
        private int _releasedCount;

        public LocalTtsService(Func<TtsConfig?>? config = null) => _config = config ?? (() => null);

        public LocalTtsResult? Last { get; private set; }

        /// <summary>When true (or when env NEXUS_TTS_SILENT=1), SAPI synthesizes into a MemoryStream/Null stream so no speaker sound plays.</summary>
        public bool NullOutput { get; set; }

        public int ReleasedCount => _releasedCount;

        public TtsConfig CurrentConfig => _config() ?? TtsConfig.Default;

        public bool EffectiveEnabled => _enabledOverride ?? CurrentConfig.Enabled;

        public bool Enabled => EffectiveEnabled;

        public void SetEnabled(bool enabled)
        {
            _enabledOverride = enabled;
            if (!enabled) ReleaseResources();
        }

        /// <summary>Called on /reload so nexus_config.json "tts.enabled" takes effect unless overridden later.</summary>
        public void OnReload() => _enabledOverride = null;

        /// <summary>The providers tried for this config, in order.</summary>
        public static string[] Route(TtsConfig c) => c.Provider switch
        {
            "sapi" => new[] { "sapi" },
            "lan" => new[] { "lan", "sapi" },
            "cli" => new[] { "cli", "sapi" },
            _ => new[] { c.LanEndpoint.Length > 0 ? "lan" : null, c.CliPath.Length > 0 ? "cli" : null, "sapi" }.OfType<string>().ToArray()
        };

        public static string DescribeRoute(TtsConfig c) => DescribeRoute(c, Route(c));

        private static string DescribeRoute(TtsConfig c, string[] route) =>
            $"{(c.Enabled ? "on" : "off")} · {c.Provider}: " + string.Join(" → ", route.Select(p => p switch
            {
                "lan" => $"lan {(c.LanEndpoint.Length > 0 ? c.LanEndpoint : "(chưa cấu hình)")}",
                "cli" => $"cli {(c.CliPath.Length > 0 ? Path.GetFileName(c.CliPath) : "(chưa cấu hình)")}",
                _ => $"sapi ({c.Voice})"
            })) + $" · voice {c.Voice} · maxWords {c.MaxWords}";

        public string DescribeConfig()
        {
            TtsConfig c = CurrentConfig with { Enabled = EffectiveEnabled };
            return DescribeRoute(c);
        }

        public string DescribeStatus()
        {
            LocalTtsResult? last = Last;
            return $"[TTS] trạng thái {(EffectiveEnabled ? "BẬT" : "TẮT")} · {DescribeConfig()}" +
                   (last == null ? " · chưa đọc câu nào"
                       : $" · lần cuối: {last.Outcome}{(last.Provider != null ? " qua " + last.Provider : string.Empty)} ({last.WordCount} từ, {last.TotalMs.ToString("0.#", CultureInfo.InvariantCulture)} ms)" +
                         (last.Voice != null ? $" [{last.Voice}]" : string.Empty) +
                         (last.Error != null ? $" · {last.Error}" : string.Empty)) +
                   " · 0 token";
        }

        public static string DescribeResult(LocalTtsResult r) =>
            r.Ok
                ? $"[TTS ✓] Đã đọc ({r.WordCount} từ, qua {r.Provider ?? "sapi"}, {r.TotalMs.ToString("0.#", CultureInfo.InvariantCulture)} ms): \"{r.Text}\" · cục bộ · 0 token"
                : $"[TTS] Không đọc được ({r.Outcome}{(r.Error != null ? ": " + r.Error : string.Empty)}, {r.TotalMs.ToString("0.#", CultureInfo.InvariantCulture)} ms) · cục bộ · 0 token";

        /// <summary>
        /// Pure function (no Regex): strips fenced code blocks, inline code/markdown formatting, links and bracket tags,
        /// collapses whitespace, and truncates to at most <paramref name="maxWords"/> words (&lt;= 1,0 ms).
        /// </summary>
        public static string SanitizeForSpeech(string? text, int maxWords, out int wordCount, out double elapsedMs)
        {
            long t0 = Stopwatch.GetTimestamp();
            string clean = SanitizeForSpeech(text, maxWords);
            wordCount = CountWords(clean);
            elapsedMs = Ms(t0);
            return clean;
        }

        public static string SanitizeForSpeech(string? text, int maxWords = TtsConfig.DefaultMaxWords)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            maxWords = Math.Clamp(maxWords, TtsConfig.MinMaxWords, TtsConfig.MaxMaxWords);
            ReadOnlySpan<char> span = text.AsSpan();
            var sb = new StringBuilder(Math.Min(span.Length, maxWords * 12));
            int i = 0, len = span.Length;
            while (i < len)
            {
                char ch = span[i];
                // 1. Fenced code block ``` ... ``` -> skip completely
                if (ch == '`' && i + 2 < len && span[i + 1] == '`' && span[i + 2] == '`')
                {
                    i += 3;
                    while (i < len)
                    {
                        if (span[i] == '`' && i + 2 < len && span[i + 1] == '`' && span[i + 2] == '`')
                        {
                            i += 3;
                            break;
                        }
                        i++;
                    }
                    sb.Append(' ');
                    continue;
                }
                // 2. Inline code `...` -> skip completely (code should not be read out loud)
                if (ch == '`')
                {
                    int close = i + 1;
                    while (close < len && span[close] != '`' && span[close] != '\n') close++;
                    if (close < len && span[close] == '`')
                    {
                        sb.Append(' ');
                        i = close + 1;
                        continue;
                    }
                    i++;
                    continue;
                }
                // 3. Bracket tags [ASK #1] vs markdown links [label](url)
                if (ch == '[')
                {
                    int close = i + 1;
                    while (close < len && span[close] != ']' && span[close] != '\n') close++;
                    if (close < len && span[close] == ']')
                    {
                        if (close + 1 < len && span[close + 1] == '(')
                        {
                            // Markdown link [label](url) -> keep label, skip (url)
                            sb.Append(span.Slice(i + 1, close - i - 1));
                            int parenClose = close + 2;
                            while (parenClose < len && span[parenClose] != ')' && span[parenClose] != '\n') parenClose++;
                            i = parenClose < len && span[parenClose] == ')' ? parenClose + 1 : close + 1;
                            continue;
                        }
                        // Bracket tag like [STT ✓] or [ASK #1] -> skip
                        i = close + 1;
                        sb.Append(' ');
                        continue;
                    }
                }
                // 4. Skip bare http(s):// URLs
                if ((ch == 'h' || ch == 'H') && i + 7 < len &&
                    (span.Slice(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     span.Slice(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    while (i < len && !char.IsWhiteSpace(span[i])) i++;
                    sb.Append(' ');
                    continue;
                }
                // 5. Strip markdown formatting chars and bullets
                if (ch is '*' or '_' or '~' or '`')
                {
                    i++;
                    continue;
                }
                if (ch is '#' or '>' or '|' or '•' or '<')
                {
                    sb.Append(' ');
                    i++;
                    continue;
                }
                if (ch is '-' or '–' or '—')
                {
                    bool lineStart = i == 0 || span[i - 1] is '\n' or '\r' or ' ';
                    if (lineStart && i + 1 < len && span[i + 1] == ' ')
                    {
                        i += 2;
                        sb.Append(' ');
                        continue;
                    }
                }
                sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
                i++;
            }

            // Collapse spaces and take <= maxWords words
            var result = new StringBuilder(sb.Length);
            int words = 0;
            bool inWord = false;
            for (int j = 0; j < sb.Length; j++)
            {
                char c = sb[j];
                if (c == ' ')
                {
                    if (inWord)
                    {
                        inWord = false;
                        if (words >= maxWords) break;
                        result.Append(' ');
                    }
                }
                else
                {
                    if (!inWord)
                    {
                        inWord = true;
                        words++;
                    }
                    result.Append(c);
                }
            }
            while (result.Length > 0 && result[result.Length - 1] is ' ' or ',' or ';' or ':')
                result.Length--;
            return result.ToString();
        }

        public static int CountWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int count = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) inWord = false;
                else if (!inWord) { inWord = true; count++; }
            }
            return count;
        }

        /// <summary>Releases any active synthesizer immediately (called after speech, on /tts off, and on [MEM] idle trim).</summary>
        public void ReleaseResources()
        {
            lock (_sapiGate)
            {
                IDisposable? s = _activeSynth;
                _activeSynth = null;
                if (s != null)
                {
                    try
                    {
                        if (OperatingSystem.IsWindows() && s is System.Speech.Synthesis.SpeechSynthesizer synth)
                        {
                            synth.SpeakAsyncCancelAll();
                            synth.SetOutputToNull();
                        }
                        s.Dispose();
                    }
                    catch (Exception) { }
                }
                Interlocked.Increment(ref _releasedCount);
            }
        }

        /// <summary>Warms up SAPI voice enumeration/engine once (to null/memory stream) and returns the cold-start ms.</summary>
        public static double WarmUpSapi()
        {
            if (!OperatingSystem.IsWindows()) return 0;
            long t0 = Stopwatch.GetTimestamp();
            var tmp = new LocalTtsService();
            _ = tmp.SpeakSapi("ok", "vi-VN", nullOutput: true);
            return Ms(t0);
        }

        public LocalTtsResult Speak(string? rawText, bool silent) =>
            Speak(rawText, forceNullOutput: silent, requireEnabled: false);

        public LocalTtsResult Speak(string? rawText, bool? forceNullOutput = null, bool requireEnabled = false)
        {
            long t0 = Stopwatch.GetTimestamp();
            TtsConfig config = CurrentConfig;
            string clean = SanitizeForSpeech(rawText, config.MaxWords);
            double sanitizeMs = Ms(t0);
            int words = CountWords(clean);
            if (requireEnabled && !EffectiveEnabled)
            {
                return Finish(t0, new LocalTtsResult(false, clean, words, "disabled", "TTS đang tắt", sanitizeMs, 0, null, null, 0, null, 0,
                    TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
            }
            if (words == 0)
            {
                return Finish(t0, new LocalTtsResult(false, string.Empty, 0, "empty", "không có nội dung sau khi lọc", sanitizeMs, 0, null, null, 0, null, 0,
                    TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
            }

            bool nullOutput = forceNullOutput ?? (NullOutput || Environment.GetEnvironmentVariable(SilentEnvironmentVariable) == "1");
            var route = new List<string>();
            foreach (string provider in Route(config))
            {
                long tp = Stopwatch.GetTimestamp();
                if (provider == "lan")
                {
                    var (ok, bytes, error) = config.LanEndpoint.Length > 0
                        ? TtsBridge.Lan(config.LanEndpoint, clean, config.Voice)
                        : (false, 0, "chưa cấu hình");
                    route.Add($"lan {(ok ? "✓" : "✗ " + error)} {Ms(tp)} ms");
                    if (ok)
                    {
                        return Finish(t0, new LocalTtsResult(true, clean, words, "spoken", null, sanitizeMs, 0, "lan",
                            string.Join(" → ", route), 0, config.LanEndpoint, bytes,
                            TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
                    }
                    continue;
                }
                if (provider == "cli")
                {
                    var (ok, output, error) = TtsBridge.Cli(config.CliPath, config.CliArgs, config.Voice, clean);
                    route.Add($"cli {(ok ? "✓" : "✗ " + error)} {Ms(tp)} ms");
                    if (ok)
                    {
                        return Finish(t0, new LocalTtsResult(true, clean, words, "spoken", null, sanitizeMs, 0, "cli",
                            string.Join(" → ", route), 0, Path.GetFileName(config.CliPath), output?.Length ?? 0,
                            TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
                    }
                    continue;
                }
                double fallbackMs = Ms(t0);
                return SpeakSapiRoute(clean, words, config.Voice, nullOutput, t0, sanitizeMs, route, fallbackMs);
            }
            return Finish(t0, new LocalTtsResult(false, clean, words, "fallback", "no provider", sanitizeMs, 0, null,
                string.Join(" → ", route), 0, null, 0, TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
        }

        private LocalTtsResult SpeakSapiRoute(string clean, int words, string voiceHint, bool nullOutput, long t0, double sanitizeMs, List<string> route, double fallbackMs)
        {
            string? simulate = Environment.GetEnvironmentVariable(SimulateMissingVariable);
            if (!string.IsNullOrWhiteSpace(simulate))
            {
                route.Add($"sapi ✗ giả lập thiếu {simulate.Trim()}");
                return Finish(t0, new LocalTtsResult(false, clean, words, "fallback", $"simulated missing {simulate.Trim()}", sanitizeMs, 0,
                    "sapi", string.Join(" → ", route), fallbackMs, null, 0, TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
            }
            if (!OperatingSystem.IsWindows())
            {
                route.Add("sapi ✗ not Windows");
                return Finish(t0, new LocalTtsResult(false, clean, words, "fallback", "SAPI requires Windows", sanitizeMs, 0,
                    "sapi", string.Join(" → ", route), fallbackMs, null, 0, TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
            }
            long ts = Stopwatch.GetTimestamp();
            var (ok, error, voiceUsed, audioBytes) = SpeakSapi(clean, voiceHint, nullOutput);
            route.Add($"sapi {(ok ? "✓" : "✗ " + error)} {Ms(ts)} ms");
            return Finish(t0, new LocalTtsResult(ok, clean, words, ok ? "spoken" : "fallback", error, sanitizeMs, 0,
                "sapi", string.Join(" → ", route), fallbackMs, voiceUsed, audioBytes,
                TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes, string.Empty));
        }

        [SupportedOSPlatform("windows")]
        private (bool Ok, string? Error, string? Voice, int AudioBytes) SpeakSapi(string text, string voiceHint, bool nullOutput)
        {
            int trackedBytes = 0;
            lock (_sapiGate)
            {
                System.Speech.Synthesis.SpeechSynthesizer? synth = null;
                MemoryStream? pcm = null;
                try
                {
                    synth = new System.Speech.Synthesis.SpeechSynthesizer();
                    _activeSynth = synth;
                    var voices = synth.GetInstalledVoices().Where(v => v.Enabled).ToList();
                    if (voices.Count == 0)
                        return (false, "no SAPI voice installed", null, 0);
                    System.Speech.Synthesis.InstalledVoice pick =
                        voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name.Equals(voiceHint, StringComparison.OrdinalIgnoreCase)) ??
                        voices.FirstOrDefault(v => v.VoiceInfo.Name.Contains(voiceHint, StringComparison.OrdinalIgnoreCase)) ??
                        voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name.StartsWith("vi", StringComparison.OrdinalIgnoreCase)) ??
                        voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name == "en-US") ??
                        voices[0];
                    synth.SelectVoice(pick.VoiceInfo.Name);
                    string voiceLabel = $"{pick.VoiceInfo.Name} ({pick.VoiceInfo.Culture.Name})";
                    if (nullOutput)
                    {
                        pcm = new MemoryStream(32768);
                        trackedBytes = 32768;
                        TtsBufferLedger.Add(trackedBytes);
                        var format = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                            SttWorkerProtocol.SampleRate,
                            System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                            System.Speech.AudioFormat.AudioChannel.Mono);
                        synth.SetOutputToAudioStream(pcm, format);
                        synth.Speak(text);
                        synth.SetOutputToNull();
                        int written = (int)pcm.Length;
                        return (written > 0, written > 0 ? null : "SAPI produced 0 bytes", voiceLabel, written);
                    }
                    synth.SetOutputToDefaultAudioDevice();
                    synth.Speak(text);
                    synth.SetOutputToNull();
                    return (true, null, voiceLabel, 0);
                }
                catch (Exception ex)
                {
                    return (false, $"{ex.GetType().Name}: {ex.Message}", null, 0);
                }
                finally
                {
                    _activeSynth = null;
                    if (synth != null)
                    {
                        try { synth.SetOutputToNull(); } catch (Exception) { }
                        try { synth.Dispose(); } catch (Exception) { }
                    }
                    if (pcm != null)
                    {
                        try
                        {
                            if (pcm.TryGetBuffer(out ArraySegment<byte> seg) && seg.Array != null)
                                Array.Clear(seg.Array, seg.Offset, seg.Count);
                        }
                        catch (Exception) { }
                        pcm.Dispose();
                    }
                    if (trackedBytes > 0) TtsBufferLedger.Release(trackedBytes);
                    Interlocked.Increment(ref _releasedCount);
                }
            }
        }

        private LocalTtsResult Finish(long t0, LocalTtsResult partial)
        {
            Last = partial with
            {
                TotalMs = Ms(t0),
                OutstandingBytesAfter = TtsBufferLedger.OutstandingBytes + PcmBufferLedger.OutstandingBytes,
                AtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            return Last;
        }

        private static double Ms(long startTicks) => Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
