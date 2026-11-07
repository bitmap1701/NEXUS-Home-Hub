using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NEXUSHomeHub.Core
{
    /// <summary>One element of agent_dispatch_logs.turn_history_json (TASK-09 §3.1.1). Role = "user" | "model" | "qa".</summary>
    public sealed record TurnEntry(int Turn, string Role, string Text, string TimestampUtc);

    /// <summary>One element of the Gemini generateContent "contents" array. Role = "user" | "model".</summary>
    public sealed record GeminiTurn(string Role, string Text);

    /// <summary>What <see cref="TurnHistory.BuildContents"/> sent (selftest / mock evidence).</summary>
    public sealed record ContentsWindow(IReadOnlyList<GeminiTurn> Contents, int PairsAvailable, int PairsSent, bool Synthesized);

    /// <summary>
    /// TASK-09 ADR-37: turn_history_json helpers. Tolerant: a missing, empty or broken history is an empty list (the dispatch
    /// keeps working; /followup then rebuilds turn 1 from task_summary + ai_response). QA entries are kept in the history
    /// for the record but never sent to the Primary Agent as conversation turns.
    /// </summary>
    public static class TurnHistory
    {
        public const string RoleUser = "user";
        public const string RoleModel = "model";
        public const string RoleQa = "qa";

        /// <summary>Upper bound per stored entry (a Gemini answer is capped by geminiMaxOutputTokens anyway).</summary>
        public const int MaxEntryChars = 8000;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        public static List<TurnEntry> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TurnEntry>();
            try
            {
                List<TurnEntry>? entries = JsonSerializer.Deserialize<List<TurnEntry>>(json, Json);
                return entries == null
                    ? new List<TurnEntry>()
                    : entries.Where(e => e != null && !string.IsNullOrEmpty(e.Role))
                        .Select(e => e with { Text = e.Text ?? string.Empty, TimestampUtc = e.TimestampUtc ?? string.Empty })
                        .ToList();
            }
            catch (JsonException)
            {
                return new List<TurnEntry>();
            }
        }

        public static string Serialize(IEnumerable<TurnEntry> entries) => JsonSerializer.Serialize(entries, Json);

        /// <summary>Keeps the newest <paramref name="maxEntries"/> entries (spec §3.3.2: maxConversationTurns * 2).</summary>
        public static List<TurnEntry> Trim(List<TurnEntry> entries, int maxEntries)
        {
            int max = Math.Max(2, maxEntries);
            return entries.Count <= max ? entries : entries.Skip(entries.Count - max).ToList();
        }

        public static string Clip(string text) =>
            text.Length > MaxEntryChars ? text.Substring(0, MaxEntryChars) + "…" : text;

        /// <summary>Turn-1 history written by /ask (user question + model answer).</summary>
        public static string FirstTurn(string question, string answer, bool answered)
        {
            string ts = DateTime.UtcNow.ToString("o");
            var entries = new List<TurnEntry> { new(1, RoleUser, Clip(question), ts) };
            if (answered) entries.Add(new TurnEntry(1, RoleModel, Clip(answer), ts));
            return Serialize(entries);
        }

        /// <summary>
        /// Multi-turn "contents" for /followup: the newest <paramref name="maxPairs"/> user/model pairs of the history, then
        /// <paramref name="newUserText"/>. Gemini needs strictly alternating roles starting with "user": consecutive entries
        /// of the same role are merged, a leading model entry is dropped, and a trailing unanswered user entry is merged
        /// into the new question. An empty history (rows from before TASK-09) is rebuilt from task_summary + ai_response.
        /// </summary>
        public static ContentsWindow BuildContents(RecentDispatch row, int maxPairs, string newUserText)
        {
            var turns = Parse(row.TurnHistoryJson)
                .Where(e => e.Role == RoleUser || e.Role == RoleModel)
                .Select(e => new GeminiTurn(e.Role, e.Text))
                .ToList();
            bool synthesized = false;
            if (turns.Count == 0)
            {
                synthesized = true;
                turns.Add(new GeminiTurn(RoleUser, row.TaskSummary));
                if (row.AiResponse.Length > 0) turns.Add(new GeminiTurn(RoleModel, row.AiResponse));
            }

            var merged = new List<GeminiTurn>();
            foreach (GeminiTurn turn in turns)
            {
                if (merged.Count == 0 && turn.Role == RoleModel) continue;
                if (merged.Count > 0 && merged[^1].Role == turn.Role)
                    merged[^1] = merged[^1] with { Text = merged[^1].Text + "\n\n" + turn.Text };
                else
                    merged.Add(turn);
            }

            string question = newUserText;
            if (merged.Count > 0 && merged[^1].Role == RoleUser)
            {
                question = merged[^1].Text + "\n\n" + newUserText;
                merged.RemoveAt(merged.Count - 1);
            }

            // merged now alternates user/model and ends with a model entry: it holds Count / 2 complete pairs.
            int available = merged.Count / 2;
            int sent = Math.Min(available, Math.Max(1, maxPairs));
            var contents = merged.Skip(merged.Count - sent * 2).ToList();
            contents.Add(new GeminiTurn(RoleUser, question));
            return new ContentsWindow(contents, available, sent, synthesized);
        }

        /// <summary>Text of the newest user entry (QA review of a follow-up answer), or null.</summary>
        public static string? LatestUserText(RecentDispatch row) =>
            Parse(row.TurnHistoryJson).LastOrDefault(e => e.Role == RoleUser)?.Text;
    }
}
