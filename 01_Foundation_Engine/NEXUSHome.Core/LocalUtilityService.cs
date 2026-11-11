using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// Result of one local utility call. Tokens are always 0 (ADR-59): no Gemini call, no quota slot, no token counter
    /// (live or mock) moves. <see cref="ElapsedMs"/> = whole synchronous path including the ADR-14 write of
    /// latest_dispatch.json; <see cref="ComputeMs"/> = the utility alone.
    /// </summary>
    public sealed record LocalUtilityResult(
        string Utility,
        string Action,
        bool Ok,
        bool Blocked,
        string Message,
        double ComputeMs,
        double ElapsedMs,
        string Status,
        LatestDispatchSnapshot? Snapshot,
        string? FileError,
        object? Data)
    {
        public string AiModel => LatestDispatchFile.LocalUtilityModel;
        public int PromptTokens => 0;
        public int CompletionTokens => 0;
        public int TotalTokens => 0;
    }

    /// <summary>
    /// TASK-14 ADR-59: Zero-Token Local Utilities shared by the Cockpit command box (<see cref="CockpitCommandProcessor"/>)
    /// and the MCP tool <c>run_local_utility</c>: <c>/calc</c> (<see cref="LocalCalculator"/>), <c>/timer</c>
    /// (<see cref="LocalTimerManager"/>), <c>/note</c> (<see cref="LocalNoteService"/>). Each call writes
    /// latest_dispatch.json atomically (ADR-14) with event "local_utility", ai_model "local-utility", status PASSED (or
    /// LOCAL_ERROR) and 0 tokens, so a running Cockpit picks up MCP calls through its watcher. A timer that expires writes
    /// event "timer_expired" / status EXPIRED and raises <see cref="TimerExpired"/> for the in-process Cockpit.
    /// No SQLite dispatch row is created; only /note writes one audit row (length only, never the text).
    /// </summary>
    public sealed class LocalUtilityService
    {
        public const string Calc = "calc";
        public const string Timer = "timer";
        public const string Note = "note";

        public const string TimerUsage =
            "Dùng: /timer <thời lượng> [nhãn] (250ms, 90s, 5m, 1h30m, 2m30s, 60 = giây) | /timer list (/timers) | /timer cancel <id|all> (stop, clear)";
        public const string NoteUsage =
            "Dùng: /note <nội dung> | /note list [N] | /note find <từ khóa> (search)";

        private readonly string _latestDispatchPath;
        private readonly string _source;
        private readonly SqliteMemoryStore? _store;

        public LocalUtilityService(string latestDispatchPath, string vaultPath, string source, SqliteMemoryStore? store,
            VaultGroundingEngine? grounding)
        {
            _latestDispatchPath = latestDispatchPath;
            _source = source;
            _store = store;
            Timers = new LocalTimerManager();
            Notes = new LocalNoteService(vaultPath);
            if (grounding != null) Notes.Changed += grounding.Invalidate; // /ground sees a new note on the very next call
            Timers.Expired += OnTimerExpired;
        }

        public LocalTimerManager Timers { get; }

        public LocalNoteService Notes { get; }

        /// <summary>Pool thread, after latest_dispatch.json holds the EXPIRED snapshot (FileError says if that write failed).</summary>
        public event Action<LocalTimerInfo, LatestDispatchSnapshot, string?>? TimerExpired;

        /// <summary>"calc" | "c" | "=" -> calc, "timer" | "timers" -> timer, "note" | "notes" -> note (case-insensitive, leading '/' allowed).</summary>
        public static bool TryNormalizeUtility(string? name, out string utility)
        {
            string n = (name ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
            utility = n switch
            {
                "calc" or "c" or "=" => Calc,
                "timer" or "timers" => Timer,
                "note" or "notes" => Note,
                _ => string.Empty
            };
            return utility.Length > 0;
        }

        public LocalUtilityResult Execute(string utility, string? commandText, bool writeLatest = true)
        {
            long ts0 = Stopwatch.GetTimestamp();
            string text = (commandText ?? string.Empty).Trim();
            string tag = utility.ToUpperInvariant();

            string action;
            bool ok;
            bool blocked = false;
            string message;
            object? data;

            if (LocalNoteService.ContainsSecret(text))
            {
                // Secret Guard first: a key never reaches the calculator, a timer label or the note file.
                action = "blocked";
                ok = false;
                blocked = true;
                message = $"[{tag} BLOCKED] Secret Guard: nội dung có chuỗi giống khóa bí mật (API key / private key) → không ghi, không lưu. Dùng /apikey set <KEY> cho khóa Gemini.";
                data = null;
                if (utility == Note) Audit("local_note", new { action = "blocked", chars = text.Length }, "BlockedSecret");
            }
            else
            {
                switch (utility)
                {
                    case Calc:
                        (action, ok, message, data) = RunCalc(text);
                        break;
                    case Timer:
                        (action, ok, message, data) = RunTimer(text);
                        break;
                    case Note:
                        (action, ok, message, data) = RunNote(text);
                        break;
                    default:
                        action = "unknown";
                        ok = false;
                        message = $"[LOCAL] Không biết tiện ích '{utility}' (calc | timer | note).";
                        data = null;
                        break;
                }
            }

            double computeMs = Ms(ts0);
            string status = ok ? "PASSED" : "LOCAL_ERROR";
            LatestDispatchSnapshot? snapshot = null;
            string? fileError = null;
            if (writeLatest && !blocked)
            {
                snapshot = LatestDispatchFile.FromLocalUtility(LatestDispatchFile.LocalUtilityEvent, utility, $"/{utility} {text}".TrimEnd(),
                    message, status, _source, computeMs);
                fileError = LatestDispatchFile.Write(_latestDispatchPath, snapshot);
            }
            double totalMs = Ms(ts0);
            if (snapshot != null) snapshot = snapshot with { TotalDispatchMs = totalMs };
            if (fileError != null) message += $" | latest_dispatch.json LỖI: {fileError}";
            return new LocalUtilityResult(utility, action, ok, blocked, message, computeMs, totalMs, status, snapshot, fileError, data);
        }

        private static double Ms(long ts0) =>
            Math.Round((Stopwatch.GetTimestamp() - ts0) * 1000.0 / Stopwatch.Frequency, 3);

        /// <summary>TASK-17 utilities whose work runs elsewhere (/net, /mic, /ptt): same ADR-14 snapshot and 0-token result.</summary>
        public const string Net = "net";
        public const string Mic = "mic";
        public const string Ptt = "ptt";

        /// <summary>
        /// TASK-17: records a finished /net, /mic or /ptt result exactly like /calc (latest_dispatch.json local event, 0 tokens),
        /// so the Cockpit thread shows it as a local line (or the Radar card) whether it finished at once or on a pool thread.
        /// </summary>
        public LocalUtilityResult Record(string utility, string commandText, string action, bool ok, string message, object? data,
            double computeMs, bool writeLatest = true)
        {
            long ts0 = Stopwatch.GetTimestamp();
            string status = ok ? "PASSED" : "LOCAL_ERROR";
            LatestDispatchSnapshot? snapshot = null;
            string? fileError = null;
            if (writeLatest)
            {
                snapshot = LatestDispatchFile.FromLocalUtility(LatestDispatchFile.LocalUtilityEvent, utility, commandText.Trim(),
                    message, status, _source, computeMs);
                fileError = LatestDispatchFile.Write(_latestDispatchPath, snapshot);
            }
            double totalMs = Math.Round(computeMs + Ms(ts0), 3);
            if (snapshot != null) snapshot = snapshot with { TotalDispatchMs = totalMs };
            if (fileError != null) message += $" | latest_dispatch.json LỖI: {fileError}";
            return new LocalUtilityResult(utility, action, ok, false, message, computeMs, totalMs, status, snapshot, fileError, data);
        }

        private static (string, bool, string, object?) RunCalc(string text)
        {
            LocalCalcResult r = LocalCalculator.Evaluate(text);
            string message = r.Ok || text.Length > 0 ? r.Message : $"{r.Message} | {LocalCalculator.Usage}";
            return ("evaluate", r.Ok, message, r);
        }

        private (string, bool, string, object?) RunTimer(string text)
        {
            string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string first = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            string rest = parts.Length > 1 ? parts[1] : string.Empty;

            if (first.Length == 0 || first == "list" || first == "ls")
            {
                var (active, ended) = Timers.List();
                return ("list", true, DescribeList(active, ended), new { active, recentEnded = ended });
            }

            if (first is "cancel" or "stop" or "clear")
            {
                IReadOnlyList<LocalTimerInfo> cancelled = Timers.Cancel(first == "clear" ? "all" : rest, out string? cancelError);
                if (cancelError != null) return ("cancel", false, $"[TIMER] {cancelError}", new { cancelled });
                string msg = cancelled.Count == 0
                    ? "[TIMER] Không có bộ đếm nào đang chạy để hủy."
                    : $"[TIMER] Đã hủy {cancelled.Count} bộ đếm: {string.Join(", ", cancelled.Select(c => $"#{c.Id} {c.Label}"))}. Đang chạy: {Timers.ActiveCount}.";
                return ("cancel", true, msg, new { cancelled, activeCount = Timers.ActiveCount });
            }

            if (!LocalTimerManager.TryParseDuration(parts[0], out long durationMs, out string? parseError))
                return ("start", false, $"[TIMER] {parseError} | {TimerUsage}", null);

            LocalTimerInfo? started = Timers.Start(durationMs, rest, out string? startError);
            if (started == null) return ("start", false, $"[TIMER] {startError}", null);

            string dueLocal = DateTime.Parse(started.DueUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return ("start", true,
                $"[TIMER #{started.Id}] Bắt đầu {started.DurationText}: {started.Label} (hết giờ lúc {dueLocal}, chạy ngầm 0 token)",
                new { started, activeCount = Timers.ActiveCount });
        }

        private static string DescribeList(IReadOnlyList<LocalTimerInfo> active, IReadOnlyList<LocalTimerInfo> ended)
        {
            string activeText = active.Count == 0
                ? "[TIMER] 0 bộ đếm đang chạy."
                : $"[TIMER] {active.Count} đang chạy (ACTIVE): " +
                  string.Join(" | ", active.Select(t => $"#{t.Id} {t.Label} còn {FormatRemaining(t.RemainingMs)} / {t.DurationText}"));
            if (ended.Count == 0) return activeText;
            string endedText = string.Join(" | ", ended.Take(3).Select(t =>
                $"#{t.Id} {t.Label} {t.State}" + (t.EndedUtc != null
                    ? " " + DateTime.Parse(t.EndedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : string.Empty)));
            return $"{activeText} Gần đây: {endedText}";
        }

        private static string FormatRemaining(long ms) =>
            ms >= 1000 ? LocalTimerManager.FormatDuration((ms + 999) / 1000 * 1000) : LocalTimerManager.FormatDuration(Math.Max(ms, 1));

        public static string ExpiredMessage(LocalTimerInfo t) => $"[TIMER EXPIRED] #{t.Id}: {t.Label} ({t.DurationText})";

        private void OnTimerExpired(LocalTimerInfo info)
        {
            string message = ExpiredMessage(info);
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromLocalUtility(LatestDispatchFile.TimerExpiredEvent, Timer,
                $"/timer {info.DurationText} {info.Label}", message, "EXPIRED", _source, 0);
            string? fileError = LatestDispatchFile.Write(_latestDispatchPath, snapshot);
            TimerExpired?.Invoke(info, snapshot, fileError);
        }

        private (string, bool, string, object?) RunNote(string text)
        {
            string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string first = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            string rest = parts.Length > 1 ? parts[1] : string.Empty;

            if (first.Length == 0) return ("append", false, $"[NOTE] {NoteUsage}", null);

            // "list" or "list 10" lists; "list of things to buy" is a note.
            if ((first == "list" || first == "ls") &&
                (rest.Length == 0 || int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                int n = rest.Length == 0 ? LocalNoteService.DefaultListCount : int.Parse(rest, NumberStyles.None, CultureInfo.InvariantCulture);
                IReadOnlyList<LocalNoteEntry> entries = Notes.List(n);
                string msg = entries.Count == 0
                    ? $"[NOTE] Chưa có ghi chú nào trong {LocalNoteService.RelativePath}."
                    : $"[NOTE] {entries.Count} ghi chú mới nhất: " + string.Join(" | ", entries.Select(DescribeEntry));
                return ("list", true, msg, new { entries, path = LocalNoteService.RelativePath });
            }

            if (first == "find" || first == "search")
            {
                if (rest.Length == 0) return ("find", false, $"[NOTE] Thiếu từ khóa. {NoteUsage}", null);
                IReadOnlyList<LocalNoteEntry> found = Notes.Find(rest, out int total);
                string msg = found.Count == 0
                    ? $"[NOTE] Không có ghi chú nào khớp \"{rest}\"."
                    : $"[NOTE] {total} ghi chú khớp \"{rest}\"" + (total > found.Count ? $" (hiện {found.Count} mới nhất)" : string.Empty) + ": " +
                      string.Join(" | ", found.Select(DescribeEntry));
                return ("find", true, msg, new { keyword = rest, totalMatches = total, entries = found });
            }

            LocalNoteWrite write = Notes.Append(text);
            Audit("local_note", new { action = "append", ok = write.Ok, id = write.Entry?.Id, chars = text.Length }, write.Ok ? "Appended" : "Rejected");
            if (!write.Ok || write.Entry == null) return ("append", false, $"[NOTE] {write.Error}", null);
            return ("append", true,
                $"[NOTE #{write.Entry.Id}] Đã lưu vào {LocalNoteService.RelativePath}: {LatestDispatchFile.Shorten(write.Entry.Text, 120)}",
                new { entry = write.Entry, path = LocalNoteService.RelativePath, writeMs = write.WriteMs });
        }

        private static string DescribeEntry(LocalNoteEntry e) =>
            (e.Id > 0 ? $"#{e.Id} " : string.Empty) + $"[{e.TimestampUtc}] {LatestDispatchFile.Shorten(e.Text, 100)}";

        private void Audit(string tool, object args, string outcome)
        {
            try
            {
                _store?.WriteAuditLog(tool, ActionRiskLevel.Green_SafeRead, JsonSerializer.Serialize(args), outcome);
            }
            catch (Exception)
            {
                // the audit row is evidence, not a precondition of the note
            }
        }
    }
}
