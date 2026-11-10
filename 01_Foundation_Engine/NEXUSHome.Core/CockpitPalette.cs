using System;
using System.Collections.Generic;
using System.Linq;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-16c (ADR-68) board 2A: one row of the "/" command palette (name, Vietnamese description, example).</summary>
    public sealed record PaletteCommand(string Name, string Description, string Example);

    /// <summary>One row of the "@" agent palette (board 2B).</summary>
    public sealed record PaletteAgent(string Id, string Name, string Division);

    /// <summary>What the command box palette shows for the text typed so far ("/..." or "...@...").</summary>
    public sealed record PaletteQuery(string Mode, string Query, int TokenStart);

    /// <summary>
    /// TASK-16c (ADR-68): the command box suggestions of design board 2. Pure functions, no UI and no I/O, so the selftest
    /// checks the filtering without a window. The window keeps at most a few rows on screen and reuses them.
    /// </summary>
    public static class CockpitPalette
    {
        public const string SlashMode = "slash";
        public const string AgentMode = "agent";
        public const int MaxRecent = 3;

        /// <summary>The "TẤT CẢ" list, in the order of the spec (board 2A) plus /f, which the card's "Hỏi tiếp" button uses.</summary>
        public static IReadOnlyList<PaletteCommand> Commands { get; } = new[]
        {
            new PaletteCommand("/img", "Hỏi về ảnh trong clipboard hoặc file", "/img lỗi gì đây?"),
            new PaletteCommand("/calc", "Tính nhanh, 0 token", "/calc sqrt(144)+2^8"),
            new PaletteCommand("/ask", "Hỏi Gemini qua squad phù hợp", "/ask cách dùng Span<T>"),
            new PaletteCommand("/g", "Hỏi kèm tài liệu trong Vault", "/g ADR-09"),
            new PaletteCommand("/snap", "Chụp màn hình G272 và hỏi (bị chặn khi đang chơi game)", "/snap đọc lỗi này"),
            new PaletteCommand("/pin", "Ghim thẻ hoặc ghi chú lên cột phải", "/pin #2"),
            new PaletteCommand("/ctx", "Hỏi kèm cửa sổ vừa dùng (khiên chống gian lận)", "/ctx giải thích lỗi này"),
            new PaletteCommand("/timer", "Hẹn giờ đếm ngược, 0 token", "/timer 25m kiểm tra build"),
            new PaletteCommand("/note", "Ghi chú nhanh vào quick_notes.md", "/note cổng TCP 8888"),
            new PaletteCommand("/copy", "Chép khối code / câu trả lời (Alt+C)", "/copy 1"),
            new PaletteCommand("/unpin", "Bỏ ghim một thẻ hoặc tất cả", "/unpin all"),
            new PaletteCommand("/qa", "QA agent chấm câu trả lời mới nhất", "/qa"),
            new PaletteCommand("/retry", "Agent tự sửa theo nhận xét QA", "/retry"),
            new PaletteCommand("/f", "Hỏi tiếp trong cùng cuộc hội thoại", "/f giải thích kỹ hơn"),
            new PaletteCommand("/mem", "RAM của HostService (Private WS)", "/mem"),
            new PaletteCommand("/status", "Trạng thái HostService, cửa sổ, agent", "/status"),
            new PaletteCommand("/boost", "Game Boost cho cửa sổ trước đó", "/boost"),
            new PaletteCommand("/ui", "Độ cao, xuyên thấu, tự thu gọn, phím tắt", "/ui half")
        };

        /// <summary>
        /// Which palette the text asks for: "/abc" without a space = commands filtered by "abc"; a last word "@abc" (no space after
        /// it) = agents filtered by "abc". Anything else = none (null).
        /// </summary>
        public static PaletteQuery? Detect(string? text)
        {
            string t = text ?? string.Empty;
            if (t.StartsWith('/') && t.IndexOf(' ') < 0) return new PaletteQuery(SlashMode, t.Substring(1), 0);
            int at = t.LastIndexOf('@');
            if (at >= 0 && (at == 0 || t[at - 1] == ' ') && t.IndexOf(' ', at) < 0)
                return new PaletteQuery(AgentMode, t.Substring(at + 1), at);
            return null;
        }

        /// <summary>Board 2A: "VỪA DÙNG" (recent commands that match, newest first) and "TẤT CẢ" (the rest, best match first).</summary>
        public static (IReadOnlyList<PaletteCommand> Recent, IReadOnlyList<PaletteCommand> All) FilterCommands(
            string? query, IEnumerable<string>? recentNames, int maxAll = 12)
        {
            string q = (query ?? string.Empty).Trim().TrimStart('/');
            var ranked = Commands
                .Select((c, i) => (c, i, score: Score(c, q)))
                .Where(x => x.score >= 0)
                .OrderBy(x => x.score).ThenBy(x => x.i)
                .Select(x => x.c)
                .ToList();
            var recent = new List<PaletteCommand>();
            foreach (string name in recentNames ?? Array.Empty<string>())
            {
                PaletteCommand? hit = ranked.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (hit != null && !recent.Contains(hit)) recent.Add(hit);
                if (recent.Count >= MaxRecent) break;
            }
            return (recent, ranked.Where(c => !recent.Contains(c)).Take(Math.Max(0, maxAll)).ToList());

            static int Score(PaletteCommand c, string q)
            {
                if (q.Length == 0) return 0;
                string name = c.Name.Substring(1);
                if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
                if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 1;
                if (c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
                return -1;
            }
        }

        /// <summary>
        /// Board 2B: agents of the registry whose name, id or division matches the query (name prefix first, then a word of the
        /// name, then anywhere in the name, the id, the division). Placeholders are left out.
        /// </summary>
        public static IReadOnlyList<PaletteAgent> FilterAgents(IEnumerable<AgentProfile> agents, string? query, int max = 8)
        {
            string q = (query ?? string.Empty).Trim();
            return agents
                .Where(a => !a.IsPlaceholder)
                .Select(a => (a, score: Score(a, q)))
                .Where(x => x.score >= 0)
                .OrderBy(x => x.score).ThenBy(x => x.a.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, max))
                .Select(x => new PaletteAgent(x.a.Id, x.a.Name, x.a.Division))
                .ToList();

            static int Score(AgentProfile a, string q)
            {
                if (q.Length == 0) return 0;
                if (a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
                if (a.Name.Split(' ', '-', '&').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 1;
                if (a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
                if (a.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
                if (a.Division.Contains(q, StringComparison.OrdinalIgnoreCase)) return 4;
                return -1;
            }
        }

        /// <summary>Remembers the command name of a sent line for "VỪA DÙNG" (newest first, at most <see cref="MaxRecent"/>).</summary>
        public static void NoteRecent(List<string> recent, string? sentText)
        {
            string t = (sentText ?? string.Empty).Trim();
            if (!t.StartsWith('/')) return;
            string name = t.Split(' ', 2)[0].ToLowerInvariant();
            if (Commands.All(c => !c.Name.Equals(name, StringComparison.Ordinal))) return;
            recent.Remove(name);
            recent.Insert(0, name);
            if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        }
    }
}
