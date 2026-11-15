using System;
using System.Globalization;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-22 (J2 / ADR-77): Safety tier for hands-free autonomous execution.</summary>
    public enum CommandSafetyTier
    {
        /// <summary>Safe read-only commands that can be executed immediately without manual Enter.</summary>
        SafeReadOnly = 0,

        /// <summary>Commands requiring explicit user review or manual Enter.</summary>
        ConfirmRequired = 1
    }

    /// <summary>TASK-20 / TASK-22: a spoken phrase recognized as a local "/" command.</summary>
    public sealed record VoiceCommandMatch(string Command, string Rule, string Normalized, CommandSafetyTier SafetyTier = CommandSafetyTier.SafeReadOnly);

    /// <summary>
    /// TASK-20 (ADR-75) & TASK-22 (ADR-77): maps a short spoken Vietnamese / English phrase (STT output of Alt+V or /ptt) to a local "/" command,
    /// 0 token. Pure function: lower case, Vietnamese diacritics removed (đ → d), punctuation → space, optional politeness words
    /// ("nexus", "hãy", "làm ơn", "đi", "nhé", "giúp tôi"…) dropped at both ends, then an exact phrase table plus three patterns
    /// with a number (port, timer) and safe bounded stackalloc Levenshtein fuzzy matching (<= 2 edits). No regex. 0 heap alloc for fuzzy match.
    /// </summary>
    public static class VoiceCommandMapper
    {
        private static readonly (string Phrase, string Command, CommandSafetyTier Tier)[] Exact =
        {
            ("kiem tra mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("radar mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("quet mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("trang thai mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("check network", "/net", CommandSafetyTier.SafeReadOnly),
            ("network radar", "/net", CommandSafetyTier.SafeReadOnly),
            ("network status", "/net", CommandSafetyTier.SafeReadOnly),
            // TASK-21 (20a.7) & TASK-22 (J2): phonetic drift (trại âm) of whisper small-q5_1 on "kiểm tra mạng"
            ("kiem tra ban", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra me", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra man", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra may", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra ma", "/net", CommandSafetyTier.SafeReadOnly),
            ("kim chia mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("kim tra mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem chia mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("kim chia man", "/net", CommandSafetyTier.SafeReadOnly),
            ("trang thai man", "/net", CommandSafetyTier.SafeReadOnly),
            // TASK-22: new variants observed in real whisper small-q5_1 runs
            ("kiem cho me", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho may", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho man", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho ban", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra may tinh", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra man hinh", "/net", CommandSafetyTier.SafeReadOnly),

            ("trang thai mic", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("trang thai micro", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("kiem tra mic", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("kiem tra micro", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("mic status", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("danh sach mic", "/mic list", CommandSafetyTier.SafeReadOnly),
            ("danh sach micro", "/mic list", CommandSafetyTier.SafeReadOnly),
            // TASK-21 (20a.7): phonetic drift on "trạng thái mic" / "kiểm tra mic" ("mít", "míc" -> "mit", "mic", "mich")
            ("trang thai mit", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("trang thai mich", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("kiem tra mit", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("kiem tra mich", "/mic status", CommandSafetyTier.SafeReadOnly),

            ("ngu canh", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("doc ngu canh", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("lay ngu canh", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("context", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("ngu can", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("ngu cang", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("doc ngu can", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("lay ngu can", "/ctx", CommandSafetyTier.SafeReadOnly),

            ("bao cao qa", "/qa", CommandSafetyTier.SafeReadOnly),
            ("kiem tra qa", "/qa", CommandSafetyTier.SafeReadOnly),
            ("chay qa", "/qa", CommandSafetyTier.SafeReadOnly),
            ("qa report", "/qa", CommandSafetyTier.SafeReadOnly),
            // TASK-21 (20a.7): phonetic drift on "báo cáo qa" ("quy a", "kêu a", "q a", "cu a")
            ("bao cao quy a", "/qa", CommandSafetyTier.SafeReadOnly),
            ("bao cao keu a", "/qa", CommandSafetyTier.SafeReadOnly),
            ("bao cao q a", "/qa", CommandSafetyTier.SafeReadOnly),
            ("bao cao cu a", "/qa", CommandSafetyTier.SafeReadOnly),
            ("kiem tra quy a", "/qa", CommandSafetyTier.SafeReadOnly),
            ("kiem tra keu a", "/qa", CommandSafetyTier.SafeReadOnly),

            ("danh sach hen gio", "/timer list", CommandSafetyTier.SafeReadOnly),
            ("xem hen gio", "/timer list", CommandSafetyTier.SafeReadOnly),
            ("huy hen gio", "/timer cancel all", CommandSafetyTier.SafeReadOnly),
            ("huy tat ca hen gio", "/timer cancel all", CommandSafetyTier.SafeReadOnly),

            // TASK-21 (J1 + 20a.7): /tts voice commands + phonetic drift
            ("bat giong noi", "/tts on", CommandSafetyTier.SafeReadOnly),
            ("mo giong noi", "/tts on", CommandSafetyTier.SafeReadOnly),
            ("tts on", "/tts on", CommandSafetyTier.SafeReadOnly),
            ("bac giong noi", "/tts on", CommandSafetyTier.SafeReadOnly),
            ("bat dong noi", "/tts on", CommandSafetyTier.SafeReadOnly),
            ("tat giong noi", "/tts off", CommandSafetyTier.SafeReadOnly),
            ("ngung giong noi", "/tts off", CommandSafetyTier.SafeReadOnly),
            ("tts off", "/tts off", CommandSafetyTier.SafeReadOnly),
            ("tat dong noi", "/tts off", CommandSafetyTier.SafeReadOnly),
            ("trang thai giong noi", "/tts status", CommandSafetyTier.SafeReadOnly),
            ("kiem tra giong noi", "/tts status", CommandSafetyTier.SafeReadOnly),
            ("trang thai tts", "/tts status", CommandSafetyTier.SafeReadOnly),
            ("tts status", "/tts status", CommandSafetyTier.SafeReadOnly),
            ("trang thai stt", "/ptt status", CommandSafetyTier.SafeReadOnly),
            ("kiem tra stt", "/ptt status", CommandSafetyTier.SafeReadOnly)
        };

        // Standard canonical targets and key drift aliases for bounded fuzzy matching (TASK-22)
        private static readonly (string Canonical, string Command, CommandSafetyTier Tier)[] FuzzyTargets =
        {
            ("kiem tra mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra may", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem tra me", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho me", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho may", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho mang", "/net", CommandSafetyTier.SafeReadOnly),
            ("kiem cho man", "/net", CommandSafetyTier.SafeReadOnly),
            ("trang thai mic", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("trang thai micro", "/mic status", CommandSafetyTier.SafeReadOnly),
            ("ngu canh", "/ctx", CommandSafetyTier.SafeReadOnly),
            ("bao cao qa", "/qa", CommandSafetyTier.SafeReadOnly),
            ("bat giong noi", "/tts on", CommandSafetyTier.SafeReadOnly),
            ("tat giong noi", "/tts off", CommandSafetyTier.SafeReadOnly),
            ("trang thai giong noi", "/tts status", CommandSafetyTier.SafeReadOnly),
            ("trang thai stt", "/ptt status", CommandSafetyTier.SafeReadOnly),
            ("huy hen gio", "/timer cancel all", CommandSafetyTier.SafeReadOnly)
        };

        private static readonly string[] Lead = { "nexus ", "hay ", "lam on ", "vui long ", "please ", "cho toi ", "giup toi " };
        private static readonly string[] Tail = { " di", " nhe", " giup toi", " giup minh", " please", " nha", " voi" };

        private static readonly (string Word, int Value)[] Numbers =
        {
            ("muoi lam", 15), ("hai muoi", 20), ("ba muoi", 30), ("bon muoi lam", 45), ("sau muoi", 60),
            ("mot", 1), ("hai", 2), ("ba", 3), ("bon", 4), ("tu", 4), ("nam", 5), ("sau", 6), ("bay", 7), ("tam", 8), ("chin", 9), ("muoi", 10)
        };

        // Prefixes allowed for fuzzy match evaluation (must start with one of these to prevent false positives)
        private static readonly string[] FuzzyPrefixes = { "kiem tra ", "kiem cho ", "kiem ", "kim ", "trang thai ", "bao cao ", "hen gio ", "bat giong ", "tat giong ", "ngu canh" };

        public static VoiceCommandMatch? TryMap(string? transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 80) return null; // a long sentence is a question
            string s;
            try { s = Normalize(transcript); }
            catch (ArgumentException) { return null; } // broken UTF-16: never let it block the Alt+V hook (review 2026-11-12)
            if (s.Length == 0) return null;
            s = Strip(s);

            // 1. Exact phrase table matching
            foreach (var (phrase, command, tier) in Exact)
            {
                if (s == phrase) return new VoiceCommandMatch(command, phrase, s, tier);
            }

            // 2. Numeric commands: Port
            // "kiem tra cong 8080" / "cong 5055" / "check port 443" → /net port N
            foreach (string head in new[] { "kiem tra cong ", "check port ", "port " })
            {
                if (s.StartsWith(head, StringComparison.Ordinal) && TryNumber(s.Substring(head.Length), out int port) && port is >= 1 and <= 65535)
                    return new VoiceCommandMatch($"/net port {port.ToString(CultureInfo.InvariantCulture)}", head.Trim() + " <N>", s, CommandSafetyTier.SafeReadOnly);
            }

            // 3. Numeric commands: Timer
            // "hen gio 5 phut" / "dat hen gio muoi phut" / "timer 90 seconds" → /timer 5m | 10m | 90s
            foreach (string head in new[] { "dat hen gio ", "hen gio ", "timer " })
            {
                if (!s.StartsWith(head, StringComparison.Ordinal)) continue;
                string rest = s.Substring(head.Length);
                foreach (var (unitWord, unit) in new[] { (" phut", "m"), (" giay", "s"), (" tieng", "h"), (" gio", "h"), (" minutes", "m"), (" minute", "m"), (" seconds", "s"), (" second", "s"), (" hours", "h"), (" hour", "h") })
                {
                    if (rest.EndsWith(unitWord, StringComparison.Ordinal) && TryNumber(rest.Substring(0, rest.Length - unitWord.Length), out int n) &&
                        n >= 1 && n <= (unit == "h" ? 24 : 600)) // LocalTimerManager caps a timer at 24 h
                    {
                        return new VoiceCommandMatch($"/timer {n.ToString(CultureInfo.InvariantCulture)}{unit}", head.Trim() + " <N> " + unitWord.Trim(), s, CommandSafetyTier.SafeReadOnly);
                    }
                }
            }

            // 4. TASK-22 (J2 / ADR-77): Guarded Phonetic Bounded Fuzzy Match (stackalloc, <= 2 edits, <= 28 chars, allowed prefixes)
            if (s.Length <= 28 && HasFuzzyPrefix(s))
            {
                int bestDist = 3; // strictly <= 2
                (string Canonical, string Command, CommandSafetyTier Tier) bestMatch = default;

                foreach (var target in FuzzyTargets)
                {
                    // Fast length difference pre-filter: if lengths differ by more than 2, distance cannot be <= 2
                    if (Math.Abs(s.Length - target.Canonical.Length) > 2) continue;

                    int dist = LevenshteinDistanceBounded(s.AsSpan(), target.Canonical.AsSpan(), maxDistance: 2);
                    if (dist >= 0 && dist < bestDist)
                    {
                        bestDist = dist;
                        bestMatch = target;
                        if (dist <= 1) break; // early exit on high confidence
                    }
                }

                if (bestDist <= 2 && bestMatch.Command != null)
                {
                    return new VoiceCommandMatch(bestMatch.Command, $"fuzzy({bestMatch.Canonical},dist={bestDist})", s, bestMatch.Tier);
                }
            }

            return null;
        }

        private static bool HasFuzzyPrefix(string s)
        {
            foreach (string p in FuzzyPrefixes)
            {
                if (s.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Bounded Levenshtein distance using stackalloc (0 heap allocation).
        /// Returns the edit distance if <= maxDistance, or -1 if distance exceeds maxDistance.
        /// </summary>
        public static int LevenshteinDistanceBounded(ReadOnlySpan<char> s, ReadOnlySpan<char> t, int maxDistance = 2)
        {
            int n = s.Length;
            int m = t.Length;
            if (Math.Abs(n - m) > maxDistance) return -1;
            if (n == 0) return m <= maxDistance ? m : -1;
            if (m == 0) return n <= maxDistance ? n : -1;

            // Row buffers allocated on the stack (max length <= 32)
            Span<int> v0 = stackalloc int[m + 1];
            Span<int> v1 = stackalloc int[m + 1];

            for (int j = 0; j <= m; j++) v0[j] = j;

            for (int i = 0; i < n; i++)
            {
                v1[0] = i + 1;
                int minInRow = v1[0];

                for (int j = 0; j < m; j++)
                {
                    int cost = (s[i] == t[j]) ? 0 : 1;
                    int dist = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
                    v1[j + 1] = dist;
                    if (dist < minInRow) minInRow = dist;
                }

                if (minInRow > maxDistance) return -1; // early prune

                v1.CopyTo(v0);
            }

            int finalDist = v0[m];
            return finalDist <= maxDistance ? finalDist : -1;
        }

        /// <summary>Lower case, no Vietnamese diacritics (đ → d), letters / digits / single spaces only.</summary>
        public static string Normalize(string text)
        {
            string d = text.Normalize(NormalizationForm.FormD);
            var b = new StringBuilder(d.Length);
            bool space = true;
            foreach (char c0 in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c0) == UnicodeCategory.NonSpacingMark) continue;
                char c = c0 is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c0);
                if (char.IsLetterOrDigit(c))
                {
                    b.Append(c);
                    space = false;
                }
                else if (!space)
                {
                    b.Append(' ');
                    space = true;
                }
            }
            if (b.Length > 0 && b[^1] == ' ') b.Length--;
            return b.ToString();
        }

        private static string Strip(string s)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (string lead in Lead)
                    if (s.StartsWith(lead, StringComparison.Ordinal) && s.Length > lead.Length) { s = s.Substring(lead.Length); changed = true; }
                foreach (string tail in Tail)
                    if (s.EndsWith(tail, StringComparison.Ordinal) && s.Length > tail.Length) { s = s.Substring(0, s.Length - tail.Length); changed = true; }
            }
            return s;
        }

        private static bool TryNumber(string text, out int value)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
            foreach (var (word, n) in Numbers)
                if (text == word) { value = n; return true; }
            value = 0;
            return false;
        }
    }
}
