using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-13: result of /copy, Alt+C, pipe COPYWIDGET and MCP copy.</summary>
    public sealed record ClipboardCopyResult(
        bool Ok,
        string Text,
        bool SystemClipboard,
        string? SystemClipboardError,
        double ElapsedMs,
        bool HistoryExcluded = false); // TASK-12 §3.1: Win+V history / cloud clipboard opt-out formats written too

    /// <summary>TASK-12 §3.1: which clipboard-history opt-out formats the Windows clipboard holds right now.</summary>
    public sealed record ClipboardHistoryFlags(
        bool ExcludeFromMonitorProcessing,
        bool CanIncludeInClipboardHistoryFormat,
        int? CanIncludeInClipboardHistoryValue,
        bool CanUploadToCloudClipboardFormat,
        int? CanUploadToCloudClipboardValue)
    {
        /// <summary>True when Win+V history and Cloud Clipboard both skip the current content.</summary>
        public bool HistoryExcluded =>
            ExcludeFromMonitorProcessing || (CanIncludeInClipboardHistoryFormat && CanIncludeInClipboardHistoryValue == 0);
    }

    /// <summary>
    /// TASK-13: copy to the Windows clipboard with plain Win32 (OpenClipboard / EmptyClipboard / SetClipboardData
    /// CF_UNICODETEXT / CloseClipboard): one attempt, no OLE retry loop (WPF Clipboard.SetText retries up to ~1 s when
    /// another program holds the clipboard). The owner is the Cockpit window (<see cref="OwnerWindow"/>); without a window
    /// (selftest, McpTools) only <see cref="LastCopiedText"/> is set, which is also what tests compare against.
    /// </summary>
    public static class ClipboardService
    {
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;
        private static readonly object Gate = new();
        private static string _lastCopiedText = string.Empty;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr memory);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
        private static extern uint RegisterClipboardFormat(string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr GlobalSize(IntPtr memory);

        // TASK-12 §3.1: registered formats that keep a copy out of Win+V history and Cloud Clipboard.
        public const string ExcludeFormatName = "ExcludeClipboardContentFromMonitorProcessing";
        public const string CanIncludeInHistoryFormatName = "CanIncludeInClipboardHistory";
        public const string CanUploadToCloudFormatName = "CanUploadToCloudClipboard";
        private const uint CfBitmap = 2;
        private const uint CfDib = 8;
        private const uint CfDibV5 = 17;

        /// <summary>Cockpit window handle (set once the HWND exists). Zero = no system clipboard in this process.</summary>
        public static IntPtr OwnerWindow { get; set; }

        public static string LastCopiedText
        {
            get { lock (Gate) return _lastCopiedText; }
        }

        public static ClipboardCopyResult Copy(string text)
        {
            long ts0 = Stopwatch.GetTimestamp();
            lock (Gate) _lastCopiedText = text;
            bool system = false;
            bool historyExcluded = false;
            string? error = null;
            IntPtr owner = OwnerWindow;
            if (owner == IntPtr.Zero || !OperatingSystem.IsWindows())
            {
                error = "no Cockpit window in this process (LastCopiedText only)";
            }
            else
            {
                system = TrySetText(owner, text, out error, out historyExcluded);
            }
            double ms = Math.Round((Stopwatch.GetTimestamp() - ts0) * 1000.0 / Stopwatch.Frequency, 3);
            return new ClipboardCopyResult(true, text, system, error, ms, historyExcluded);
        }

        /// <summary>TASK-12 §3.1 evidence: the history opt-out formats on the clipboard now (null when it cannot be opened).</summary>
        public static ClipboardHistoryFlags? TryReadHistoryFlags()
        {
            if (!OperatingSystem.IsWindows()) return null;
            uint exclude = RegisterClipboardFormat(ExcludeFormatName);
            uint include = RegisterClipboardFormat(CanIncludeInHistoryFormatName);
            uint cloud = RegisterClipboardFormat(CanUploadToCloudFormatName);
            if (!OpenClipboard(OwnerWindow)) return null;
            try
            {
                bool hasExclude = exclude != 0 && IsClipboardFormatAvailable(exclude);
                bool hasInclude = include != 0 && IsClipboardFormatAvailable(include);
                bool hasCloud = cloud != 0 && IsClipboardFormatAvailable(cloud);
                return new ClipboardHistoryFlags(hasExclude, hasInclude, hasInclude ? ReadDword(include) : null,
                    hasCloud, hasCloud ? ReadDword(cloud) : null);
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>TASK-12: changes every time any program puts new content on the clipboard (the /img source memo key).</summary>
        public static uint SequenceNumber => OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;

        /// <summary>TASK-12: true when the clipboard offers an image (PNG from Snipping Tool / Win+Shift+S, or a bitmap / DIB).</summary>
        public static bool ContainsImage()
        {
            if (!OperatingSystem.IsWindows()) return false;
            uint png = RegisterClipboardFormat("PNG");
            return (png != 0 && IsClipboardFormatAvailable(png)) || IsClipboardFormatAvailable(CfDib) ||
                   IsClipboardFormatAvailable(CfDibV5) || IsClipboardFormatAvailable(CfBitmap);
        }

        /// <summary>
        /// TASK-12 /img: the clipboard image as encoded file bytes, read once with plain Win32 (no OLE, works from any thread):
        /// the "PNG" format when present (Win+Shift+S), otherwise CF_DIB wrapped in a 14-byte BITMAPFILEHEADER so it decodes as
        /// a .bmp. Returns null with <paramref name="format"/> = the reason when there is no usable image.
        /// </summary>
        public static byte[]? TryReadImageBytes(out string format)
        {
            format = "none";
            if (!OperatingSystem.IsWindows()) { format = "not Windows"; return null; }
            uint png = RegisterClipboardFormat("PNG");
            if (!OpenClipboard(OwnerWindow)) { format = $"clipboard busy (OpenClipboard error {Marshal.GetLastWin32Error()})"; return null; }
            try
            {
                if (png != 0 && IsClipboardFormatAvailable(png))
                {
                    byte[]? bytes = CopyGlobal(GetClipboardData(png));
                    if (bytes is { Length: > 8 })
                    {
                        format = "PNG";
                        return bytes;
                    }
                }
                if (IsClipboardFormatAvailable(CfDib)) // also synthesized by Windows from CF_BITMAP / CF_DIBV5
                {
                    byte[]? dib = CopyGlobal(GetClipboardData(CfDib));
                    byte[]? bmp = dib == null ? null : WrapDibAsBmp(dib);
                    if (bmp != null)
                    {
                        format = "CF_DIB";
                        return bmp;
                    }
                }
                format = "no PNG / CF_DIB image on the clipboard";
                return null;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static byte[]? CopyGlobal(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            long size = (long)GlobalSize(handle).ToUInt64();
            if (size <= 0 || size > 256L * 1024 * 1024) return null;
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        /// <summary>BITMAPFILEHEADER + the CF_DIB block (header, BI_BITFIELDS masks, colour table, pixels) = a valid .bmp file.</summary>
        internal static byte[]? WrapDibAsBmp(byte[] dib)
        {
            if (dib.Length < 40) return null;
            int headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40 || headerSize > dib.Length) return null;
            short bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int colorsUsed = BitConverter.ToInt32(dib, 32);
            int masks = headerSize == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0; // BI_BITFIELDS / BI_ALPHABITFIELDS
            int colors = colorsUsed > 0 ? colorsUsed : bitCount is > 0 and <= 8 ? 1 << bitCount : 0;
            int offBits = 14 + headerSize + masks + colors * 4;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(offBits).CopyTo(bmp, 10);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
            return bmp;
        }

        private static int? ReadDword(uint format)
        {
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return null;
            try { return (long)GlobalSize(handle).ToUInt64() >= 4 ? Marshal.ReadInt32(data) : null; }
            finally { GlobalUnlock(handle); }
        }

        /// <summary>One more clipboard format holding a 4-byte value (owned by the system once set). False if it was refused.</summary>
        private static bool SetDwordFormat(string name, int value)
        {
            uint format = RegisterClipboardFormat(name);
            if (format == 0) return false;
            IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)4);
            if (memory == IntPtr.Zero) return false;
            IntPtr target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                GlobalFree(memory);
                return false;
            }
            try { Marshal.WriteInt32(target, value); }
            finally { GlobalUnlock(memory); }
            if (SetClipboardData(format, memory) == IntPtr.Zero)
            {
                GlobalFree(memory);
                return false;
            }
            return true;
        }

        /// <summary>What the Windows clipboard holds as text now (Gate 13 E2 compares it with the copied code), or null.</summary>
        public static string? TryReadSystemText()
        {
            if (!OperatingSystem.IsWindows() || !OpenClipboard(OwnerWindow)) return null;
            try
            {
                IntPtr handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero) return null;
                IntPtr data = GlobalLock(handle);
                if (data == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(data); }
                finally { GlobalUnlock(handle); }
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static bool TrySetText(IntPtr owner, string text, out string? error, out bool historyExcluded)
        {
            error = null;
            historyExcluded = false;
            if (!OpenClipboard(owner))
            {
                error = $"clipboard busy (OpenClipboard error {Marshal.GetLastWin32Error()}); text kept in LastCopiedText";
                return false;
            }
            try
            {
                if (!EmptyClipboard())
                {
                    error = $"EmptyClipboard error {Marshal.GetLastWin32Error()}";
                    return false;
                }
                int bytes = (text.Length + 1) * 2;
                IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                if (memory == IntPtr.Zero)
                {
                    error = "GlobalAlloc failed";
                    return false;
                }
                IntPtr target = GlobalLock(memory);
                if (target == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    error = "GlobalLock failed";
                    return false;
                }
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(memory);
                }
                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory); // still ours when SetClipboardData fails
                    error = $"SetClipboardData error {Marshal.GetLastWin32Error()}";
                    return false;
                }
                // TASK-12 §3.1: same open/close session, so no history listener ever sees the text without the opt-out.
                bool excluded = SetDwordFormat(ExcludeFormatName, 0);
                bool noHistory = SetDwordFormat(CanIncludeInHistoryFormatName, 0);
                SetDwordFormat(CanUploadToCloudFormatName, 0);
                historyExcluded = excluded || noHistory;
                return true; // the system owns the memory now
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    /// <summary>TASK-13: one card pinned to the Right MFD (mfd_pins.json). <see cref="PinId"/> = position 1..max.</summary>
    public sealed record MfdPin(
        [property: JsonPropertyName("pinId")] int PinId,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("pinnedAtUtc")] string PinnedAtUtc,
        [property: JsonPropertyName("source")] string Source);

    public sealed record MfdPinResult(bool Ok, bool Blocked, string Message, IReadOnlyList<MfdPin> Pins, MfdPin? Pinned, int Removed, bool Evicted);

    /// <summary>
    /// TASK-13: the Right MFD pin board, 03_Local_Data_Vault/config/mfd_pins.json (UTF-8 without BOM, git-ignored with the
    /// config folder, ADR-24). At most maxPinnedCards (default 5): a new pin beyond the cap drops the oldest one (FIFO);
    /// pinning the same content again refreshes it instead of adding a copy. Writes are atomic (ADR-14) under a
    /// machine-wide mutex (HostService and McpTools); the Secret Guard of /note refuses key-shaped text before any write.
    /// </summary>
    public sealed class MfdPinManager
    {
        public const string FileName = "mfd_pins.json";
        public const int MaxContentChars = 4000;
        private const string MutexName = @"Local\NEXUS_Home_Hub_MfdPins";
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private readonly Func<int> _maxPins;
        private readonly object _gate = new();
        private long _cachedStamp = -1;
        private List<MfdPin> _cached = new();

        public MfdPinManager(string configDirectory, Func<int> maxPins)
        {
            PinsPath = Path.Combine(configDirectory, FileName);
            _maxPins = maxPins;
        }

        public string PinsPath { get; }

        public int MaxPins => Math.Clamp(_maxPins(), 1, 5);

        /// <summary>Current pins (cached; re-read only when the file's write stamp changes, e.g. after McpTools pinned).</summary>
        public IReadOnlyList<MfdPin> List()
        {
            lock (_gate)
            {
                long stamp = Stamp();
                if (stamp != _cachedStamp)
                {
                    _cached = Read();
                    _cachedStamp = stamp;
                }
                return _cached.ToList();
            }
        }

        public int Count => List().Count;

        public MfdPinResult Pin(string type, string title, string? language, string content, string source)
        {
            if (LocalNoteService.ContainsSecret(content) || LocalNoteService.ContainsSecret(title))
                return new MfdPinResult(false, true, "[PIN BLOCKED] Secret Guard: nội dung có chuỗi giống khóa bí mật → không ghim, không ghi mfd_pins.json.", List(), null, 0, false);
            string clean = content.Replace("\r\n", "\n").Trim();
            if (clean.Length == 0) return new MfdPinResult(false, false, "[PIN] Không có nội dung để ghim.", List(), null, 0, false);
            if (clean.Length > MaxContentChars) clean = clean.Substring(0, MaxContentChars);

            bool evicted = false;
            string? error = Mutate(pins =>
            {
                int same = pins.FindIndex(p => p.Content == clean);
                if (same >= 0) pins.RemoveAt(same); // same content again: move it to the newest slot
                while (pins.Count >= MaxPins)
                {
                    pins.RemoveAt(0); // FIFO: drop the oldest
                    evicted = true;
                }
                pins.Add(new MfdPin(0, type, OneLine(title, 60), language, clean, DateTime.UtcNow.ToString("o"), source));
            });
            IReadOnlyList<MfdPin> now = List();
            if (error != null) return new MfdPinResult(false, false, $"[PIN] Không ghi được {FileName}: {error}", now, null, 0, false);
            MfdPin? stored = now.LastOrDefault();
            return new MfdPinResult(true, false,
                $"[PIN #{stored?.PinId}] {stored?.Title} → Right MFD ({now.Count}/{MaxPins})" + (evicted ? " · thẻ cũ nhất đã gỡ (FIFO)" : string.Empty),
                now, stored, 0, evicted);
        }

        /// <param name="target">"all" / "clear" / empty = every pin; otherwise a pinId (with or without '#').</param>
        public MfdPinResult Unpin(string? target)
        {
            string t = (target ?? string.Empty).Trim().TrimStart('#');
            int removed = 0;
            string? notFound = null;
            string? error = Mutate(pins =>
            {
                if (t.Length == 0 || t.Equals("all", StringComparison.OrdinalIgnoreCase) || t.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    removed = pins.Count;
                    pins.Clear();
                    return;
                }
                if (!int.TryParse(t, out int id) || id < 1 || id > pins.Count)
                {
                    notFound = $"[PIN] Không có thẻ ghim #{t}. Dùng /pins để xem, /unpin <id|all>.";
                    return;
                }
                pins.RemoveAt(id - 1);
                removed = 1;
            });
            IReadOnlyList<MfdPin> now = List();
            if (error != null) return new MfdPinResult(false, false, $"[PIN] Không ghi được {FileName}: {error}", now, null, 0, false);
            if (notFound != null) return new MfdPinResult(false, false, notFound, now, null, 0, false);
            return new MfdPinResult(true, false, $"[PIN] Đã gỡ {removed} thẻ. Còn {now.Count}/{MaxPins} trên Right MFD.", now, null, removed, false);
        }

        public static string Describe(IReadOnlyList<MfdPin> pins, int max) =>
            pins.Count == 0
                ? $"[PINS] Right MFD trống (0/{max}). Dùng /pin, /pin #<n>, /pin <ghi chú> hoặc Alt+P."
                : $"[PINS] {pins.Count}/{max}: " + string.Join(" | ", pins.Select(p => $"#{p.PinId} {p.Type}: {p.Title}"));

        /// <summary>Read-modify-write under the process lock and the machine-wide mutex; pinIds are renumbered 1..N.</summary>
        private string? Mutate(Action<List<MfdPin>> change)
        {
            lock (_gate)
            {
                Mutex? mutex = null;
                bool owned = false;
                try
                {
                    mutex = new Mutex(false, MutexName);
                    try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) return "mfd_pins.json đang bận (tiến trình khác giữ khóa quá 3 s).";
                    List<MfdPin> pins = Read();
                    change(pins);
                    var renumbered = pins.Select((p, i) => p with { PinId = i + 1 }).ToList();
                    string? writeError = AtomicFile.WriteAllBytes(PinsPath, Utf8NoBom.GetBytes(JsonSerializer.Serialize(renumbered, Json)));
                    if (writeError == null)
                    {
                        _cached = renumbered;
                        _cachedStamp = Stamp();
                    }
                    return writeError;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
                finally
                {
                    if (owned) mutex?.ReleaseMutex();
                    mutex?.Dispose();
                }
            }
        }

        private List<MfdPin> Read()
        {
            try
            {
                if (!File.Exists(PinsPath)) return new List<MfdPin>();
                string text = File.ReadAllText(PinsPath, Utf8NoBom);
                List<MfdPin>? pins = JsonSerializer.Deserialize<List<MfdPin>>(text, Json);
                return pins?.Where(p => p != null && !string.IsNullOrEmpty(p.Content)).Select((p, i) => p with { PinId = i + 1 }).ToList()
                       ?? new List<MfdPin>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new List<MfdPin>(); // unreadable file: start empty, the next pin rewrites it whole
            }
        }

        private long Stamp()
        {
            try
            {
                var info = new FileInfo(PinsPath);
                return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static string OneLine(string text, int max)
        {
            string t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return t.Length > max ? t.Substring(0, max - 1) + "…" : t;
        }
    }

    /// <summary>
    /// TASK-13: the cards of the current response (the AI / QA answer or local utility result in latest_dispatch.json),
    /// shared by /copy, /pin, Alt+C, Alt+P, the Right MFD indicator and MCP manage_hud_widgets. The file is re-read and
    /// re-extracted only when its write stamp changes, so a /copy costs one file-attribute read.
    /// </summary>
    public sealed class HudWidgetService
    {
        private readonly string _latestDispatchPath;
        private readonly object _gate = new();
        private long _stamp = -1;
        private IReadOnlyList<HudWidgetCard> _cards = Array.Empty<HudWidgetCard>();
        private string _sourceText = string.Empty;
        private string? _sourceLabel;

        public HudWidgetService(string latestDispatchPath, MfdPinManager pins)
        {
            _latestDispatchPath = latestDispatchPath;
            Pins = pins;
        }

        public MfdPinManager Pins { get; }

        /// <summary>Text of the current response (AI answer, else QA answer, else local utility message).</summary>
        public string SourceText
        {
            get { Refresh(); lock (_gate) return _sourceText; }
        }

        public string? SourceLabel
        {
            get { Refresh(); lock (_gate) return _sourceLabel; }
        }

        public IReadOnlyList<HudWidgetCard> Cards
        {
            get { Refresh(); lock (_gate) return _cards; }
        }

        /// <summary>Card text for /copy [n]: code block n (1-based); without n the first code block, else the whole response.</summary>
        public (string? Text, string Label) CopyTarget(int? codeIndex)
        {
            IReadOnlyList<HudWidgetCard> cards = Cards;
            var code = cards.Where(c => c.Type == "code").ToList();
            if (codeIndex is int n)
            {
                if (n >= 1 && n <= code.Count) return (code[n - 1].Content, $"khối code #{n} ({code[n - 1].Language}, {code[n - 1].LineCount} dòng)");
                return (null, code.Count == 0 ? "phản hồi hiện tại không có khối code" : $"chỉ có {code.Count} khối code");
            }
            if (code.Count > 0) return (code[0].Content, $"khối code #1 ({code[0].Language}, {code[0].LineCount} dòng)");
            string text = SourceText;
            return text.Length > 0 ? (text, $"toàn bộ {SourceLabel ?? "phản hồi"}") : (null, "chưa có phản hồi nào");
        }

        /// <summary>Card n (1-based, any type) for /pin #n; without n the first card, else the whole response as a note.</summary>
        public (HudWidgetCard? Card, string? Error) PinTarget(int? cardIndex)
        {
            IReadOnlyList<HudWidgetCard> cards = Cards;
            if (cardIndex is int n)
            {
                if (n >= 1 && n <= cards.Count) return (cards[n - 1], null);
                return (null, cards.Count == 0 ? "phản hồi hiện tại không có thẻ nào" : $"chỉ có {cards.Count} thẻ");
            }
            if (cards.Count > 0) return (cards[0], null);
            string text = SourceText;
            if (text.Length == 0) return (null, "chưa có phản hồi nào để ghim");
            string first = text.Split('\n')[0].Trim();
            return (new HudWidgetCard(1, "note", first.Length > 60 ? first.Substring(0, 59) + "…" : first, null,
                text.Split('\n').Length, first, text), null);
        }

        private void Refresh()
        {
            long stamp;
            try
            {
                var info = new FileInfo(_latestDispatchPath);
                stamp = info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            lock (_gate)
            {
                if (stamp == _stamp) return;
                LatestDispatchSnapshot? s = stamp == 0 ? null : LatestDispatchFile.TryRead(_latestDispatchPath);
                if (stamp != 0 && s == null) return; // mid-write or unreadable: keep the previous cards, retry next call
                string text = s == null ? string.Empty
                    : !string.IsNullOrWhiteSpace(s.AiResponse) ? s.AiResponse
                    : !string.IsNullOrWhiteSpace(s.QaResponse) ? s.QaResponse!
                    : string.Empty;
                string? local = s != null && LatestDispatchFile.IsLocalEvent(s.Event) ? s.LocalUtility : null;
                _cards = WidgetCardExtractor.Extract(text, local);
                _sourceText = text;
                _sourceLabel = s == null ? null : local != null ? $"kết quả /{local}" : $"phản hồi dispatch #{s.DispatchId}";
                _stamp = stamp;
            }
        }
    }
}
