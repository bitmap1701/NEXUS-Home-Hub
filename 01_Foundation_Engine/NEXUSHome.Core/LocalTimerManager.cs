using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>One /timer. <see cref="State"/> = ACTIVE | EXPIRED | CANCELLED.</summary>
    public sealed record LocalTimerInfo(
        int Id,
        string Label,
        long DurationMs,
        string DurationText,
        string StartedUtc,
        string DueUtc,
        string State,
        long RemainingMs,
        string? EndedUtc = null);

    /// <summary>
    /// TASK-14 ADR-59 <c>/timer</c>: countdown timers inside the process, 0 tokens, 0 network.
    /// Each timer is one one-shot <see cref="System.Threading.Timer"/> (period = Infinite) that is disposed when it fires or
    /// is cancelled. With no active timer there is no timer object, thread or wake-up loop at all (0 % idle CPU, ADR-05).
    /// The expiry callback runs on a pool thread and raises <see cref="Expired"/>; the Cockpit shows it as a Passive HUD
    /// (WS_EX_NOACTIVATE) so the game keeps the foreground window.
    /// Timers live in the process that created them: HostService (Cockpit) timers survive until HostService exits; an MCP
    /// client's timers live as long as its McpTools process.
    /// </summary>
    public sealed class LocalTimerManager : IDisposable
    {
        public const int MaxActiveTimers = 20;
        public const long MaxDurationMs = 24L * 60 * 60 * 1000; // 24 h
        public const int MaxLabelChars = 80;
        private const int RecentExpiredKept = 10;
        public const string DefaultLabel = "Timer";

        private readonly object _gate = new();
        private readonly Dictionary<int, Entry> _active = new();
        private readonly LinkedList<LocalTimerInfo> _recentEnded = new();
        private int _nextId;
        private bool _disposed;

        /// <summary>Raised on a thread-pool thread when a timer reaches 0 (after it has moved to EXPIRED).</summary>
        public event Action<LocalTimerInfo>? Expired;

        private sealed class Entry
        {
            public Entry(int id, string label, long durationMs, DateTime startedUtc)
            {
                Id = id;
                Label = label;
                DurationMs = durationMs;
                StartedUtc = startedUtc;
                DueUtc = startedUtc.AddMilliseconds(durationMs);
            }

            public int Id { get; }
            public string Label { get; }
            public long DurationMs { get; }
            public DateTime StartedUtc { get; }
            public DateTime DueUtc { get; }
            public Timer? Timer { get; set; }
        }

        /// <summary>Timer objects alive right now (selftest: must be 0 when nothing runs).</summary>
        public int ActiveCount
        {
            get { lock (_gate) return _active.Count; }
        }

        /// <summary>
        /// "250ms", "90s", "5m", "1h30m", "2m30s", "1h2m3s400ms", or a bare integer = seconds ("60").
        /// Units: ms, s, m, h (case-insensitive). 1 ms .. 24 h.
        /// </summary>
        public static bool TryParseDuration(string? text, out long milliseconds, out string? error)
        {
            milliseconds = 0;
            error = null;
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0)
            {
                error = "Thiếu thời lượng.";
                return false;
            }

            if (s.All(char.IsDigit))
            {
                if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) || seconds > MaxDurationMs / 1000)
                {
                    error = "Thời lượng vượt 24 giờ.";
                    return false;
                }
                milliseconds = seconds * 1000;
                return CheckRange(milliseconds, out error);
            }

            int pos = 0;
            long total = 0;
            int lastRank = int.MaxValue; // h=4 > m=3 > s=2 > ms=1: units must go from large to small, each at most once
            while (pos < s.Length)
            {
                int start = pos;
                while (pos < s.Length && char.IsDigit(s[pos])) pos++;
                if (pos == start || pos - start > 9)
                {
                    error = $"Thời lượng không hợp lệ '{text}'. Ví dụ: 250ms, 90s, 5m, 1h30m, 2m30s, 60.";
                    return false;
                }
                long amount = long.Parse(s.AsSpan(start, pos - start), NumberStyles.None, CultureInfo.InvariantCulture);

                long unitMs;
                int rank;
                if (pos + 1 < s.Length && s[pos] == 'm' && s[pos + 1] == 's') { unitMs = 1; rank = 1; pos += 2; }
                else if (pos < s.Length && s[pos] == 'h') { unitMs = 3_600_000; rank = 4; pos++; }
                else if (pos < s.Length && s[pos] == 'm') { unitMs = 60_000; rank = 3; pos++; }
                else if (pos < s.Length && s[pos] == 's') { unitMs = 1_000; rank = 2; pos++; }
                else
                {
                    error = $"Thiếu đơn vị sau '{amount}' (ms, s, m, h).";
                    return false;
                }

                if (rank >= lastRank)
                {
                    error = "Đơn vị phải theo thứ tự h → m → s → ms, mỗi đơn vị một lần (ví dụ 1h30m).";
                    return false;
                }
                lastRank = rank;

                total += amount * unitMs;
                if (total > MaxDurationMs)
                {
                    error = "Thời lượng vượt 24 giờ.";
                    return false;
                }
            }

            milliseconds = total;
            return CheckRange(milliseconds, out error);
        }

        private static bool CheckRange(long ms, out string? error)
        {
            error = null;
            if (ms < 1)
            {
                error = "Thời lượng phải lớn hơn 0.";
                return false;
            }
            if (ms > MaxDurationMs)
            {
                error = "Thời lượng vượt 24 giờ.";
                return false;
            }
            return true;
        }

        /// <summary>Canonical text: 250ms, 60s, 1m30s, 1h30m, 2m30s500ms.</summary>
        public static string FormatDuration(long ms)
        {
            if (ms <= 0) return "0s";
            var sb = new StringBuilder();
            long h = ms / 3_600_000; ms %= 3_600_000;
            long m = ms / 60_000; ms %= 60_000;
            long s = ms / 1_000; ms %= 1_000;
            if (h > 0) sb.Append(h).Append('h');
            if (m > 0) sb.Append(m).Append('m');
            if (s > 0) sb.Append(s).Append('s');
            if (ms > 0) sb.Append(ms).Append("ms");
            return sb.ToString();
        }

        /// <summary>Starts a timer. Returns null and an error when the limit is reached or the manager is disposed.</summary>
        public LocalTimerInfo? Start(long durationMs, string? label, out string? error)
        {
            error = null;
            string cleanLabel = CleanLabel(label);
            if (!CheckRange(durationMs, out error)) return null;

            Entry entry;
            lock (_gate)
            {
                if (_disposed)
                {
                    error = "Bộ đếm đã dừng (tiến trình đang tắt).";
                    return null;
                }
                if (_active.Count >= MaxActiveTimers)
                {
                    error = $"Đã có {MaxActiveTimers} bộ đếm đang chạy. Dùng /timer cancel <id|all>.";
                    return null;
                }
                entry = new Entry(++_nextId, cleanLabel, durationMs, DateTime.UtcNow);
                _active[entry.Id] = entry;
                // One-shot: due = duration, period = Infinite. Created inside the lock so a very short timer cannot fire
                // before its Entry holds the Timer reference.
                entry.Timer = new Timer(OnFire, entry.Id, durationMs, Timeout.Infinite);
            }
            return Describe(entry, "ACTIVE", DateTime.UtcNow);
        }

        public static string CleanLabel(string? label)
        {
            string text = (label ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) return DefaultLabel;
            return text.Length > MaxLabelChars ? text.Substring(0, MaxLabelChars) + "…" : text;
        }

        private void OnFire(object? state)
        {
            int id = (int)state!;
            LocalTimerInfo info;
            lock (_gate)
            {
                if (!_active.Remove(id, out Entry? entry)) return; // cancelled meanwhile
                entry.Timer?.Dispose();
                entry.Timer = null;
                DateTime now = DateTime.UtcNow;
                info = Describe(entry, "EXPIRED", now) with { RemainingMs = 0, EndedUtc = now.ToString("o") };
                Remember(info);
            }

            try
            {
                Expired?.Invoke(info);
            }
            catch (Exception)
            {
                // A subscriber failure must never crash the pool thread (and with it the process).
            }
        }

        /// <param name="target">"all" (or empty) cancels every active timer; otherwise an id, with or without '#'.</param>
        public IReadOnlyList<LocalTimerInfo> Cancel(string? target, out string? error)
        {
            error = null;
            string t = (target ?? string.Empty).Trim().TrimStart('#');
            var cancelled = new List<LocalTimerInfo>();
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                if (t.Length == 0 || t.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Entry e in _active.Values.OrderBy(e => e.Id).ToList())
                    {
                        cancelled.Add(CancelLocked(e, now));
                    }
                    return cancelled;
                }

                if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                {
                    error = "Dùng: /timer cancel <id|all>";
                    return cancelled;
                }
                if (!_active.TryGetValue(id, out Entry? entry))
                {
                    error = $"Không có bộ đếm #{id} đang chạy.";
                    return cancelled;
                }
                cancelled.Add(CancelLocked(entry, now));
                return cancelled;
            }
        }

        private LocalTimerInfo CancelLocked(Entry entry, DateTime now)
        {
            _active.Remove(entry.Id);
            entry.Timer?.Dispose();
            entry.Timer = null;
            LocalTimerInfo info = Describe(entry, "CANCELLED", now) with { EndedUtc = now.ToString("o") };
            Remember(info);
            return info;
        }

        /// <summary>Active timers (soonest first) and the last ended ones (newest first).</summary>
        public (IReadOnlyList<LocalTimerInfo> Active, IReadOnlyList<LocalTimerInfo> RecentEnded) List()
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                var active = _active.Values.OrderBy(e => e.DueUtc).ThenBy(e => e.Id).Select(e => Describe(e, "ACTIVE", now)).ToList();
                return (active, _recentEnded.ToList());
            }
        }

        private void Remember(LocalTimerInfo info)
        {
            _recentEnded.AddFirst(info);
            while (_recentEnded.Count > RecentExpiredKept) _recentEnded.RemoveLast();
        }

        private static LocalTimerInfo Describe(Entry e, string state, DateTime now)
        {
            long remaining = Math.Max(0, (long)Math.Ceiling((e.DueUtc - now).TotalMilliseconds));
            return new LocalTimerInfo(e.Id, e.Label, e.DurationMs, FormatDuration(e.DurationMs),
                e.StartedUtc.ToString("o"), e.DueUtc.ToString("o"), state, remaining);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (Entry e in _active.Values)
                {
                    e.Timer?.Dispose();
                    e.Timer = null;
                }
                _active.Clear();
            }
        }
    }
}
