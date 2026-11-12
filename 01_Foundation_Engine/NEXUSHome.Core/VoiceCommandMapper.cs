using System;
using System.Globalization;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-20: a spoken phrase recognized as a local "/" command.</summary>
    public sealed record VoiceCommandMatch(string Command, string Rule, string Normalized);

    /// <summary>
    /// TASK-20 (ADR-75): maps a short spoken Vietnamese / English phrase (STT output of Alt+V or /ptt) to a local "/" command,
    /// 0 token. Pure function: lower case, Vietnamese diacritics removed (đ → d), punctuation → space, optional politeness words
    /// ("nexus", "hãy", "làm ơn", "đi", "nhé", "giúp tôi"…) dropped at both ends, then an exact phrase table plus three patterns
    /// with a number (port, timer). Anything else (a free question) returns null and stays as typed. No regex.
    /// </summary>
    public static class VoiceCommandMapper
    {
        private static readonly (string Phrase, string Command)[] Exact =
        {
            ("kiem tra mang", "/net"), ("radar mang", "/net"), ("quet mang", "/net"), ("trang thai mang", "/net"), ("mang", "/net"),
            ("check network", "/net"), ("network radar", "/net"), ("network status", "/net"),
            ("trang thai mic", "/mic status"), ("trang thai micro", "/mic status"), ("kiem tra mic", "/mic status"), ("kiem tra micro", "/mic status"),
            ("mic status", "/mic status"), ("danh sach mic", "/mic list"), ("danh sach micro", "/mic list"),
            ("ngu canh", "/ctx"), ("doc ngu canh", "/ctx"), ("lay ngu canh", "/ctx"), ("context", "/ctx"),
            ("bao cao qa", "/qa"), ("kiem tra qa", "/qa"), ("chay qa", "/qa"), ("qa report", "/qa"),
            ("danh sach hen gio", "/timer list"), ("xem hen gio", "/timer list"), ("huy hen gio", "/timer cancel all"), ("huy tat ca hen gio", "/timer cancel all"),
            ("trang thai giong noi", "/ptt status"), ("trang thai stt", "/ptt status")
        };

        private static readonly string[] Lead = { "nexus ", "hay ", "lam on ", "vui long ", "please ", "cho toi ", "giup toi " };
        private static readonly string[] Tail = { " di", " nhe", " giup toi", " giup minh", " please", " nha", " voi" };

        private static readonly (string Word, int Value)[] Numbers =
        {
            ("muoi lam", 15), ("hai muoi", 20), ("ba muoi", 30), ("bon muoi lam", 45), ("sau muoi", 60),
            ("mot", 1), ("hai", 2), ("ba", 3), ("bon", 4), ("tu", 4), ("nam", 5), ("sau", 6), ("bay", 7), ("tam", 8), ("chin", 9), ("muoi", 10)
        };

        public static VoiceCommandMatch? TryMap(string? transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 80) return null; // a long sentence is a question
            string s = Normalize(transcript);
            if (s.Length == 0) return null;
            s = Strip(s);
            foreach (var (phrase, command) in Exact)
                if (s == phrase) return new VoiceCommandMatch(command, phrase, s);

            // "kiem tra cong 8080" / "cong 5055" / "check port 443" → /net port N
            foreach (string head in new[] { "kiem tra cong ", "cong ", "check port ", "port " })
                if (s.StartsWith(head, StringComparison.Ordinal) && TryNumber(s.Substring(head.Length), out int port) && port is >= 1 and <= 65535)
                    return new VoiceCommandMatch($"/net port {port.ToString(CultureInfo.InvariantCulture)}", head.Trim() + " <N>", s);

            // "hen gio 5 phut" / "dat hen gio muoi phut" / "timer 90 seconds" → /timer 5m | 10m | 90s
            foreach (string head in new[] { "dat hen gio ", "hen gio ", "timer " })
            {
                if (!s.StartsWith(head, StringComparison.Ordinal)) continue;
                string rest = s.Substring(head.Length);
                foreach (var (unitWord, unit) in new[] { (" phut", "m"), (" giay", "s"), (" tieng", "h"), (" gio", "h"), (" minutes", "m"), (" minute", "m"), (" seconds", "s"), (" hours", "h") })
                    if (rest.EndsWith(unitWord, StringComparison.Ordinal) && TryNumber(rest.Substring(0, rest.Length - unitWord.Length), out int n) && n is >= 1 and <= 600)
                        return new VoiceCommandMatch($"/timer {n.ToString(CultureInfo.InvariantCulture)}{unit}", head.Trim() + " <N> " + unitWord.Trim(), s);
            }
            return null;
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
