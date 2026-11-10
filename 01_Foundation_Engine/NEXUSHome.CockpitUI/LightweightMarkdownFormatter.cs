using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NEXUSHomeHub.Core;

namespace NEXUSHome.CockpitUI
{
    /// <summary>What the last render produced (pipe UISTATE, Verify-Task16b E2).</summary>
    internal sealed record MarkdownRenderInfo(int Blocks, int BoldRuns, int CodeRuns, int CodeBlocks, int Bullets, int Headings, int Inlines,
        double ParseMs, double RenderMs, int Chars);

    /// <summary>
    /// TASK-16b CUI-Markdown (ADR-66): turns <see cref="LightweightMarkdown"/> blocks into Inlines of the answer TextBlock.
    /// No NuGet package, no FlowDocument (a TextBlock with Inlines is far lighter):
    ///   **bold** → bold Run #F8FAFC · `code` → Consolas Run, background #1E293B, text #38BDF8 · ``` block → InlineUIContainer
    ///   with a #0F172A box and a thin #3338BDF8 border (monospace, wraps) · "- " / "* " → "• " · "# " → bold heading.
    /// Brushes are frozen and shared, so a re-render allocates only the Inlines themselves.
    /// </summary>
    internal static class LightweightMarkdownFormatter
    {
        private static readonly Brush BoldBrush = Frozen(Color.FromRgb(0xF8, 0xFA, 0xFC));
        private static readonly Brush CodeForeground = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
        private static readonly Brush CodeBackground = Frozen(Color.FromRgb(0x1E, 0x29, 0x3B));
        private static readonly Brush BlockBackground = Frozen(Color.FromRgb(0x0F, 0x17, 0x2A));
        private static readonly Brush BlockBorder = Frozen(Color.FromArgb(0x33, 0x38, 0xBD, 0xF8));
        private static readonly Brush BlockText = Frozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
        private static readonly Brush HeadingBrush = Frozen(Color.FromRgb(0xA7, 0x8B, 0xFA));
        private static readonly Brush BulletBrush = Frozen(Color.FromRgb(0x2D, 0xD4, 0xBF));
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        public static MarkdownRenderInfo Render(TextBlock target, string? text)
        {
            long t0 = Stopwatch.GetTimestamp();
            MdDocument doc = LightweightMarkdown.Parse(text);
            InlineCollection inlines = target.Inlines;
            inlines.Clear();
            double width = target.ActualWidth > 40 ? target.ActualWidth - 12 : 520;
            int count = 0;
            for (int b = 0; b < doc.Blocks.Count; b++)
            {
                MdBlock block = doc.Blocks[b];
                if (b > 0 && block.Kind != MdBlockKind.CodeBlock)
                {
                    inlines.Add(new LineBreak());
                    count++;
                }
                switch (block.Kind)
                {
                    case MdBlockKind.Blank:
                        break;
                    case MdBlockKind.CodeBlock:
                        if (b > 0)
                        {
                            inlines.Add(new LineBreak());
                            count++;
                        }
                        inlines.Add(CodeBlock(block, width));
                        count++;
                        break;
                    case MdBlockKind.Heading:
                        foreach (MdSpan span in block.Spans)
                        {
                            inlines.Add(new Run(span.Text) { FontWeight = FontWeights.Bold, Foreground = HeadingBrush });
                            count++;
                        }
                        break;
                    default:
                        if (block.Kind == MdBlockKind.Bullet)
                        {
                            inlines.Add(new Run("  • ") { Foreground = BulletBrush });
                            count++;
                        }
                        foreach (MdSpan span in block.Spans)
                        {
                            inlines.Add(SpanRun(span));
                            count++;
                        }
                        break;
                }
            }
            double renderMs = System.Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            return new MarkdownRenderInfo(doc.Blocks.Count, doc.BoldSpans, doc.CodeSpans, doc.CodeBlocks, doc.Bullets, doc.Headings, count,
                doc.ParseMs, renderMs, text?.Length ?? 0);
        }

        private static Run SpanRun(MdSpan span) => span.Kind switch
        {
            MdSpanKind.Bold => new Run(span.Text) { FontWeight = FontWeights.Bold, Foreground = BoldBrush },
            MdSpanKind.Code => new Run(span.Text) { FontFamily = Mono, Background = CodeBackground, Foreground = CodeForeground },
            _ => new Run(span.Text)
        };

        private static InlineUIContainer CodeBlock(MdBlock block, double width)
        {
            var body = new TextBlock
            {
                Text = block.Code ?? string.Empty,
                FontFamily = Mono,
                FontSize = 11.5,
                Foreground = BlockText,
                TextWrapping = TextWrapping.Wrap
            };
            var box = new Border
            {
                Background = BlockBackground,
                BorderBrush = BlockBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 2, 0, 2),
                Width = width,
                Child = body,
                ToolTip = block.Language is { Length: > 0 } lang ? $"{lang} · Alt+C / phím 1: copy" : "Alt+C / phím 1: copy"
            };
            return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Top };
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
