using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NEXUSHomeHub.Core
{
    public enum MdSpanKind { Text, Bold, Code }

    /// <summary>One run of inline text: plain, **bold** or `code`.</summary>
    public readonly record struct MdSpan(MdSpanKind Kind, string Text);

    public enum MdBlockKind { Paragraph, Bullet, Heading, CodeBlock, Blank }

    /// <summary>One line-level block. A code block keeps its raw text in <see cref="Code"/> (no inline parsing inside).</summary>
    public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdSpan> Spans, string? Language = null, string? Code = null);

    /// <summary>Parsed answer plus the counts Verify-Task16b E2 checks.</summary>
    public sealed record MdDocument(IReadOnlyList<MdBlock> Blocks, int BoldSpans, int CodeSpans, int CodeBlocks, int Bullets, int Headings, double ParseMs);

    /// <summary>
    /// TASK-16b CUI-Markdown (ADR-66): the few Markdown pieces Gemini answers use, parsed without a library and without Regex
    /// (one pass over the characters, so a 4 KB answer takes well under 1 ms):
    ///   ```lang … ``` fenced code block (an unclosed fence runs to the end), "- " / "* " / "+ " bullet (→ "• "),
    ///   "# " … "###### " heading, **bold** and `inline code` inside any other line. Anything unmatched stays literal text,
    ///   so empty input, a lone "**" or a lone "`" never throws. Platform-neutral: the Cockpit turns the blocks into WPF Inlines.
    /// </summary>
    public static class LightweightMarkdown
    {
        private static readonly IReadOnlyList<MdSpan> NoSpans = Array.Empty<MdSpan>();

        public static MdDocument Parse(string? text)
        {
            long t0 = Stopwatch.GetTimestamp();
            var blocks = new List<MdBlock>();
            int bold = 0, code = 0, codeBlocks = 0, bullets = 0, headings = 0;
            if (!string.IsNullOrEmpty(text))
            {
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("```", StringComparison.Ordinal))
                    {
                        string language = trimmed.Substring(3).Trim();
                        var body = new System.Text.StringBuilder();
                        int j = i + 1;
                        for (; j < lines.Length; j++)
                        {
                            if (lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                            if (body.Length > 0) body.Append('\n');
                            body.Append(lines[j]);
                        }
                        blocks.Add(new MdBlock(MdBlockKind.CodeBlock, NoSpans, language.Length == 0 ? null : language, body.ToString()));
                        codeBlocks++;
                        i = j; // skips the closing fence (or ends the loop when the fence is unclosed)
                        continue;
                    }
                    if (trimmed.Length == 0)
                    {
                        blocks.Add(new MdBlock(MdBlockKind.Blank, NoSpans));
                        continue;
                    }
                    MdBlockKind kind = MdBlockKind.Paragraph;
                    string content = line;
                    if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
                    {
                        kind = MdBlockKind.Bullet;
                        content = trimmed.Substring(2);
                        bullets++;
                    }
                    else if (trimmed[0] == '#')
                    {
                        int level = 0;
                        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
                        if (level < trimmed.Length && trimmed[level] == ' ')
                        {
                            kind = MdBlockKind.Heading;
                            content = trimmed.Substring(level + 1);
                            headings++;
                        }
                    }
                    IReadOnlyList<MdSpan> spans = ParseInline(content, ref bold, ref code);
                    blocks.Add(new MdBlock(kind, spans));
                }
            }
            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 4);
            return new MdDocument(blocks, bold, code, codeBlocks, bullets, headings, ms);
        }

        /// <summary>**bold** and `code` in one line; an opener without its closer is kept as literal text.</summary>
        public static IReadOnlyList<MdSpan> ParseInline(string line, ref int boldCount, ref int codeCount)
        {
            var spans = new List<MdSpan>();
            var plain = new System.Text.StringBuilder();
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '`')
                {
                    int close = line.IndexOf('`', i + 1);
                    if (close > i + 1)
                    {
                        Flush(spans, plain);
                        spans.Add(new MdSpan(MdSpanKind.Code, line.Substring(i + 1, close - i - 1)));
                        codeCount++;
                        i = close + 1;
                        continue;
                    }
                }
                else if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    int close = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        Flush(spans, plain);
                        spans.Add(new MdSpan(MdSpanKind.Bold, line.Substring(i + 2, close - i - 2)));
                        boldCount++;
                        i = close + 2;
                        continue;
                    }
                }
                plain.Append(c);
                i++;
            }
            Flush(spans, plain);
            return spans;
        }

        private static void Flush(List<MdSpan> spans, System.Text.StringBuilder plain)
        {
            if (plain.Length == 0) return;
            spans.Add(new MdSpan(MdSpanKind.Text, plain.ToString()));
            plain.Clear();
        }
    }
}
