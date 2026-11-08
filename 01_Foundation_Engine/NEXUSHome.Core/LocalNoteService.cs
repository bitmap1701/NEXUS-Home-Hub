using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>One line of quick_notes.md. <see cref="Id"/> = 0 for a bullet written by hand outside a "### Note" heading.</summary>
    public sealed record LocalNoteEntry(int Id, string TimestampUtc, string Text);

    /// <summary>Result of <see cref="LocalNoteService.Append"/>.</summary>
    public sealed record LocalNoteWrite(bool Ok, bool Blocked, LocalNoteEntry? Entry, string? Error, double WriteMs);

    /// <summary>
    /// TASK-14 ADR-59 <c>/note</c>: quick notes in <c>03_Local_Data_Vault/zettelkasten/quick_notes.md</c> (UTF-8 without BOM,
    /// git-ignored). Each note is its own <c>### Note &lt;id&gt; · &lt;UTC&gt;</c> section followed by the spec line
    /// <c>- [YYYY-MM-DD HH:mm:ss UTC] &lt;text&gt;</c>, so <see cref="VaultGroundingEngine"/> (which cuts a section body at
    /// 1 200 characters) can always reach the newest note.
    /// Writes are atomic (ADR-14: temp file + File.Move) under a machine-wide named mutex, so HostService and McpTools never
    /// lose each other's notes and a reader never sees half a file. Secret Guard: text that looks like an API key or a
    /// private key is refused before anything touches the disk.
    /// </summary>
    public sealed class LocalNoteService
    {
        public const string FolderName = "zettelkasten";
        public const string FileName = "quick_notes.md";
        public const string RelativePath = FolderName + "/" + FileName;
        public const int MaxNoteChars = 2000;
        public const int DefaultListCount = 5;
        public const int MaxListCount = 50;
        public const int MaxFindResults = 20;
        private const string HeadingPrefix = "### Note ";
        private const string MutexName = @"Local\NEXUS_Home_Hub_QuickNotes";
        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Secret Guard (ADR-24): Google API keys (same rule as <see cref="DpapiSecretVault.GoogleApiKeyPattern"/>) plus common
        /// secret shapes (OpenAI sk-, GitHub gh*_, AWS AKIA, Slack xox*-, PEM private keys). Interpreted Regex (ADR-29).
        /// </summary>
        public static readonly Regex SecretPattern = new(
            @"AIza[0-9A-Za-z_\-]{20,}|\bsk-(?:proj-)?[A-Za-z0-9]{20,}|\bgh[pousr]_[A-Za-z0-9]{30,}|\bAKIA[0-9A-Z]{16}\b|\bxox[abprs]-[A-Za-z0-9\-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----",
            RegexOptions.CultureInvariant);

        public const string Header =
            "# Quick Notes (NEXUS /note)\n\n" +
            "> Ghi chú nhanh từ Cockpit (/note) và MCP run_local_utility. UTF-8 không BOM, nằm trong .gitignore, không đẩy lên Git.\n";

        private readonly object _gate = new();

        public LocalNoteService(string vaultPath)
        {
            NotePath = Path.Combine(vaultPath, FolderName, FileName);
        }

        public string NotePath { get; }

        /// <summary>Raised after every successful write (grounding drops its memo so /ground sees the note at once).</summary>
        public event Action? Changed;

        public static bool ContainsSecret(string? text) => !string.IsNullOrEmpty(text) && SecretPattern.IsMatch(text);

        public static string CleanText(string? text)
        {
            var sb = new StringBuilder((text ?? string.Empty).Length);
            bool space = false;
            foreach (char c in text ?? string.Empty)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public LocalNoteWrite Append(string? text)
        {
            long ts0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (ContainsSecret(text))
                return new LocalNoteWrite(false, true, null, "Nội dung có chuỗi giống khóa bí mật (API key / private key): không ghi xuống đĩa.", 0);

            string clean = CleanText(text);
            if (clean.Length == 0) return new LocalNoteWrite(false, false, null, "Thiếu nội dung. Dùng: /note <nội dung>", 0);
            if (clean.Length > MaxNoteChars) return new LocalNoteWrite(false, false, null, $"Ghi chú dài quá {MaxNoteChars} ký tự.", 0);

            DateTime now = DateTime.UtcNow;
            string stamp = now.ToString(TimestampFormat, CultureInfo.InvariantCulture) + " UTC";
            LocalNoteEntry? entry = null;
            string? error = WithFileLock(() =>
            {
                string existing = ReadText();
                int nextId = ParseEntries(existing).Select(e => e.Id).DefaultIfEmpty(0).Max() + 1;
                var sb = new StringBuilder(existing.Length + clean.Length + 96);
                sb.Append(existing.Length == 0 ? Header : existing);
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                sb.Append('\n').Append(HeadingPrefix).Append(nextId.ToString(CultureInfo.InvariantCulture)).Append(" · ").Append(stamp).Append('\n');
                sb.Append("- [").Append(stamp).Append("] ").Append(clean).Append('\n');
                string? writeError = AtomicFile.WriteAllBytes(NotePath, Utf8NoBom.GetBytes(sb.ToString()));
                if (writeError == null) entry = new LocalNoteEntry(nextId, stamp, clean);
                return writeError;
            });

            double ms = Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - ts0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3);
            if (error != null || entry == null) return new LocalNoteWrite(false, false, null, error ?? "Không ghi được quick_notes.md.", ms);
            RaiseChanged();
            return new LocalNoteWrite(true, false, entry, null, ms);
        }

        /// <summary>Newest <paramref name="count"/> notes, newest first.</summary>
        public IReadOnlyList<LocalNoteEntry> List(int count = DefaultListCount)
        {
            int n = Math.Clamp(count, 1, MaxListCount);
            List<LocalNoteEntry> all = ParseEntries(ReadText());
            return Enumerable.Range(0, Math.Min(n, all.Count)).Select(i => all[all.Count - 1 - i]).ToList();
        }

        /// <summary>Notes containing <paramref name="keyword"/> (case-insensitive), newest first, at most <see cref="MaxFindResults"/>.</summary>
        public IReadOnlyList<LocalNoteEntry> Find(string keyword, out int totalMatches)
        {
            string k = CleanText(keyword);
            List<LocalNoteEntry> matches = k.Length == 0
                ? new List<LocalNoteEntry>()
                : ParseEntries(ReadText()).Where(e => e.Text.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
            totalMatches = matches.Count;
            matches.Reverse();
            return matches.Take(MaxFindResults).ToList();
        }

        public int Count() => ParseEntries(ReadText()).Count;

        /// <summary>
        /// Removes the "### Note &lt;id&gt;" sections with these ids (atomic rewrite, same lock). Used by the Gate 14 selftest to
        /// take its own test notes out again; not exposed as a Cockpit command.
        /// </summary>
        public int RemoveNotes(IReadOnlyCollection<int> ids)
        {
            if (ids.Count == 0) return 0;
            int removed = 0;
            string? error = WithFileLock(() =>
            {
                string existing = ReadText();
                if (existing.Length == 0) return null;
                var kept = new StringBuilder(existing.Length);
                bool skipping = false;
                string[] lines = existing.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
                    {
                        bool wasSkipping = skipping;
                        skipping = TryParseHeadingId(line, out int id) && ids.Contains(id);
                        // a kept note right after a removed one: put back the blank line that separates sections
                        if (!skipping && wasSkipping && kept.Length > 0 && !(kept.Length >= 2 && kept[kept.Length - 2] == '\n')) kept.Append('\n');
                        if (skipping)
                        {
                            removed++;
                            // drop the blank line written before this heading
                            if (kept.Length >= 2 && kept[kept.Length - 1] == '\n' && kept[kept.Length - 2] == '\n') kept.Length--;
                            continue;
                        }
                    }
                    if (skipping) continue;
                    kept.Append(line);
                    if (i < lines.Length - 1) kept.Append('\n');
                }
                return removed == 0 ? null : AtomicFile.WriteAllBytes(NotePath, Utf8NoBom.GetBytes(kept.ToString()));
            });
            if (error != null) return 0;
            if (removed > 0) RaiseChanged();
            return removed;
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // grounding invalidation is best effort
            }
        }

        /// <summary>Process lock + machine-wide named mutex (HostService and McpTools write the same file).</summary>
        private string? WithFileLock(Func<string?> action)
        {
            lock (_gate)
            {
                Mutex? mutex = null;
                bool owned = false;
                try
                {
                    mutex = new Mutex(false, MutexName);
                    try
                    {
                        owned = mutex.WaitOne(TimeSpan.FromSeconds(3));
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true; // previous owner died; the file itself is always whole (atomic replace)
                    }
                    if (!owned) return "quick_notes.md đang bận (tiến trình khác giữ khóa quá 3 s).";
                    return action();
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
                finally
                {
                    if (owned) mutex?.ReleaseMutex();
                    mutex?.Dispose();
                }
            }
        }

        /// <summary>Whole file as text (BOM stripped if someone saved one by hand; the next write removes it).</summary>
        private string ReadText()
        {
            try
            {
                using var stream = new FileStream(NotePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd().Replace("\r\n", "\n");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return string.Empty;
            }
        }

        private static bool TryParseHeadingId(string line, out int id)
        {
            id = 0;
            int start = HeadingPrefix.Length;
            int end = start;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            return end > start && end - start <= 9 &&
                   int.TryParse(line.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        private static List<LocalNoteEntry> ParseEntries(string text)
        {
            var entries = new List<LocalNoteEntry>();
            if (text.Length == 0) return entries;
            int currentId = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
                {
                    currentId = TryParseHeadingId(line, out int id) ? id : 0;
                    continue;
                }
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    currentId = 0;
                    continue;
                }
                if (!line.StartsWith("- [", StringComparison.Ordinal)) continue;
                int close = line.IndexOf("] ", 3, StringComparison.Ordinal);
                if (close < 0) continue;
                entries.Add(new LocalNoteEntry(currentId, line.Substring(3, close - 3), line.Substring(close + 2).Trim()));
                currentId = 0; // one bullet per heading; later hand-written bullets count as id 0
            }
            return entries;
        }
    }
}
