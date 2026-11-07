using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        int HttpRetryBaseDelayMs = 300)
    {
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
            300);
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
        public static string? WriteAllBytes(string path, byte[] bytes)
        {
            string temp = $"{path}.{Environment.ProcessId}.tmp";
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
                $"maxDailyTokens={c.MaxDailyTokens} | backoff: httpMaxRetries={c.HttpMaxRetries}, httpRetryBaseDelayMs={c.HttpRetryBaseDelayMs}";
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

            return new NexusConfig(mode, monitors, passive, command, trimFloor, debounce, slaDesktop, slaCombat,
                protectedPrefixes, combatPrefixes, geminiModel, geminiTimeout, geminiTokens, geminiTemperature, autoQa, maxTurns,
                maxRpm, maxTpm, maxDaily, httpRetries, httpRetryDelay);
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
