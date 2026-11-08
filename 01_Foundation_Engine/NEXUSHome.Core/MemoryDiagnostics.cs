using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-13a: one memory breakdown of this process (MB unless the name says otherwise). Null = not available.</summary>
    public sealed record MemorySnapshot(
        string CapturedAtUtc,
        double? PrivateWorkingSetMb,
        string PrivateWorkingSetSource,
        double WorkingSetMb,
        double CommitMb,
        double ManagedHeapAllocatedMb,
        double GcHeapSizeMb,
        double GcCommittedMb,
        double GcFragmentedMb,
        double[] GcGenerationSizesMb,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double? SqliteNativeMb,
        int AgentCount,
        double AgentRegistryCacheMb,
        double VaultIndexCacheMb,
        int ThreadCount,
        int HandleCount,
        bool SlimmingEnabled,
        string HudLabel);

    /// <summary>TASK-13a: what one idle trim did.</summary>
    public sealed record MemoryTrimResult(
        string Reason,
        string AtUtc,
        double? PrivateWorkingSetBeforeMb,
        double? PrivateWorkingSetAfterMb,
        double ManagedBeforeMb,
        double ManagedAfterMb,
        double DurationMs)
    {
        public double? FreedMb => PrivateWorkingSetBeforeMb is double b && PrivateWorkingSetAfterMb is double a ? Math.Round(b - a, 1) : null;
    }

    /// <summary>
    /// TASK-13a (ADR-09): the memory numbers of this process, measured the way the Gates measure them.
    /// Private Working Set = resident pages that are not shareable, the "Working Set - Private" counter that Task Manager
    /// and the Verify scripts (Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate) report. It is read with
    /// GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX2) - one syscall, no PerformanceCounter, no Perflib tables loaded
    /// into the process - and, on Windows builds older than 10 22H2 / 11 22H2 (Sept 2023 update), by counting the
    /// non-shared pages of QueryWorkingSet. PrivateMemorySize64 (commit charge, ~94 MB) is a different metric and is no
    /// longer shown on the HUD.
    /// </summary>
    public static class MemoryDiagnostics
    {
        public const double SoftWarnMb = 54.0;
        public const double HardLimitMb = 60.0;
        private const double Mb = 1024.0 * 1024.0;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx2
        {
            public uint Cb;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
            public nuint PrivateUsage;
            public nuint PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryWorkingSet(IntPtr process, IntPtr buffer, uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static int _ex2Supported = -1; // -1 unknown, 0 no, 1 yes

        /// <summary>Where the last <see cref="PrivateWorkingSetBytes"/> value came from.</summary>
        public static string PrivateWorkingSetSource { get; private set; } = "not read yet";

        /// <summary>Private Working Set of this process in bytes, or null when no method works (non-Windows, access denied).</summary>
        public static long? PrivateWorkingSetBytes()
        {
            if (!OperatingSystem.IsWindows()) { PrivateWorkingSetSource = "unavailable (not Windows)"; return null; }
            try
            {
                if (_ex2Supported != 0)
                {
                    var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
                    if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Cb) &&
                        counters.Cb == (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() && counters.PrivateWorkingSetSize > 0)
                    {
                        _ex2Supported = 1;
                        PrivateWorkingSetSource = "GetProcessMemoryInfo(EX2)";
                        return (long)counters.PrivateWorkingSetSize;
                    }
                    _ex2Supported = 0;
                }
                long? counted = CountPrivateWorkingSetPages();
                PrivateWorkingSetSource = counted.HasValue ? "QueryWorkingSet (non-shared pages)" : "unavailable";
                return counted;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                PrivateWorkingSetSource = $"unavailable ({ex.GetType().Name})";
                return null;
            }
        }

        public static double? PrivateWorkingSetMb()
        {
            long? bytes = PrivateWorkingSetBytes();
            return bytes.HasValue ? Math.Round(bytes.Value / Mb, 1) : null;
        }

        /// <summary>Fallback: pages of the working set whose Shared bit (bit 8 of PSAPI_WORKING_SET_BLOCK) is clear.</summary>
        private static long? CountPrivateWorkingSetPages()
        {
            int entries = 65536;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int size = IntPtr.Size * (entries + 1);
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (QueryWorkingSet(GetCurrentProcess(), buffer, (uint)size))
                    {
                        long count = Marshal.ReadIntPtr(buffer).ToInt64();
                        long privatePages = 0;
                        for (long i = 0; i < count; i++)
                        {
                            long block = Marshal.ReadIntPtr(buffer, (int)(IntPtr.Size * (i + 1))).ToInt64();
                            if ((block & 0x100) == 0) privatePages++;
                        }
                        return privatePages * Environment.SystemPageSize;
                    }
                    long needed = Marshal.ReadIntPtr(buffer).ToInt64();
                    entries = (int)Math.Min(int.MaxValue / IntPtr.Size - 1, Math.Max(entries * 2L, needed + 4096));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }

        /// <summary>The text the HUD and /status show: "Private WS: 51.2 MB / 60 MB".</summary>
        public static string HudLabel(double? privateWorkingSetMb) =>
            privateWorkingSetMb is double mb
                ? $"Private WS: {mb.ToString("F1", CultureInfo.InvariantCulture)} MB / {HardLimitMb.ToString("F0", CultureInfo.InvariantCulture)} MB"
                : "Private WS: n/a";

        /// <summary>Native memory held by SQLite in this process (page caches, statements), or null if the call fails.</summary>
        public static double? SqliteNativeMb()
        {
            try
            {
                return Math.Round(SQLitePCL.raw.sqlite3_memory_used() / Mb, 2);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static MemorySnapshot Capture(AgentRegistry? registry, VaultGroundingEngine? grounding)
        {
            double? privateWs = PrivateWorkingSetMb();
            using Process self = Process.GetCurrentProcess();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            var generations = new double[gc.GenerationInfo.Length];
            for (int i = 0; i < generations.Length; i++) generations[i] = Math.Round(gc.GenerationInfo[i].SizeAfterBytes / Mb, 2);

            return new MemorySnapshot(
                DateTime.UtcNow.ToString("o"),
                privateWs,
                PrivateWorkingSetSource,
                Math.Round(self.WorkingSet64 / Mb, 1),
                Math.Round(self.PrivateMemorySize64 / Mb, 1),
                Math.Round(GC.GetTotalMemory(forceFullCollection: false) / Mb, 2),
                Math.Round(gc.HeapSizeBytes / Mb, 2),
                Math.Round(gc.TotalCommittedBytes / Mb, 2),
                Math.Round(gc.FragmentedBytes / Mb, 2),
                generations,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                SqliteNativeMb(),
                registry?.Count ?? 0,
                Math.Round((registry?.EstimateIndexBytes() ?? 0) / Mb, 3),
                Math.Round((grounding?.EstimateMemoBytes() ?? 0) / Mb, 3),
                self.Threads.Count,
                self.HandleCount,
                MemorySlimming.Enabled,
                HudLabel(privateWs));
        }

        /// <summary>One line for /mem.</summary>
        public static string Describe(MemorySnapshot s)
        {
            string zone = s.PrivateWorkingSetMb is double p
                ? p >= HardLimitMb ? "ĐỎ (≥ 60)" : p >= SoftWarnMb ? "VÀNG (≥ 54)" : "XANH"
                : "?";
            MemoryTrimResult? trim = MemorySlimming.LastTrim;
            string trimText = !s.SlimmingEnabled
                ? "tắt (NEXUS_MEMORY_SLIMMING=0)"
                : trim == null
                    ? $"bật, chưa chạy (sau {MemorySlimming.IdleTrimDelayMs / 1000} s rảnh)"
                    : $"bật, lần cuối {trim.PrivateWorkingSetBeforeMb?.ToString("F1", CultureInfo.InvariantCulture) ?? "?"} → {trim.PrivateWorkingSetAfterMb?.ToString("F1", CultureInfo.InvariantCulture) ?? "?"} MB ({trim.DurationMs:F0} ms, {MemorySlimming.TrimCount} lần)";
            return string.Create(CultureInfo.InvariantCulture,
                $"[MEM] {s.HudLabel} [{zone}] | Working Set {s.WorkingSetMb:F1} MB | Commit {s.CommitMb:F1} MB | " +
                $"GC heap {s.GcHeapSizeMb:F1} MB (committed {s.GcCommittedMb:F1}, fragmented {s.GcFragmentedMb:F1}; managed {s.ManagedHeapAllocatedMb:F1}) | " +
                $"SQLite {(s.SqliteNativeMb is double q ? q.ToString("F2", CultureInfo.InvariantCulture) : "?")} MB | " +
                $"Agents {s.AgentCount} (~{s.AgentRegistryCacheMb * 1024:F0} KB) | Vault memo ~{s.VaultIndexCacheMb * 1024:F0} KB | " +
                $"Threads {s.ThreadCount}, handles {s.HandleCount} | Dọn khi rảnh: {trimText}");
        }
    }

    /// <summary>
    /// TASK-13a memory slimming, all measured against NEXUS_MEMORY_SLIMMING=0 (Verify-Task13a E2 runs both):
    /// 1. SQLite page cache capped at <see cref="SqliteCacheKb"/> KB per pooled connection (default is 2 MB each).
    /// 2. Idle trim, run by the Cockpit once after <see cref="IdleTrimDelayMs"/> ms without activity while not in command
    ///    mode: idle pooled SQLite connections are closed (their page caches freed) and one blocking, compacting
    ///    GCCollectionMode.Aggressive collection returns free GC regions and the gen0 budget to the OS. It is a one-shot
    ///    timer re-armed by activity, so an idle Cockpit does no periodic work (0 % CPU, ADR-05). No EmptyWorkingSet:
    ///    pages are released, not pushed to the standby list, so the number is the real idle footprint (Gate 14b point 4).
    /// </summary>
    public static class MemorySlimming
    {
        public const string DisableVariable = "NEXUS_MEMORY_SLIMMING";
        public const int IdleTrimDelayMs = 5000;
        public const int SqliteCacheKb = 512;

        private static readonly object Gate = new();
        private static int _trimCount;

        /// <summary>False only when NEXUS_MEMORY_SLIMMING=0 (Gate 13a "before" measurement).</summary>
        public static bool Enabled { get; } = Environment.GetEnvironmentVariable(DisableVariable) != "0";

        public static MemoryTrimResult? LastTrim { get; private set; }

        public static int TrimCount => Volatile.Read(ref _trimCount);

        /// <summary>PRAGMA appended to every new SQLite connection (empty when slimming is off).</summary>
        public static string SqlitePragma => Enabled ? $" PRAGMA cache_size=-{SqliteCacheKb};" : string.Empty;

        public static MemoryTrimResult Trim(string reason)
        {
            lock (Gate)
            {
                long ts0 = Stopwatch.GetTimestamp();
                double? before = MemoryDiagnostics.PrivateWorkingSetMb();
                double managedBefore = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2);

                SqliteConnection.ClearAllPools(); // idle pooled connections close now; busy ones close when returned
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                double? after = MemoryDiagnostics.PrivateWorkingSetMb();
                double managedAfter = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2);
                double ms = Math.Round((Stopwatch.GetTimestamp() - ts0) * 1000.0 / Stopwatch.Frequency, 1);
                var result = new MemoryTrimResult(reason, DateTime.UtcNow.ToString("o"), before, after, managedBefore, managedAfter, ms);
                LastTrim = result;
                Interlocked.Increment(ref _trimCount);
                return result;
            }
        }
    }
}
