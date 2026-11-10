using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-15 (ADR-62): what /ctx and MCP get_active_window_context found, and which shield (if any) stopped it.</summary>
    public sealed record CtxResult(
        string Status,                 // OK | BLOCKED_ANTICHEAT | BLOCKED_SENSITIVE | NO_WINDOW | UNSUPPORTED
        string Message,
        string? ProcessName,           // null when blocked as sensitive (nothing about that window is kept)
        string? WindowTitle,           // <= 200 chars, only when Status = OK
        string? ClipText,              // <= 1 000 chars, only when Status = OK and the clipboard text was new
        string? ContextBlock,          // "[ACTIVE_WINDOW_CONTEXT: app=… | title=… | text=…]" <= 1 200 chars
        string? Fingerprint,           // CTX_V1 SHA-256 of app + title + text (GeminiResponseCache grounding fingerprint)
        string? MatchedRule,           // anti-cheat name / sensitive category (never the title itself)
        string? Shield,                // "anticheat-foreground" | "anticheat-running" | "process-unreadable" | "denylist"
        bool Simulated,
        bool ScanCached,
        double ScanMs,
        double GuardMs,                // from the start of the check to the decision
        double TotalMs,
        string AtUtc);

    /// <summary>
    /// TASK-15 (ADR-62): Active Window Context with two hard shields, run ONLY when /ctx or the MCP tool asks (never on wake):
    ///   1. Anti-Cheat Safe Guard: the target window's process (GetWindowThreadProcessId + OpenProcess(PROCESS_QUERY_LIMITED_
    ///      INFORMATION) + QueryFullProcessImageNameW) and every running process (one Toolhelp32 snapshot of names, reused for
    ///      2 s, no timer) against the anti-cheat / protected-game list. A match blocks before the title or the clipboard is
    ///      read. A process that cannot be identified is blocked too (fail closed). No PROCESS_VM_READ, no UI Automation, no
    ///      hooks, no WM_GETTEXT to another process (GetWindowTextW reads the caption the system already keeps).
    ///   2. Sensitive Window Denylist: process name and window title against password managers, private browsing, keys,
    ///      banking / OTP words. A match blocks and drops the title at once: it is never returned, logged, written to SQLite
    ///      or to latest_dispatch.json.
    /// Blocked calls cost 0 tokens.
    /// </summary>
    public static class ActiveWindowContext
    {
        public const string StatusOk = "OK";
        public const string StatusAntiCheat = "BLOCKED_ANTICHEAT";
        public const string StatusSensitive = "BLOCKED_SENSITIVE";
        public const string StatusNoWindow = "NO_WINDOW";
        public const string StatusUnsupported = "UNSUPPORTED";
        public const string AntiCheatMessage = "[CTX_BLOCKED: Anti-Cheat Safe Guard]";
        public const string SensitiveMessage = "[CTX_BLOCKED: Sensitive Window Denylist]";
        public const int MaxTitleChars = 200;
        public const int MaxClipChars = 1000;
        public const int MaxBlockChars = 1200;
        public const int RunningScanTtlMs = 2000;

        /// <summary>Anti-cheat services and strictly protected games (process names, case-insensitive).</summary>
        public static readonly string[] AntiCheatProcesses =
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BattlEye", "vgc", "vgk", "FACEIT", "faceitclient", "Ricochet",
            "GameGuard", "xhunter1",
            "valorant", "Valorant-Win64-Shipping", "cs2", "r5apex", "LeagueClient", "League of Legends", "GenshinImpact",
            "StarRail", "PUBG-Win64-Shipping", "FortniteClient-Win64-Shipping"
        };

        /// <summary>Denylist (process name or window title contains, case-insensitive) with the category reported instead of the text.</summary>
        public static readonly (string Keyword, string Category)[] SensitiveKeywords =
        {
            ("keepass", "password-manager"), ("bitwarden", "password-manager"), ("1password", "password-manager"),
            ("lastpass", "password-manager"), ("dashlane", "password-manager"), ("authy", "authenticator"),
            ("credential manager", "credentials"), ("windows security", "credentials"),
            ("inprivate", "private-browsing"), ("incognito", "private-browsing"), ("private browsing", "private-browsing"),
            ("tor browser", "private-browsing"),
            (".env", "secret-file"), ("id_rsa", "secret-file"), ("id_ed25519", "secret-file"), ("dpapi", "secret-file"),
            ("secret", "secret"), ("password", "secret"), ("mật khẩu", "secret"),
            ("banking", "finance"), ("ngân hàng", "finance"), ("otp", "one-time-code"), ("2fa", "one-time-code"),
            ("seed phrase", "crypto-wallet"), ("metamask", "crypto-wallet")
        };

        /// <summary>Test hook (selftest, pipe CTXSIM): "process|title" replaces the real window; null = real window.</summary>
        public static string? SimulatedTarget { get; set; }

        /// <summary>Test hook: process names reported as running (replaces the Toolhelp32 scan); null = real scan.</summary>
        public static IReadOnlyList<string>? SimulatedRunning { get; set; }

        private static readonly object ScanGate = new();
        private static IReadOnlyList<string> _scanMatches = Array.Empty<string>();
        private static long _scanAtTicks;

        public static bool IsAntiCheatName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            string name = processName.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            foreach (string entry in AntiCheatProcesses)
            {
                if (name.Equals(entry, StringComparison.OrdinalIgnoreCase)) return true;
                // longer names also cover their variants (EasyAntiCheat_EOS, BEService_x64 ...); short ones (vgc, cs2) are exact only
                if (entry.Length >= 6 && name.StartsWith(entry, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The denylist category matched by the process name or the title, or null.</summary>
        public static string? MatchSensitive(string? processName, string? title)
        {
            foreach ((string keyword, string category) in SensitiveKeywords)
            {
                if (!string.IsNullOrEmpty(processName) && processName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return category;
                if (!string.IsNullOrEmpty(title) && ContainsWord(title, keyword)) return category;
            }
            return null;
        }

        /// <summary>
        /// Contains, but a short word such as "otp" / "2fa" must stand alone (not inside "Photoshop" / "laptop"); keywords with a
        /// dot, a space or 5+ letters match anywhere.
        /// </summary>
        private static bool ContainsWord(string text, string keyword)
        {
            int at = 0;
            while ((at = text.IndexOf(keyword, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (keyword.Length >= 5 || keyword.Contains('.') || keyword.Contains(' ')) return true;
                bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                int end = at + keyword.Length;
                bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (startOk && endOk) return true;
                at = end;
            }
            return false;
        }

        /// <summary>
        /// Shields + context for <paramref name="hwnd"/> (the window the user came from; zero = the current foreground window).
        /// <paramref name="readClipboard"/> is called only when both shields passed.
        /// </summary>
        public static CtxResult Inspect(IntPtr hwnd, Func<string?>? readClipboard = null)
        {
            long t0 = Stopwatch.GetTimestamp();
            string at = DateTime.UtcNow.ToString("o");
            string? simulated = SimulatedTarget;
            string? process;
            Func<string?> readTitle;
            if (simulated != null)
            {
                int bar = simulated.IndexOf('|');
                process = bar < 0 ? simulated : simulated.Substring(0, bar);
                string simTitle = bar < 0 ? string.Empty : simulated.Substring(bar + 1);
                readTitle = () => simTitle;
            }
            else if (!OperatingSystem.IsWindows())
            {
                return new CtxResult(StatusUnsupported, "[CTX] /ctx cần Windows.", null, null, null, null, null, null, null, false, false, 0, Ms(t0), Ms(t0), at);
            }
            else
            {
                if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                    return new CtxResult(StatusNoWindow, "[CTX] Chưa có cửa sổ nào để đọc (mở cửa sổ cần hỏi rồi bấm Ctrl+Shift+Space).",
                        null, null, null, null, null, null, null, false, false, 0, Ms(t0), Ms(t0), at);
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId)
                    return new CtxResult(StatusNoWindow, "[CTX] Cửa sổ đang chọn là chính NEXUS Cockpit.", null, null, null, null, null, null, null,
                        false, false, 0, Ms(t0), Ms(t0), at);
                process = ProcessNameOf(pid);
                IntPtr target = hwnd;
                readTitle = () => ReadTitle(target);
                if (process == null)
                {
                    // fail closed: a process NEXUS cannot even name is not read
                    return Blocked(StatusAntiCheat, AntiCheatMessage, null, "process-unreadable", "process-unreadable", false, false, 0, t0, at);
                }
            }

            // Shield 1a: the target process itself
            if (IsAntiCheatName(process))
                return Blocked(StatusAntiCheat, AntiCheatMessage, process, process, "anticheat-foreground", simulated != null, false, 0, t0, at);

            // Shield 1b: anything on the anti-cheat list running at all (names only, snapshot reused for 2 s)
            (IReadOnlyList<string> running, bool cached, double scanMs) = RunningAntiCheat();
            if (running.Count > 0)
                return Blocked(StatusAntiCheat, AntiCheatMessage, process, running[0], "anticheat-running", simulated != null, cached, scanMs, t0, at);

            // Shield 2: denylist on the process name and the title; the title is dropped on a match
            string title = readTitle() ?? string.Empty;
            string? category = MatchSensitive(process, title);
            if (category != null)
            {
                return Blocked(StatusSensitive, SensitiveMessage, null, category, "denylist", simulated != null, cached, scanMs, t0, at);
            }
            double guardMs = Ms(t0);

            string shortTitle = title.Length > MaxTitleChars ? title.Substring(0, MaxTitleChars) : title;
            string? clip = readClipboard?.Invoke();
            if (clip != null)
            {
                clip = clip.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (clip.Length == 0 || LocalNoteService.ContainsSecret(clip) || MatchSensitive(null, clip) != null) clip = null; // never forward a key / password text
                else if (clip.Length > MaxClipChars) clip = clip.Substring(0, MaxClipChars);
            }
            string block = BuildBlock(process!, shortTitle, clip);
            string fingerprint = Fingerprint(process!, shortTitle, clip);
            return new CtxResult(StatusOk, $"[CTX] {process} — {shortTitle}{(clip != null ? $" (+{clip.Length} ký tự clipboard)" : string.Empty)}",
                process, shortTitle, clip, block, fingerprint, null, null, simulated != null, cached, scanMs, guardMs, Ms(t0), at);
        }

        /// <summary>"[ACTIVE_WINDOW_CONTEXT: app=… | title=… | text=…]", the clipboard part trimmed first so the block stays &lt;= 1 200 chars.</summary>
        public static string BuildBlock(string process, string title, string? clip)
        {
            string head = $"[ACTIVE_WINDOW_CONTEXT: app={process} | title={title}";
            if (clip == null) return Cap(head + "]");
            int room = MaxBlockChars - head.Length - " | text=".Length - 1;
            if (room <= 0) return Cap(head + "]");
            string text = clip.Length > room ? clip.Substring(0, room) : clip;
            return Cap($"{head} | text={text}]");

            static string Cap(string s) => s.Length <= MaxBlockChars ? s : s.Substring(0, MaxBlockChars - 1) + "]";
        }

        public static string Fingerprint(string process, string title, string? clip)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{process}\n{title}\n{clip}"));
            return "CTX_V1:" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }

        /// <summary>The prompt Gemini gets: the context block, then the user's question.</summary>
        public static string BuildPrompt(string question, string block) =>
            $"{block}\n\nNgữ cảnh trên là cửa sổ người dùng đang làm việc (tiêu đề, và đoạn chữ vừa copy nếu có). " +
            $"Chỉ dùng nó khi liên quan. Câu hỏi: {question}";

        private static CtxResult Blocked(string status, string message, string? process, string? rule, string shield, bool simulated,
            bool cached, double scanMs, long t0, string at)
        {
            double ms = Ms(t0);
            return new CtxResult(status, rule != null ? $"{message} ({rule})" : message,
                status == StatusSensitive ? null : process, null, null, null, null, rule, shield, simulated, cached, scanMs, ms, ms, at);
        }

        /// <summary>Running anti-cheat / protected-game processes (Toolhelp32 names only, reused for <see cref="RunningScanTtlMs"/>).</summary>
        public static (IReadOnlyList<string> Matches, bool Cached, double ScanMs) RunningAntiCheat()
        {
            IReadOnlyList<string>? simulated = SimulatedRunning;
            if (simulated != null)
            {
                var hits = new List<string>();
                foreach (string n in simulated) if (IsAntiCheatName(n)) hits.Add(n);
                return (hits, false, 0);
            }
            if (!OperatingSystem.IsWindows()) return (Array.Empty<string>(), false, 0);
            lock (ScanGate)
            {
                long now = Stopwatch.GetTimestamp();
                if (_scanAtTicks != 0 && (now - _scanAtTicks) * 1000.0 / Stopwatch.Frequency < RunningScanTtlMs)
                    return (_scanMatches, true, 0);
                var hits = new List<string>();
                foreach (string name in ProcessNames())
                    if (IsAntiCheatName(name) && !hits.Contains(name, StringComparer.OrdinalIgnoreCase)) hits.Add(name);
                _scanMatches = hits;
                _scanAtTicks = Stopwatch.GetTimestamp();
                return (hits, false, Math.Round((_scanAtTicks - now) * 1000.0 / Stopwatch.Frequency, 3));
            }
        }

        /// <summary>Forgets the cached running-process scan (tests; /ctx --rescan).</summary>
        public static void InvalidateScan()
        {
            lock (ScanGate) _scanAtTicks = 0;
        }

        private static List<string> ProcessNames()
        {
            var names = new List<string>(256);
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return names;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (!Process32FirstW(snap, ref entry)) return names;
                do
                {
                    string exe = entry.szExeFile ?? string.Empty;
                    names.Add(exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe);
                }
                while (Process32NextW(snap, ref entry));
            }
            finally
            {
                CloseHandle(snap);
            }
            return names;
        }

        /// <summary>File name without extension of the process image, or null when it cannot be read (OpenProcess with query-limited rights only).</summary>
        public static string? ProcessNameOf(uint pid)
        {
            if (!OperatingSystem.IsWindows() || pid == 0) return null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return null;
                return Path.GetFileNameWithoutExtension(sb.ToString(0, size));
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static string ReadTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLengthW(hwnd);
            if (length <= 0) return string.Empty;
            var sb = new StringBuilder(Math.Min(length, 1024) + 1);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static double Ms(long t0) => Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 4);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    }
}
