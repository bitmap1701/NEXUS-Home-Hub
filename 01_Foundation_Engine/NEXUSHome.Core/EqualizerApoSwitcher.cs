using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-16 U2: one config.txt switch. <see cref="Changed"/> = false when the line was already right (no write).</summary>
    public sealed record EqSwitchResult(
        string Profile,
        bool Ok,
        bool Changed,
        string ConfigPath,
        string? IncludeLine,
        string? Error,
        bool BackupWritten,
        string AtUtc,
        double ElapsedMs);

    /// <summary>
    /// TASK-16 U2 (ADR-62): points Equalizer APO's config.txt at one of the two Samsung K450 profiles by rewriting only its
    /// <c>Include: nexus_k450_*.txt</c> line (added at the end when there is none). Every other line, the line endings and a
    /// UTF-8 BOM if the file has one are kept. The first change leaves <c>config.txt.nexus-backup</c> next to it. Nothing is
    /// written when the target profile file is missing, when the line is already right, or when the file is not valid UTF-8.
    /// Equalizer APO reloads config.txt by itself. By default the folder is under Program Files, which a normal user cannot
    /// write: the result then says so (UnauthorizedAccess) and nothing else happens; NEXUS never asks for admin rights.
    /// </summary>
    public static class EqualizerApoSwitcher
    {
        public const string DirVariable = "NEXUS_EQ_CONFIG_DIR";
        public const string YoutubeProfile = "youtube";
        public const string MusicProfile = "music";
        public const string ConfigFileName = "config.txt";
        public const string BackupSuffix = ".nexus-backup";

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        public static string ProfileFile(string profile) => $"nexus_k450_{profile}.txt";

        /// <summary>env NEXUS_EQ_CONFIG_DIR (tests, other installs) → nexus_config.json equalizerApoConfigDir.</summary>
        public static string ResolveDirectory(NexusConfig config)
        {
            string? env = Environment.GetEnvironmentVariable(DirVariable);
            return string.IsNullOrWhiteSpace(env) ? config.EqualizerApoConfigDir : env.Trim();
        }

        /// <summary>The auto-switch has something to do: config.txt and the YouTube profile both exist.</summary>
        public static bool IsAvailable(string directory) =>
            File.Exists(Path.Combine(directory, ConfigFileName)) && File.Exists(Path.Combine(directory, ProfileFile(YoutubeProfile)));

        /// <summary>The nexus_k450 profile config.txt includes now (null = none).</summary>
        public static string? CurrentProfile(string directory)
        {
            try
            {
                string path = Path.Combine(directory, ConfigFileName);
                if (!File.Exists(path)) return null;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string? file = NexusIncludeFile(line);
                    if (file != null) return file.Substring("nexus_k450_".Length, file.Length - "nexus_k450_".Length - ".txt".Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }

        public static EqSwitchResult Switch(string directory, string profile)
        {
            long t0 = Stopwatch.GetTimestamp();
            string configPath = Path.Combine(directory, ConfigFileName);
            if (profile != YoutubeProfile && profile != MusicProfile)
                return Fail(profile, configPath, $"unknown profile '{profile}'", t0);
            string includeFile = ProfileFile(profile);
            string desired = $"Include: {includeFile}";
            try
            {
                if (!File.Exists(configPath)) return Fail(profile, configPath, "config.txt not found (Equalizer APO not installed here?)", t0);
                if (!File.Exists(Path.Combine(directory, includeFile))) return Fail(profile, configPath, $"{includeFile} not found", t0);

                byte[] original = File.ReadAllBytes(configPath);
                bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
                string text;
                try
                {
                    text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
                }
                catch (DecoderFallbackException)
                {
                    return Fail(profile, configPath, "config.txt is not UTF-8: left unchanged", t0);
                }

                string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                bool endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
                var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
                if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (NexusIncludeFile(lines[i]) == null) continue;
                    if (!found)
                    {
                        lines[i] = desired;
                        found = true;
                    }
                    else
                    {
                        lines.RemoveAt(i); // a second nexus_k450 include would apply two profiles at once
                        i--;
                    }
                }
                if (!found) lines.Add(desired);

                string updated = string.Join(newline, lines) + (endsWithNewline || !found ? newline : string.Empty);
                if (updated == text) return new EqSwitchResult(profile, true, false, configPath, desired, null, false, Now(), Ms(t0));

                bool backup = false;
                string backupPath = configPath + BackupSuffix;
                if (!File.Exists(backupPath))
                {
                    File.WriteAllBytes(backupPath, original);
                    backup = true;
                }
                byte[] body = new UTF8Encoding(false).GetBytes(updated);
                byte[] bytes = bom ? Concat(Utf8Bom, body) : body;
                string? error = AtomicFile.WriteAllBytes(configPath, bytes);
                return error == null
                    ? new EqSwitchResult(profile, true, true, configPath, desired, null, backup, Now(), Ms(t0))
                    : Fail(profile, configPath, error, t0);
            }
            catch (UnauthorizedAccessException)
            {
                return Fail(profile, configPath,
                    "no write permission on the Equalizer APO config folder (Program Files): grant Modify once (manual, Red tier), NEXUS never elevates", t0);
            }
            catch (IOException ex)
            {
                return Fail(profile, configPath, $"{ex.GetType().Name}: {ex.Message}", t0);
            }
        }

        /// <summary>"Include: nexus_k450_music.txt" → "nexus_k450_music.txt"; any other line (comments too) → null.</summary>
        public static string? NexusIncludeFile(string line)
        {
            string t = line.Trim();
            if (!t.StartsWith("Include:", StringComparison.OrdinalIgnoreCase)) return null;
            string file = t.Substring("Include:".Length).Trim();
            return file.StartsWith("nexus_k450_", StringComparison.OrdinalIgnoreCase) &&
                   file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                   file.IndexOfAny(new[] { '\\', '/' }) < 0
                ? file.ToLowerInvariant()
                : null;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static EqSwitchResult Fail(string profile, string configPath, string error, long t0) =>
            new(profile, false, false, configPath, null, error, false, Now(), Ms(t0));

        private static string Now() => DateTime.UtcNow.ToString("o");

        private static double Ms(long t0) => Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
