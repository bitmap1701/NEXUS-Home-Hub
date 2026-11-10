using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-13: one actionable card taken from a response. <see cref="Type"/> = "code" | "checklist" | "utility".
    /// <see cref="Content"/> is what /copy and Alt+C put on the clipboard (code without its fences).
    /// </summary>
    public sealed record HudWidgetCard(
        int Index,
        string Type,
        string Title,
        string? Language,
        int LineCount,
        string PreviewText,
        [property: JsonIgnore] string Content);

    /// <summary>TASK-13: light card summary written into latest_dispatch.json ("extractedCards", at most 5).</summary>
    public sealed record HudWidgetCardSummary(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("lineCount")] int LineCount,
        [property: JsonPropertyName("previewText")] string PreviewText);

    /// <summary>
    /// TASK-13: line scanner (no Regex, ADR-29) that finds in a Gemini / QA answer or a local utility result:
    /// fenced code blocks (``` or ~~~, optional language after the fence), checklists (- [ ] / - [x] / * [ ] lines) and
    /// short numbered step lists (1. / 1) lines, two or more in a row). A local utility result (/calc, /timer, /note)
    /// becomes one "utility" card. Cards are numbered from 1 in reading order; code blocks come first in /copy because
    /// /copy N means "code block N".
    /// </summary>
    public static class WidgetCardExtractor
    {
        public const int MaxCards = 20;
        public const int MaxSummaryCards = 5;
        private const int PreviewChars = 80;
        private const int TitleChars = 60;

        public static IReadOnlyList<HudWidgetCard> Extract(string? text, string? localUtility = null)
        {
            var cards = new List<HudWidgetCard>();
            if (string.IsNullOrEmpty(text)) return cards;

            if (!string.IsNullOrEmpty(localUtility))
            {
                string firstLine = FirstLine(text);
                cards.Add(new HudWidgetCard(1, "utility", Shorten($"/{localUtility}: {firstLine}", TitleChars), localUtility,
                    CountLines(text), Shorten(firstLine, PreviewChars), text.Trim()));
                return cards;
            }

            var lines = new List<string>();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n')) lines.Add(raw);

            int i = 0;
            while (i < lines.Count && cards.Count < MaxCards)
            {
                string trimmed = lines[i].TrimStart();
                if (IsFence(trimmed, out char fenceChar, out int fenceLength))
                {
                    string language = trimmed.Substring(fenceLength).Trim();
                    int space = language.IndexOf(' ');
                    if (space > 0) language = language.Substring(0, space);
                    var code = new StringBuilder();
                    int bodyLines = 0;
                    int j = i + 1;
                    bool closed = false;
                    for (; j < lines.Count; j++)
                    {
                        string t = lines[j].TrimStart();
                        if (IsClosingFence(t, fenceChar, fenceLength)) { closed = true; break; }
                        if (bodyLines > 0) code.Append('\n');
                        code.Append(lines[j]);
                        bodyLines++;
                    }
                    string content = code.ToString();
                    if (content.Trim().Length > 0)
                    {
                        string head = FirstNonEmptyLine(content);
                        string lang = language.Length > 0 ? language.ToLowerInvariant() : "text";
                        cards.Add(new HudWidgetCard(cards.Count + 1, "code", Shorten($"{lang}: {head}", TitleChars), lang,
                            bodyLines, Shorten(head, PreviewChars), content));
                    }
                    i = closed ? j + 1 : j;
                    continue;
                }

                if (IsChecklistLine(trimmed) || IsNumberedStep(trimmed))
                {
                    bool checklist = IsChecklistLine(trimmed);
                    var items = new List<string>();
                    int j = i;
                    while (j < lines.Count)
                    {
                        string t = lines[j].TrimStart();
                        if (checklist ? IsChecklistLine(t) : IsNumberedStep(t)) { items.Add(t); j++; continue; }
                        break;
                    }
                    if (checklist || items.Count >= 2)
                    {
                        string body = string.Join("\n", items);
                        int done = 0;
                        foreach (string item in items) if (item.Contains("[x]", StringComparison.OrdinalIgnoreCase)) done++;
                        string title = checklist ? $"checklist {done}/{items.Count}: {StepText(items[0])}" : $"{items.Count} bước: {StepText(items[0])}";
                        cards.Add(new HudWidgetCard(cards.Count + 1, "checklist", Shorten(title, TitleChars), null, items.Count,
                            Shorten(StepText(items[0]), PreviewChars), body));
                    }
                    i = j;
                    continue;
                }
                i++;
            }
            return cards;
        }

        /// <summary>The first <see cref="MaxSummaryCards"/> cards as the light latest_dispatch.json summary.</summary>
        public static IReadOnlyList<HudWidgetCardSummary> Summaries(IReadOnlyList<HudWidgetCard> cards)
        {
            var list = new List<HudWidgetCardSummary>(Math.Min(cards.Count, MaxSummaryCards));
            for (int i = 0; i < cards.Count && i < MaxSummaryCards; i++)
            {
                HudWidgetCard c = cards[i];
                list.Add(new HudWidgetCardSummary(c.Index, c.Type, c.Title, c.Language, c.LineCount, c.PreviewText));
            }
            return list;
        }

        /// <summary>TASK-16 B5: the Right MFD label "Câu trả lời hiện tại: X thẻ" (the detailed <see cref="Indicator"/> is its tooltip).</summary>
        public static string CardCountLabel(IReadOnlyList<HudWidgetCard> cards) => $"Câu trả lời hiện tại: {cards.Count} thẻ";

        /// <summary>"[2 Code Blocks | 1 Checklist | Alt+C: Copy #1 | Alt+P: Pin]" for the HUD indicator.</summary>
        public static string Indicator(IReadOnlyList<HudWidgetCard> cards)
        {
            if (cards.Count == 0) return "[0 thẻ | Alt+P: Pin kết quả]";
            int code = 0, checklist = 0, utility = 0;
            foreach (HudWidgetCard c in cards)
            {
                if (c.Type == "code") code++;
                else if (c.Type == "checklist") checklist++;
                else utility++;
            }
            var parts = new List<string>();
            if (code > 0) parts.Add($"{code} Code Block{(code > 1 ? "s" : string.Empty)}");
            if (checklist > 0) parts.Add($"{checklist} Checklist{(checklist > 1 ? "s" : string.Empty)}");
            if (utility > 0) parts.Add("Utility");
            parts.Add(code > 0 ? "Alt+C: Copy #1" : "Alt+C: Copy");
            parts.Add("Alt+P: Pin");
            return "[" + string.Join(" | ", parts) + "]";
        }

        /// <summary>
        /// TASK-12 §3.1 (Gate 13 polish): the preview line a Right MFD slot shows under the card title. The title already
        /// carries the first line ("csharp: var listener = …", "checklist 1/3: Mở cổng 8888", or the note text itself), so
        /// when the first non-empty content line repeats it the preview starts at the next line; a one-line card shows
        /// only its title (empty string).
        /// </summary>
        public static string PreviewAfterTitle(string? title, string? content)
        {
            var lines = new List<string>();
            foreach (string raw in (content ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length > 0) lines.Add(t);
            }
            if (lines.Count == 0) return string.Empty;
            return RepeatsTitle(title ?? string.Empty, lines[0])
                ? (lines.Count > 1 ? lines[1] : string.Empty)
                : lines[0];
        }

        /// <summary>True when <paramref name="line"/> is what the title already shows (title may be cut with "…").</summary>
        private static bool RepeatsTitle(string title, string line)
        {
            string full = title.Trim().TrimEnd('…').TrimEnd();
            if (full.Length == 0) return false;
            int colon = full.IndexOf(": ", StringComparison.Ordinal);
            string afterPrefix = colon >= 0 ? full.Substring(colon + 2).Trim() : string.Empty;
            string step = StepText(line);
            foreach (string shown in new[] { full, afterPrefix })
            {
                if (shown.Length == 0) continue;
                foreach (string candidate in new[] { line, step })
                {
                    if (candidate.Length == 0) continue;
                    if (candidate.StartsWith(shown, StringComparison.Ordinal) || shown.StartsWith(candidate, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static bool IsFence(string trimmed, out char fenceChar, out int length)
        {
            fenceChar = '\0';
            length = 0;
            if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~')) return false;
            char c = trimmed[0];
            int n = 0;
            while (n < trimmed.Length && trimmed[n] == c) n++;
            if (n < 3) return false;
            if (c == '`' && trimmed.IndexOf('`', n) >= 0) return false; // inline ```code``` on one line
            fenceChar = c;
            length = n;
            return true;
        }

        private static bool IsClosingFence(string trimmed, char fenceChar, int fenceLength)
        {
            int n = 0;
            while (n < trimmed.Length && trimmed[n] == fenceChar) n++;
            return n >= fenceLength && trimmed.Substring(n).Trim().Length == 0;
        }

        private static bool IsChecklistLine(string t) =>
            t.Length >= 5 && (t[0] == '-' || t[0] == '*' || t[0] == '+') && t[1] == ' ' && t[2] == '[' &&
            (t[3] == ' ' || t[3] == 'x' || t[3] == 'X') && t[4] == ']';

        private static bool IsNumberedStep(string t)
        {
            int n = 0;
            while (n < t.Length && n < 3 && char.IsDigit(t[n])) n++;
            return n > 0 && n + 1 < t.Length && (t[n] == '.' || t[n] == ')') && t[n + 1] == ' ' && t.Length > n + 2;
        }

        /// <summary>"- [ ] Mở cổng 8888" -> "Mở cổng 8888"; "2) Chạy build" -> "Chạy build".</summary>
        private static string StepText(string item)
        {
            string t = item.TrimStart();
            if (IsChecklistLine(t)) return t.Substring(5).Trim();
            int n = 0;
            while (n < t.Length && char.IsDigit(t[n])) n++;
            return n > 0 && n + 1 < t.Length ? t.Substring(n + 1).Trim() : t;
        }

        private static string FirstLine(string text)
        {
            int nl = text.IndexOf('\n');
            return (nl < 0 ? text : text.Substring(0, nl)).Trim();
        }

        private static string FirstNonEmptyLine(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().Length > 0) return line.Trim();
            }
            return string.Empty;
        }

        private static int CountLines(string text)
        {
            int n = 1;
            foreach (char c in text) if (c == '\n') n++;
            return n;
        }

        private static string Shorten(string text, int max) => text.Length > max ? text.Substring(0, max - 1) + "…" : text;
    }
}
