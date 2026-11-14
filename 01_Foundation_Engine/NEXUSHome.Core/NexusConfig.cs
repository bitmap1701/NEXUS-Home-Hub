using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-06 ADR-20: operating parameters from 03_Local_Data_Vault/config/nexus_config.json (UTF-8, no BOM).
    /// The 9 fields of the spec plus <see cref="CombatProcessPrefixes"/> (optional): the processes that switch the
    /// dispatch SLA to the Live Combat profile (ADR-17). It is separate from <see cref="ProtectedProcessPrefixes"/>
    /// because that list also holds always-running system processes (explorer, dwm...), which would otherwise put
    /// the machine permanently in combat mode.
    /// </summary>
    /// <summary>TASK-17a ADR-69: one /net ping target of nexus_config.json "networkTargets".</summary>
    public sealed record NetworkTargetConfig(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("host")] string Host);

    /// <summary>
    /// TASK-18a ADR-72: optional "stt" of nexus_config.json. Provider "auto" tries lan → cli → sapi (en-US), skipping what is
    /// not configured; "lan" / "cli" fall back to sapi when they fail; "sapi" = TASK-18. Language is passed to lan / cli.
    /// TASK-20a: lanTimeoutMs = how long the PC waits for the LAN worker's answer (1000–30000, default 10000; the connect
    /// stays &lt;= 200 ms so a sleeping Nitro 5 is still noticed at once). Measured 2026-11-14: whisper small on the Nitro 5 CPU
    /// takes 2,4–3,6 s for a 3 s phrase, the old fixed 2000 ms always fell back to SAPI.
    /// </summary>
    public sealed record SttConfig(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("lanEndpoint")] string LanEndpoint,
        [property: JsonPropertyName("cliPath")] string CliPath,
        [property: JsonPropertyName("cliArgs")] string CliArgs,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("lanTimeoutMs")] int LanTimeoutMs = SttConfig.DefaultLanTimeoutMs)
    {
        public const int DefaultLanTimeoutMs = 10000;
        public const int MinLanTimeoutMs = 1000;
        public const int MaxLanTimeoutMs = 30000;

        public static readonly SttConfig Default = new("auto", string.Empty, string.Empty, string.Empty, "vi-VN");
    }

    /// <summary>
    /// TASK-21 ADR-76: optional "tts" of nexus_config.json. Provider "auto" tries lan → cli → sapi, skipping what is not
    /// configured; "lan" / "cli" fall back to sapi when they fail; "sapi" uses System.Speech.Synthesis (disposed after each
    /// call or on [MEM]). SanitizeForSpeech strips markdown/code and cuts at &lt;= maxWords (default 25, clamped 1..100).
    /// </summary>
    public sealed record TtsConfig(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("lanEndpoint")] string LanEndpoint,
        [property: JsonPropertyName("cliPath")] string CliPath,
        [property: JsonPropertyName("cliArgs")] string CliArgs,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("maxWords")] int MaxWords = TtsConfig.DefaultMaxWords)
    {
        public const int DefaultMaxWords = 25;
        public const int MinMaxWords = 1;
        public const int MaxMaxWords = 100;

        public static readonly TtsConfig Default = new(true, "auto", string.Empty, string.Empty, string.Empty, "vi-VN", DefaultMaxWords);
    }

    public sealed record NexusConfig(
        string DefaultNexusMode,
        IReadOnlyList<string> PreferredMonitorKeywords,
        string PassiveHudHotkey,
        string CommandModeHotkey,
        int MemoryTrimFloorMb,
        int WatcherDebounceMs,
        int DispatchSlaDesktopMs,
        int DispatchSlaCombatMs,
        IReadOnlyList<string> ProtectedProcessPrefixes,
        IReadOnlyList<string> CombatProcessPrefixes,
        string GeminiModel,
        int GeminiTimeoutSeconds,
        int GeminiMaxOutputTokens,
        double GeminiTemperature,
        bool AutoQaAfterAsk,
        int MaxConversationTurns,
        int MaxRequestsPerMinute = 10,
        int MaxTokensPerMinute = 30000,
        int MaxDailyTokens = 250000,
        int HttpMaxRetries = 2,
        int HttpRetryBaseDelayMs = 300,
        int MaxPinnedCards = 5,   // TASK-13: Right MFD pin board cap (1..5)
        int MaxVisionImageBytes = 256000,   // TASK-12 ADR-61: JPEG budget of /img and /snap (250 KB)
        int MaxVisionDimensionPx = 1280,    // TASK-12 ADR-61: longest edge after scaling
        IReadOnlyList<string>? PrivacyRedactedWindowKeywords = null, // TASK-12: window titles that block /snap (null = defaults)
        int MaxSourceDimensionPx = 4096,    // TASK-12b: /img sources with a longer edge are refused before decoding
        string VisionWorker = "process",    // TASK-12c: "process" = decode/compress in a short-lived McpTools --vision-worker; "inproc" = in this process
        string EqualizerApoConfigDir = @"C:\Program Files\EqualizerAPO\config", // TASK-16 U2 (env NEXUS_EQ_CONFIG_DIR overrides)
        bool YoutubeEqAutoSwitch = true,    // TASK-16 U2: YouTube in a browser on the G272 -> nexus_k450_youtube.txt
        int YoutubeCheckDebounceMs = 500,   // TASK-16 U2: window events (focus / title / move / minimise) settle this long, then one check
        string WatchWebhookUrl = "",        // TASK-16 U3: http(s) URL for /timer expiry and 3-strike escalation (env NEXUS_WATCH_WEBHOOK_URL overrides)
        bool ShiftCompleteSound = false,    // TASK-16b: system "asterisk" sound when MCP notify_shift_complete arrives
        // TASK-17a ADR-69: /net LAN targets (null = the 4 defaults of NetworkRadar; never written when null)
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NetworkTargetConfig>? NetworkTargets = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SttConfig? Stt = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TtsConfig? Tts = null)
    {
        /// <summary>TASK-12 §3.3: default privacy denylist (case-insensitive substrings of window titles).</summary>
        public static IReadOnlyList<string> DefaultPrivacyRedactedWindowKeywords { get; } = new[]
        {
            "Password", "1Password", "Bitwarden", "KeePass", "Bank", "Vietcombank", "Techcombank", "TPBank", "MoMo", "Binance",
            ".env", "gemini_key", "AI Studio", "Incognito", "InPrivate"
        };

        public static NexusConfig Default { get; } = new(
            "Micro",
            new[] { "MSI", "G272" },
            "Alt+Space",
            "Ctrl+Shift+Space",
            50,
            150,
            10,
            25,
            new[]
            {
                "DeltaForce", "df_launcher", "TslGame", "ExecPubg", "fczf", "fifazf", "Roblox",
                "SGuard", "ACE-", "BEService", "BEClient", "zksvc",
                "audiodg", "dwm", "csrss", "explorer", "nvcontainer", "NVDisplay", "NEXUSHome"
            },
            // TASK-14 decision (5): Overwolf is NOT a combat process (it also runs in the background on the desktop and would
            // keep the SLA in LiveCombat); same list as nexus_config.json on Nhat Hoa's PC.
            new[] { "DeltaForce", "TslGame", "SGuard" },
            // TASK-07 §3.2 (optional in the file: older nexus_config.json files get these defaults)
            // TASK-11b Hạng mục D: gemini-2.5-flash answers HTTP 404 for the current key -> default gemini-3.5-flash-lite
            GeminiAgentClient.FallbackModel,
            25,
            1024,
            0.3,
            false, // TASK-08 §3.3.1: /ask runs the QA agent automatically only when true (or with --auto-qa)
            6,     // TASK-09 ADR-37: user/model turn pairs sent with /followup (optional in the file)
            // TASK-10 §3.1.2 (ADR-47): client-side rate/token budget guard & HTTP 429/5xx backoff
            10,
            30000,
            250000,
            2,
            300,
            5,
            256000,
            1280,
            DefaultPrivacyRedactedWindowKeywords);
    }

    public sealed record NexusConfigLoadResult(
        bool Ok,
        bool Created,
        bool Restored,
        string? BackupPath,
        IReadOnlyList<string> Warnings,
        string? Error,
        string LoadedAtUtc);

    /// <summary>ADR-17: which P95 dispatch budget applies right now.</summary>
    public sealed record DispatchSlaProfile(string Profile, double BudgetMs, IReadOnlyList<string> MatchedProcesses);

    /// <summary>A global hotkey such as "Ctrl+Shift+Space" (RegisterHotKey modifiers + virtual key).</summary>
    public sealed record HotkeyChord(uint Modifiers, uint VirtualKey, string Text)
    {
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;

        private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20, ["Backspace"] = 0x08, ["Back"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D,
            ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["`"] = 0xC0, ["Oem3"] = 0xC0
        };

        /// <summary>
        /// "Alt+Space", "Ctrl+Shift+Space", "Win+F9"... At least one of Alt / Ctrl / Win is required so a global
        /// hotkey never swallows plain typing.
        /// </summary>
        public static bool TryParse(string? text, out HotkeyChord? chord)
        {
            chord = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;

            uint modifiers = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "alt": modifiers |= ModAlt; break;
                    case "ctrl":
                    case "control": modifiers |= ModControl; break;
                    case "shift": modifiers |= ModShift; break;
                    case "win":
                    case "windows": modifiers |= ModWin; break;
                    default: return false;
                }
            }
            if ((modifiers & (ModAlt | ModControl | ModWin)) == 0) return false;

            string key = parts[^1];
            uint vk;
            if (NamedKeys.TryGetValue(key, out uint named)) vk = named;
            else if (key.Length == 1 && char.IsLetterOrDigit(key[0]) && key[0] < 128) vk = char.ToUpperInvariant(key[0]);
            else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 24)
                vk = (uint)(0x70 + f - 1);
            else return false;

            chord = new HotkeyChord(modifiers, vk, text.Trim());
            return true;
        }
    }

    /// <summary>Temp file + replace, UTF-8 without BOM, retried briefly if a reader holds the target (shared helper).</summary>
    public static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// TASK-11b: same temp-file + replace as <see cref="WriteAllText"/>, for bytes already encoded (no BOM), with a
        /// 1-byte FileStream buffer so the bytes go to the OS in one write. The folder is created only when missing.
        /// </summary>
        public static string? WriteAllBytes(string path, byte[] bytes) => WriteAllBytes(path, bytes, out _, out _);

        /// <param name="tempWriteMs">TASK-11b profile: create + write + close of the temp file.</param>
        /// <param name="moveMs">TASK-11b profile: File.Move(temp, path, overwrite) including retries.</param>
        public static string? WriteAllBytes(string path, byte[] bytes, out double tempWriteMs, out double moveMs)
        {
            tempWriteMs = 0;
            moveMs = 0;
            string temp = $"{path}.{Environment.ProcessId}.tmp";
            long ts0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                try
                {
                    WriteTemp(temp, bytes);
                }
                catch (DirectoryNotFoundException)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    WriteTemp(temp, bytes);
                }
                long ts1 = System.Diagnostics.Stopwatch.GetTimestamp();
                tempWriteMs = Math.Round((ts1 - ts0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3);
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(temp, path, overwrite: true);
                        moveMs = Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - ts1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3);
                        return null;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void WriteTemp(string temp, byte[] bytes)
        {
            using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None);
            stream.Write(bytes, 0, bytes.Length);
        }

        public static string? WriteAllText(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = $"{path}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, text, Utf8NoBom);
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(temp, path, overwrite: true);
                        return null;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>
    /// Loads / reloads nexus_config.json with fallback resilience (ADR-20):
    /// - missing folder or file: created with <see cref="NexusConfig.Default"/>;
    /// - JSON syntax error: nothing applied, the last valid configuration is kept, the broken file is copied to
    ///   nexus_config.json.invalid-&lt;UTC&gt; and the file is rewritten from the configuration in use;
    /// - missing / empty / out-of-range field: that field keeps its last valid value (warning), the rest applies.
    /// Never throws. Applying pushes the GameBoost settings (protected prefixes, trim floor) to this process.
    /// </summary>
    public sealed class NexusConfigService
    {
        public const string FileName = "nexus_config.json";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        private static readonly JsonDocumentOptions ParseOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly object _gate = new();

        public NexusConfigService(string configPath)
        {
            ConfigPath = configPath;
            Current = NexusConfig.Default;
            LastResult = Load();
        }

        public string ConfigPath { get; }

        public NexusConfig Current { get; private set; }

        public NexusConfigLoadResult LastResult { get; private set; }

        /// <summary>Short SHA-256 of the configuration in use (same file + same logic => same value in every process).</summary>
        public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(Current)))).Substring(0, 12).ToLowerInvariant();

        public NexusConfigLoadResult Reload() => Load();

        public static string Serialize(NexusConfig config) => JsonSerializer.Serialize(config, Json);

        /// <summary>ADR-17: Live Combat when a process matching <see cref="NexusConfig.CombatProcessPrefixes"/> runs.</summary>
        public DispatchSlaProfile DetectSla() => DetectSla(Current);

        public static DispatchSlaProfile DetectSla(NexusConfig config)
        {
            var matched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (config.CombatProcessPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                            matched.Add(name);
                    }
                    catch (InvalidOperationException)
                    {
                        // exited meanwhile
                    }
                }
            }
            return matched.Count > 0
                ? new DispatchSlaProfile("LiveCombat", config.DispatchSlaCombatMs, matched.ToArray())
                : new DispatchSlaProfile("Desktop", config.DispatchSlaDesktopMs, Array.Empty<string>());
        }

        /// <summary>Text for the Cockpit "/config" command.</summary>
        public string Describe()
        {
            NexusConfig c = Current;
            DispatchSlaProfile sla = DetectSla(c);
            return
                $"[CONFIG] {ConfigPath} (fingerprint {Fingerprint})\n" +
                $"  defaultNexusMode={c.DefaultNexusMode} | memoryTrimFloorMb={c.MemoryTrimFloorMb} | watcherDebounceMs={c.WatcherDebounceMs}\n" +
                $"  preferredMonitorKeywords=[{string.Join(", ", c.PreferredMonitorKeywords)}]\n" +
                $"  passiveHudHotkey={c.PassiveHudHotkey} | commandModeHotkey={c.CommandModeHotkey} (áp dụng khi khởi động lại HostService)\n" +
                $"  dispatchSla: desktop<{c.DispatchSlaDesktopMs} ms, combat<{c.DispatchSlaCombatMs} ms → hiện tại {sla.Profile} (<{sla.BudgetMs} ms)" +
                (sla.MatchedProcesses.Count > 0 ? $" do [{string.Join(", ", sla.MatchedProcesses)}]" : string.Empty) + "\n" +
                $"  protectedProcessPrefixes ({c.ProtectedProcessPrefixes.Count}): {string.Join(", ", c.ProtectedProcessPrefixes)}\n" +
                $"  combatProcessPrefixes: {string.Join(", ", c.CombatProcessPrefixes)}\n" +
                $"  gemini: model={c.GeminiModel}, timeout={c.GeminiTimeoutSeconds}s, maxOutputTokens={c.GeminiMaxOutputTokens}, " +
                $"temperature={c.GeminiTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}, autoQaAfterAsk={c.AutoQaAfterAsk}, " +
                $"maxConversationTurns={c.MaxConversationTurns}\n" +
                $"  quotaGuard: maxRequestsPerMinute={c.MaxRequestsPerMinute}, maxTokensPerMinute={c.MaxTokensPerMinute}, " +
                $"maxDailyTokens={c.MaxDailyTokens} | backoff: httpMaxRetries={c.HttpMaxRetries}, httpRetryBaseDelayMs={c.HttpRetryBaseDelayMs}\n" +
                $"  rightMfd: maxPinnedCards={c.MaxPinnedCards}\n" +
                $"  vision: maxVisionImageBytes={c.MaxVisionImageBytes}, maxVisionDimensionPx={c.MaxVisionDimensionPx}, maxSourceDimensionPx={c.MaxSourceDimensionPx}, visionWorker={c.VisionWorker}, " +
                $"privacyRedactedWindowKeywords ({(c.PrivacyRedactedWindowKeywords ?? NexusConfig.DefaultPrivacyRedactedWindowKeywords).Count})\n" +
                $"  ui-v2: equalizerApoConfigDir={c.EqualizerApoConfigDir}, youtubeEqAutoSwitch={c.YoutubeEqAutoSwitch.ToString().ToLowerInvariant()}, " +
                $"youtubeCheckDebounceMs={c.YoutubeCheckDebounceMs}, watchWebhook={(WatchWebhook.ResolveUrl(c) != null ? "on" : "off")}, shiftCompleteSound={c.ShiftCompleteSound}\n" +
                $"  net: networkTargets=" + (c.NetworkTargets is { Count: > 0 } t
                    ? $"[{string.Join(", ", t.Select(x => $"{x.Label} {x.Host}"))}] (nexus_config.json)"
                    : "4 đích mặc định (Gateway .1, NITRO5-LAN .22, NITRO5-WIFI .192, LAN-TV .97)") + "\n" +
                $"  stt: " + (c.Stt is { } s ? LocalSttService.DescribeRoute(s) : "mặc định (auto → SAPI en-US)") + "\n" + // TASK-18a
                $"  tts: " + (c.Tts is { } tts ? LocalTtsService.DescribeRoute(tts) : "mặc định (enabled, auto → SAPI, maxWords 25)"); // TASK-21
        }

        private NexusConfigLoadResult Load()
        {
            lock (_gate)
            {
                string now = DateTime.UtcNow.ToString("o");
                try
                {
                    if (!File.Exists(ConfigPath))
                    {
                        string? createError = AtomicFile.WriteAllText(ConfigPath, Serialize(Current));
                        Apply(Current);
                        return LastResult = new NexusConfigLoadResult(createError == null, createError == null, false, null,
                            Array.Empty<string>(), createError, now);
                    }

                    byte[] bytes = ReadShared(ConfigPath);
                    bool hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    string text = Encoding.UTF8.GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));

                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(text, ParseOptions);
                    }
                    catch (JsonException ex)
                    {
                        return LastResult = RestoreBrokenFile($"JSON không hợp lệ: {ex.Message}", now);
                    }

                    using (document)
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            return LastResult = RestoreBrokenFile("JSON gốc phải là một object { ... }.", now);

                        var warnings = new List<string>();
                        NexusConfig merged = Merge(document.RootElement, Current, warnings);
                        Current = merged;
                        Apply(merged);

                        if (hadBom)
                        {
                            // ADR-16/20: no BOM. The content is rewritten from the configuration just applied.
                            AtomicFile.WriteAllText(ConfigPath, Serialize(merged));
                            warnings.Add("File có BOM: đã ghi lại dạng UTF-8 không BOM.");
                        }
                        return LastResult = new NexusConfigLoadResult(true, false, false, null, warnings, null, now);
                    }
                }
                catch (Exception ex)
                {
                    // I/O problems (file locked by an editor, no permission...): keep running with what we have.
                    return LastResult = new NexusConfigLoadResult(false, false, false, null, Array.Empty<string>(),
                        $"Không đọc được {FileName}: {ex.Message}", now);
                }
            }
        }

        private NexusConfigLoadResult RestoreBrokenFile(string error, string now)
        {
            string backup = $"{ConfigPath}.invalid-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            string? backupError = null;
            try
            {
                File.Copy(ConfigPath, backup, overwrite: false);
            }
            catch (Exception ex)
            {
                backupError = ex.Message;
            }
            // TASK-17a ADR-69: a broken file keeps the last good settings, except networkTargets, which falls back to the defaults
            Current = Current with { NetworkTargets = null, Stt = null, Tts = null };
            string? writeError = AtomicFile.WriteAllText(ConfigPath, Serialize(Current));
            Apply(Current);
            var warnings = new List<string>();
            if (backupError != null) warnings.Add($"Không sao lưu được file hỏng: {backupError}");
            if (writeError != null) warnings.Add($"Không ghi lại được file chuẩn: {writeError}");
            return new NexusConfigLoadResult(false, false, writeError == null, backupError == null ? backup : null, warnings, error, now);
        }

        private static byte[] ReadShared(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(30);
                }
            }
        }

        private static void Apply(NexusConfig config) =>
            GameBoost.Configure(config.ProtectedProcessPrefixes, config.MemoryTrimFloorMb);

        // ───────────── field-by-field merge (invalid field -> keep last valid value) ─────────────

        private static NexusConfig Merge(JsonElement root, NexusConfig fallback, List<string> warnings)
        {
            string mode = ReadString(root, "defaultNexusMode", fallback.DefaultNexusMode, warnings,
                v => SquadDispatcher.TryParseMode(v, out NexusMode m) ? m.ToString() : null, "Micro | Sprint | Full");
            IReadOnlyList<string> monitors = ReadStrings(root, "preferredMonitorKeywords", fallback.PreferredMonitorKeywords, warnings);
            string passive = ReadString(root, "passiveHudHotkey", fallback.PassiveHudHotkey, warnings,
                v => HotkeyChord.TryParse(v, out _) ? v.Trim() : null, "ví dụ Alt+Space (cần Alt/Ctrl/Win)");
            string command = ReadString(root, "commandModeHotkey", fallback.CommandModeHotkey, warnings,
                v => HotkeyChord.TryParse(v, out _) ? v.Trim() : null, "ví dụ Ctrl+Shift+Space (cần Alt/Ctrl/Win)");
            if (string.Equals(passive, command, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"passiveHudHotkey và commandModeHotkey trùng nhau ({passive}): giữ cặp đang dùng.");
                passive = fallback.PassiveHudHotkey;
                command = fallback.CommandModeHotkey;
            }
            int trimFloor = ReadInt(root, "memoryTrimFloorMb", fallback.MemoryTrimFloorMb, 0, 4096, warnings);
            int debounce = ReadInt(root, "watcherDebounceMs", fallback.WatcherDebounceMs, 20, 2000, warnings);
            int slaDesktop = ReadInt(root, "dispatchSlaDesktopMs", fallback.DispatchSlaDesktopMs, 1, 1000, warnings);
            int slaCombat = ReadInt(root, "dispatchSlaCombatMs", fallback.DispatchSlaCombatMs, 1, 1000, warnings);
            IReadOnlyList<string> protectedPrefixes = ReadStrings(root, "protectedProcessPrefixes", fallback.ProtectedProcessPrefixes, warnings);
            IReadOnlyList<string> combatPrefixes = ReadStrings(root, "combatProcessPrefixes", fallback.CombatProcessPrefixes, warnings, optional: true);
            string geminiModel = ReadString(root, "geminiModel", fallback.GeminiModel, warnings,
                v => IsModelName(v) ? v.Trim() : null, "tên model, ví dụ gemini-3.5-flash-lite", optional: true);
            int geminiTimeout = ReadInt(root, "geminiTimeoutSeconds", fallback.GeminiTimeoutSeconds, 1, 120, warnings, optional: true);
            int geminiTokens = ReadInt(root, "geminiMaxOutputTokens", fallback.GeminiMaxOutputTokens, 1, 8192, warnings, optional: true);
            double geminiTemperature = ReadDouble(root, "geminiTemperature", fallback.GeminiTemperature, 0.0, 2.0, warnings, optional: true);
            bool autoQa = ReadBool(root, "autoQaAfterAsk", fallback.AutoQaAfterAsk, warnings);
            int maxTurns = ReadInt(root, "maxConversationTurns", fallback.MaxConversationTurns, 1, 20, warnings, optional: true);
            int maxRpm = ReadInt(root, "maxRequestsPerMinute", fallback.MaxRequestsPerMinute, 1, 10000, warnings, optional: true);
            int maxTpm = ReadInt(root, "maxTokensPerMinute", fallback.MaxTokensPerMinute, 1, 100000000, warnings, optional: true);
            int maxDaily = ReadInt(root, "maxDailyTokens", fallback.MaxDailyTokens, 1, 1000000000, warnings, optional: true);
            int httpRetries = ReadInt(root, "httpMaxRetries", fallback.HttpMaxRetries, 0, 10, warnings, optional: true);
            int httpRetryDelay = ReadInt(root, "httpRetryBaseDelayMs", fallback.HttpRetryBaseDelayMs, 10, 10000, warnings, optional: true);
            int maxPins = ReadInt(root, "maxPinnedCards", fallback.MaxPinnedCards, 1, 5, warnings, optional: true);
            // TASK-12 ADR-61 (optional, older files get the defaults)
            int visionBytes = ReadInt(root, "maxVisionImageBytes", fallback.MaxVisionImageBytes, 32000, 4000000, warnings, optional: true);
            int visionDimension = ReadInt(root, "maxVisionDimensionPx", fallback.MaxVisionDimensionPx, 256, 4096, warnings, optional: true);
            IReadOnlyList<string> privacyKeywords = ReadStrings(root, "privacyRedactedWindowKeywords",
                fallback.PrivacyRedactedWindowKeywords ?? NexusConfig.DefaultPrivacyRedactedWindowKeywords, warnings, optional: true);
            int sourceDimension = ReadInt(root, "maxSourceDimensionPx", fallback.MaxSourceDimensionPx, 1024, 8192, warnings, optional: true);
            string visionWorker = ReadString(root, "visionWorker", fallback.VisionWorker, warnings,
                v => v.Trim().ToLowerInvariant() is "process" or "inproc" ? v.Trim().ToLowerInvariant() : null, "process | inproc", optional: true);
            string eqDir = ReadString(root, "equalizerApoConfigDir", fallback.EqualizerApoConfigDir, warnings,
                v => v.Trim().Length > 0 ? v.Trim() : null, "a folder path", optional: true);
            bool youtubeEq = ReadBool(root, "youtubeEqAutoSwitch", fallback.YoutubeEqAutoSwitch, warnings);
            int youtubeDebounce = ReadInt(root, "youtubeCheckDebounceMs", fallback.YoutubeCheckDebounceMs, 100, 10000, warnings, optional: true);
            string watchUrl = ReadString(root, "watchWebhookUrl", fallback.WatchWebhookUrl, warnings,
                v => v.Trim().Length == 0 || WatchWebhook.IsValidUrl(v.Trim()) ? v.Trim() : null, "\"\" or an absolute http(s) URL", optional: true);

            bool shiftSound = ReadBool(root, "shiftCompleteSound", fallback.ShiftCompleteSound, warnings);
            IReadOnlyList<NetworkTargetConfig>? networkTargets = ReadNetworkTargets(root, warnings);
            SttConfig? stt = ReadStt(root, warnings);
            TtsConfig? tts = ReadTts(root, warnings);

            return new NexusConfig(mode, monitors, passive, command, trimFloor, debounce, slaDesktop, slaCombat,
                protectedPrefixes, combatPrefixes, geminiModel, geminiTimeout, geminiTokens, geminiTemperature, autoQa, maxTurns,
                maxRpm, maxTpm, maxDaily, httpRetries, httpRetryDelay, maxPins, visionBytes, visionDimension, privacyKeywords, sourceDimension, visionWorker,
                eqDir, youtubeEq, youtubeDebounce, watchUrl, shiftSound, networkTargets, stt, tts);
        }

        /// <summary>TASK-21 ADR-76: the provider names "tts.provider" accepts.</summary>
        public static readonly string[] TtsProviders = { "auto", "lan", "cli", "sapi" };

        /// <summary>
        /// TASK-21 ADR-76 "tts": { enabled, provider, lanEndpoint "host:port", cliPath, cliArgs, voice, maxWords }. Missing / null = null
        /// (enabled, auto → SAPI, maxWords 25). Not an object = warning + null. A bad field falls back to its default with a warning.
        /// </summary>
        private static TtsConfig? ReadTts(JsonElement root, List<string> warnings)
        {
            if (!TryGetField(root, "tts", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("'tts' phải là object {enabled, provider, lanEndpoint, cliPath, cliArgs, voice, maxWords}: dùng mặc định (auto → SAPI).");
                return null;
            }
            string Text(string name, string fallback)
            {
                if (!TryGetField(value, name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return fallback;
                if (v.ValueKind == JsonValueKind.String) return (v.GetString() ?? string.Empty).Trim();
                warnings.Add($"'tts.{name}' phải là chuỗi: dùng \"{fallback}\".");
                return fallback;
            }
            TtsConfig d = TtsConfig.Default;
            bool enabled = d.Enabled;
            if (TryGetField(value, "enabled", out JsonElement en) && en.ValueKind != JsonValueKind.Null)
            {
                if (en.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = en.GetBoolean();
                else warnings.Add($"'tts.enabled' phải là true hoặc false: dùng {d.Enabled.ToString().ToLowerInvariant()}.");
            }
            string provider = Text("provider", d.Provider).ToLowerInvariant();
            if (Array.IndexOf(TtsProviders, provider) < 0)
            {
                warnings.Add($"'tts.provider' = \"{provider}\" không hợp lệ (auto | lan | cli | sapi): dùng auto.");
                provider = d.Provider;
            }
            string lan = Text("lanEndpoint", d.LanEndpoint);
            if (lan.Length > 0 && !IsHostPort(lan))
            {
                warnings.Add($"'tts.lanEndpoint' = \"{lan}\" không phải host:port: bỏ qua LAN.");
                lan = string.Empty;
            }
            string voice = Text("voice", d.Voice);
            int maxWords = d.MaxWords;
            if (TryGetField(value, "maxWords", out JsonElement mw) && mw.ValueKind != JsonValueKind.Null)
            {
                if (mw.ValueKind == JsonValueKind.Number && mw.TryGetInt32(out int words))
                {
                    maxWords = Math.Clamp(words, TtsConfig.MinMaxWords, TtsConfig.MaxMaxWords);
                    if (maxWords != words) warnings.Add($"'tts.maxWords' = {words} ngoài {TtsConfig.MinMaxWords}–{TtsConfig.MaxMaxWords}: dùng {maxWords}.");
                }
                else warnings.Add($"'tts.maxWords' phải là số nguyên ({TtsConfig.MinMaxWords}–{TtsConfig.MaxMaxWords}): dùng {d.MaxWords}.");
            }
            return new TtsConfig(enabled, provider, lan, Text("cliPath", d.CliPath), Text("cliArgs", d.CliArgs),
                voice.Length > 0 ? voice : d.Voice, maxWords);
        }

        /// <summary>TASK-18a ADR-72: the provider names "stt.provider" accepts.</summary>
        public static readonly string[] SttProviders = { "auto", "lan", "cli", "sapi" };

        /// <summary>
        /// TASK-18a ADR-72 "stt": { provider, lanEndpoint "host:port", cliPath, cliArgs, language, lanTimeoutMs (TASK-20a) }. Missing / null = null (auto
        /// with nothing configured = SAPI). Not an object = warning + null. A bad field falls back to its default with a warning.
        /// </summary>
        private static SttConfig? ReadStt(JsonElement root, List<string> warnings)
        {
            if (!TryGetField(root, "stt", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("'stt' phải là object {provider, lanEndpoint, cliPath, cliArgs, language, lanTimeoutMs}: dùng mặc định (auto → SAPI).");
                return null;
            }
            string Text(string name, string fallback)
            {
                if (!TryGetField(value, name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return fallback;
                if (v.ValueKind == JsonValueKind.String) return (v.GetString() ?? string.Empty).Trim();
                warnings.Add($"'stt.{name}' phải là chuỗi: dùng \"{fallback}\".");
                return fallback;
            }
            SttConfig d = SttConfig.Default;
            string provider = Text("provider", d.Provider).ToLowerInvariant();
            if (Array.IndexOf(SttProviders, provider) < 0)
            {
                warnings.Add($"'stt.provider' = \"{provider}\" không hợp lệ (auto | lan | cli | sapi): dùng auto.");
                provider = d.Provider;
            }
            string lan = Text("lanEndpoint", d.LanEndpoint);
            if (lan.Length > 0 && !IsHostPort(lan))
            {
                warnings.Add($"'stt.lanEndpoint' = \"{lan}\" không phải host:port: bỏ qua LAN.");
                lan = string.Empty;
            }
            string language = Text("language", d.Language);
            int lanTimeout = d.LanTimeoutMs; // TASK-20a: clamped to 1000–30000
            if (TryGetField(value, "lanTimeoutMs", out JsonElement t) && t.ValueKind != JsonValueKind.Null)
            {
                if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int ms))
                {
                    lanTimeout = Math.Clamp(ms, SttConfig.MinLanTimeoutMs, SttConfig.MaxLanTimeoutMs);
                    if (lanTimeout != ms) warnings.Add($"'stt.lanTimeoutMs' = {ms} ngoài {SttConfig.MinLanTimeoutMs}–{SttConfig.MaxLanTimeoutMs}: dùng {lanTimeout}.");
                }
                else warnings.Add($"'stt.lanTimeoutMs' phải là số nguyên (ms): dùng {d.LanTimeoutMs}.");
            }
            return new SttConfig(provider, lan, Text("cliPath", d.CliPath), Text("cliArgs", d.CliArgs), language.Length > 0 ? language : d.Language, lanTimeout);
        }

        /// <summary>"host:port" with port 1–65535 and no whitespace (IPv4 or a name; TASK-18a).</summary>
        public static bool IsHostPort(string value)
        {
            int colon = value.LastIndexOf(':');
            return colon > 0 && colon < value.Length - 1 && !value.Any(char.IsWhiteSpace) && value.Length <= 260 &&
                   int.TryParse(value.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) &&
                   port is >= 1 and <= 65535;
        }

        /// <summary>TASK-17a ADR-69: at most this many /net targets are read (all pinged in parallel, each cut at 200 ms).</summary>
        public const int MaxNetworkTargets = 8;

        /// <summary>
        /// TASK-17a ADR-69 "networkTargets": [{ "label": "...", "host": "..." }]. Missing, not an array, empty, or no valid entry
        /// = null (NetworkRadar uses its 4 defaults). An entry without a non-blank label and host (or a host with spaces / longer
        /// than 253) is skipped with a warning; entries beyond <see cref="MaxNetworkTargets"/> are dropped with a warning.
        /// Unlike the other fields the previous value is NOT kept on error: a bad list always means the defaults.
        /// </summary>
        private static IReadOnlyList<NetworkTargetConfig>? ReadNetworkTargets(JsonElement root, List<string> warnings)
        {
            if (!TryGetField(root, "networkTargets", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("'networkTargets' phải là mảng [{\"label\", \"host\"}]: dùng 4 đích mặc định.");
                return null;
            }
            var list = new List<NetworkTargetConfig>();
            int index = 0, skipped = 0, total = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                index++;
                total++;
                string? label = null, host = null;
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetField(item, "label", out JsonElement l) && l.ValueKind == JsonValueKind.String) label = l.GetString()?.Trim();
                    if (TryGetField(item, "host", out JsonElement h) && h.ValueKind == JsonValueKind.String) host = h.GetString()?.Trim();
                }
                if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(host) || host.Length > 253 || host.Any(char.IsWhiteSpace) || label.Length > 40)
                {
                    skipped++;
                    warnings.Add($"'networkTargets' mục {index} thiếu label / host hợp lệ: bỏ qua.");
                    continue;
                }
                if (list.Count == MaxNetworkTargets) continue;
                list.Add(new NetworkTargetConfig(label, host));
            }
            if (total - skipped > MaxNetworkTargets) warnings.Add($"'networkTargets' có {total - skipped} đích hợp lệ: chỉ dùng {MaxNetworkTargets} đích đầu.");
            if (list.Count == 0)
            {
                if (total > 0) warnings.Add("'networkTargets' không còn đích hợp lệ nào: dùng 4 đích mặc định.");
                return null;
            }
            return list;
        }

        /// <summary>The model name goes into the request URL path: letters, digits, '.', '-', '_' only.</summary>
        private static bool IsModelName(string value)
        {
            string v = value.Trim();
            return v.Length is > 0 and <= 64 && v.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_');
        }

        private static bool TryGetField(JsonElement root, string name, out JsonElement value)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static string ReadString(JsonElement root, string name, string fallback, List<string> warnings,
            Func<string, string?> normalize, string expected, bool optional = false)
        {
            if (!TryGetField(root, name, out JsonElement value))
            {
                if (!optional) warnings.Add($"Thiếu '{name}': giữ '{fallback}'.");
                return fallback;
            }
            string? normalized = value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? normalize(value.GetString()!)
                : null;
            if (normalized == null)
            {
                warnings.Add($"'{name}' không hợp lệ ({expected}): giữ '{fallback}'.");
                return fallback;
            }
            return normalized;
        }

        private static int ReadInt(JsonElement root, string name, int fallback, int min, int max, List<string> warnings,
            bool optional = false)
        {
            if (!TryGetField(root, name, out JsonElement value))
            {
                if (!optional) warnings.Add($"Thiếu '{name}': giữ {fallback}.");
                return fallback;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= min && number <= max)
                return number;
            warnings.Add($"'{name}' phải là số nguyên {min}..{max}: giữ {fallback}.");
            return fallback;
        }

        /// <summary>Optional boolean (absent = keep the current value, no warning).</summary>
        private static bool ReadBool(JsonElement root, string name, bool fallback, List<string> warnings)
        {
            if (!TryGetField(root, name, out JsonElement value)) return fallback;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            warnings.Add($"'{name}' phải là true hoặc false: giữ {fallback.ToString().ToLowerInvariant()}.");
            return fallback;
        }

        private static double ReadDouble(JsonElement root, string name, double fallback, double min, double max,
            List<string> warnings, bool optional = false)
        {
            if (!TryGetField(root, name, out JsonElement value))
            {
                if (!optional) warnings.Add($"Thiếu '{name}': giữ {fallback}.");
                return fallback;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && number >= min && number <= max)
                return number;
            warnings.Add($"'{name}' phải là số {min}..{max}: giữ {fallback}.");
            return fallback;
        }

        private static IReadOnlyList<string> ReadStrings(JsonElement root, string name, IReadOnlyList<string> fallback,
            List<string> warnings, bool optional = false)
        {
            if (!TryGetField(root, name, out JsonElement value))
            {
                if (!optional) warnings.Add($"Thiếu '{name}': giữ danh sách đang dùng.");
                return fallback;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                bool allStrings = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        items.Add(item.GetString()!.Trim());
                    else
                        allStrings = false;
                }
                List<string> distinct = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (allStrings && distinct.Count > 0) return distinct;
            }
            warnings.Add($"'{name}' phải là mảng chuỗi không rỗng: giữ danh sách đang dùng.");
            return fallback;
        }
    }
}
