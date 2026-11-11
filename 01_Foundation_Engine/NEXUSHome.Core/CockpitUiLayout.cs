using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-16 U1 (ADR-65): what the Cockpit remembers between runs, in config/cockpit_ui.json (per machine, Git-ignored,
    /// UTF-8 without BOM). <see cref="HeightLevel"/> = "collapsed" | "half" | "full"; position in physical pixels of the
    /// virtual screen (null = default spot on the G272).
    /// </summary>
    public sealed record CockpitUiState(
        [property: JsonPropertyName("heightLevel")] string HeightLevel = CockpitLayout.Half,
        [property: JsonPropertyName("leftPx")] int? LeftPx = null,
        [property: JsonPropertyName("topPx")] int? TopPx = null,
        [property: JsonPropertyName("autoCollapse")] bool AutoCollapse = true,
        [property: JsonPropertyName("clickThrough")] bool ClickThrough = false,
        // TASK-17 V1: microphone choice (PreferredMicSelector): name substring preferred over the Windows default, and the
        // device forced with /mic select (null = automatic)
        [property: JsonPropertyName("preferredMicSubstring")] string PreferredMicSubstring = PreferredMicSelector.DefaultPreferredSubstring,
        [property: JsonPropertyName("micDevice")] string? MicDevice = null);

    /// <summary>TASK-16 U1: the three snap heights (WPF units: px at 100 % scaling) and the snap rules.</summary>
    public static class CockpitLayout
    {
        public const string Collapsed = "collapsed";
        public const string Half = "half";
        public const string Full = "full";

        public const double CollapsedHeight = 40;
        public const double HalfHeight = 330;
        public const double FullHeight = 640;

        /// <summary>Passive + visible + no activity for this long → collapse to 40 px (when AutoCollapse is on).</summary>
        public const int AutoCollapseDelayMs = 30000;

        /// <summary>Opacity while click-through is on (WS_EX_TRANSPARENT).</summary>
        public const double ClickThroughOpacity = 0.6;

        public static string Normalize(string? level) => (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Collapsed or "collapse" or "40" => Collapsed,
            Full or "640" => Full,
            _ => Half
        };

        public static double HeightOf(string? level) => Normalize(level) switch
        {
            Collapsed => CollapsedHeight,
            Full => FullHeight,
            _ => HalfHeight
        };

        /// <summary>The level whose height is nearest to <paramref name="height"/> (drag released → snap, no animation).</summary>
        public static string Snap(double height)
        {
            double toCollapsed = Math.Abs(height - CollapsedHeight);
            double toHalf = Math.Abs(height - HalfHeight);
            double toFull = Math.Abs(height - FullHeight);
            if (toCollapsed <= toHalf && toCollapsed <= toFull) return Collapsed;
            return toHalf <= toFull ? Half : Full;
        }

        /// <summary>Double-click: Collapsed → Half, anything else → Collapsed.</summary>
        public static string ToggleCollapsed(string? level) => Normalize(level) == Collapsed ? Half : Collapsed;

        /// <summary>Test hook: NEXUS_UI_AUTOCOLLAPSE_MS shortens the 30 s idle delay (Verify-Task16 E3), clamped 1 s..10 min.</summary>
        public const string AutoCollapseDelayVariable = "NEXUS_UI_AUTOCOLLAPSE_MS";

        public static int ResolveAutoCollapseDelayMs()
        {
            string? env = Environment.GetEnvironmentVariable(AutoCollapseDelayVariable);
            return int.TryParse(env, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ms)
                ? Math.Clamp(ms, 1000, 600000)
                : AutoCollapseDelayMs;
        }

        /// <summary>
        /// /ui and pipe "UI" argument → one normalised action, or null when unknown:
        /// level:collapsed | level:half | level:full | toggle | clickthrough:on|off | autocollapse:on|off | keys | status.
        /// Vietnamese aliases: thu / nua / day, xuyen, tuthu, phim.
        /// </summary>
        public static string? ParseUiAction(string? argument)
        {
            string[] words = (argument ?? string.Empty).Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) return "status";
            string verb = words[0];
            string? arg = words.Length > 1 ? words[1] : null;
            bool? onOff = arg switch { "on" or "bat" or "bật" or "1" or "true" => true, "off" or "tat" or "tắt" or "0" or "false" => false, _ => null };
            return verb switch
            {
                "collapsed" or "collapse" or "thu" or "40" => "level:" + Collapsed,
                "half" or "nua" or "nửa" or "330" => "level:" + Half,
                "full" or "day" or "đầy" or "640" => "level:" + Full,
                "toggle" => "toggle",
                "clickthrough" or "click-through" or "xuyen" or "xuyên" => onOff is bool c ? "clickthrough:" + (c ? "on" : "off") : null,
                "autocollapse" or "auto-collapse" or "tuthu" or "tựthu" => onOff is bool a ? "autocollapse:" + (a ? "on" : "off") : null,
                "keys" or "f1" or "phim" or "phím" or "help" => "keys",
                "status" or "state" => "status",
                _ => null
            };
        }
    }

    /// <summary>
    /// TASK-16 B1 (display layer, on top of the TASK-12c data layer): the user's real Cockpit never shows test records in
    /// ACTIVE SQUAD or RECENT DISPATCHES. A test run (selftest, Verify with mock Gemini) still sees everything.
    /// </summary>
    public static class CockpitDisplayFilter
    {
        private static readonly System.Text.RegularExpressions.Regex GateTag =
            new(@"\(Gate [^)]*E[^)]*\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>True in the user's real HostService (not a selftest, no Gemini mock).</summary>
        public static bool HideTestRecords => !TestRun.Active;

        public static bool IsTestSnapshot(LatestDispatchSnapshot s) =>
            s.IsTest == true || LooksLikeTest(s.AiModel, s.TaskSummary);

        public static bool IsTestDispatch(RecentDispatch r) => LooksLikeTest(r.AiModel, r.TaskSummary);

        /// <summary>Same rule as the TASK-12c is_test backfill: a mock model, or a "(Gate N … E…)" tag in the task.</summary>
        public static bool LooksLikeTest(string? aiModel, string? taskSummary) =>
            (aiModel ?? string.Empty).Contains("mock", StringComparison.OrdinalIgnoreCase) ||
            GateTag.IsMatch(taskSummary ?? string.Empty);
    }

    /// <summary>TASK-16 U1: load / save config/cockpit_ui.json. A missing or broken file gives the defaults.</summary>
    public sealed class CockpitUiStore
    {
        public const string FileName = "cockpit_ui.json";

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public CockpitUiStore(string configDirectory)
        {
            Path = System.IO.Path.Combine(configDirectory, FileName);
        }

        public string Path { get; }

        public string? LastError { get; private set; }

        public CockpitUiState Load()
        {
            try
            {
                if (!File.Exists(Path)) return new CockpitUiState();
                CockpitUiState? state = JsonSerializer.Deserialize<CockpitUiState>(File.ReadAllText(Path, Encoding.UTF8), Json);
                if (state == null) return new CockpitUiState();
                LastError = null;
                return state with { HeightLevel = CockpitLayout.Normalize(state.HeightLevel) };
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return new CockpitUiState();
            }
        }

        /// <returns>null on success, else the error (the Cockpit keeps working with what it has in memory).</returns>
        public string? Save(CockpitUiState state)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (dir != null) Directory.CreateDirectory(dir);
                byte[] bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(
                    state with { HeightLevel = CockpitLayout.Normalize(state.HeightLevel) }, Json));
                LastError = AtomicFile.WriteAllBytes(Path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
            return LastError;
        }
    }
}
