using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-16 U1 (ADR-62): what the Cockpit remembers between runs, in config/cockpit_ui.json (per machine, Git-ignored,
    /// UTF-8 without BOM). <see cref="HeightLevel"/> = "collapsed" | "half" | "full"; position in physical pixels of the
    /// virtual screen (null = default spot on the G272).
    /// </summary>
    public sealed record CockpitUiState(
        [property: JsonPropertyName("heightLevel")] string HeightLevel = CockpitLayout.Half,
        [property: JsonPropertyName("leftPx")] int? LeftPx = null,
        [property: JsonPropertyName("topPx")] int? TopPx = null,
        [property: JsonPropertyName("autoCollapse")] bool AutoCollapse = true,
        [property: JsonPropertyName("clickThrough")] bool ClickThrough = false);

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
