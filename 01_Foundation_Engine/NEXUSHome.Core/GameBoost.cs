using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace NEXUSHomeHub.Core
{
    /// <summary>Foreground window helpers (Win32), used by GameBoost and the Cockpit status command.</summary>
    public static class ForegroundWindowInfo
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

        public static IntPtr Current() => GetForegroundWindow();

        public static int ProcessIdOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return unchecked((int)pid);
        }

        public static string TitleOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var buffer = new char[256];
            int length = GetWindowText(hwnd, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : string.Empty;
        }
    }

    public sealed record BoostEntry(int Pid, string Name, double WorkingSetMb);

    public sealed record BoostResult(
        bool DryRun,
        string TargetStatus,
        string? TargetProcess,
        int ForegroundPid,
        string? ForegroundProcess,
        int TrimmedCount,
        double FreedMb,
        int YieldedCount,
        int SkippedErrors,
        IReadOnlyList<BoostEntry> Shielded,
        IReadOnlyList<BoostEntry> Trimmed,
        IReadOnlyList<BoostEntry> Yielded,
        string Message,
        int? DeniedErrorCode = null);

    /// <summary>
    /// ADR-06 anti-cheat safe Game Boost.
    /// - Foreground &amp; Game Shield: the foreground window's process, the target, this process, the anti-cheat/game
    ///   whitelist and core system processes are never trimmed or re-prioritised.
    /// - Anti-cheat targets (DeltaForce*, TslGame*, ...) are never opened with write access: instead of forcing
    ///   PriorityClass=High (Access Denied under ACE-Guard), Background Yield lowers background processes above
    ///   100 MB from Normal to BelowNormal. "/boost off" (RestoreYield) puts them back.
    /// - Non-protected targets (e.g. devenv) get PriorityClass=High; Access Denied is caught and falls back to Yield.
    /// - Target not running and no usable foreground process: nothing is touched.
    /// </summary>
    public static class GameBoost
    {
        public const double YieldThresholdMb = 100.0;

        /// <summary>
        /// Game / anti-cheat prefixes from ADR-06 (prefix, case-insensitive). The spec file had escape-mangled names;
        /// "fczf" (FC Online) is reconstructed from "\fczf" (see qa_report §6).
        /// </summary>
        public static readonly string[] ProtectedPrefixes =
        {
            "DeltaForce", "TslGame", "fczf", "Roblox", "SGuard", "ACE-", "BEService"
        };

        /// <summary>System processes never touched (ADR-06: audiodg, dwm, nvcontainer; plus Windows shell/session core).</summary>
        public static readonly string[] SystemProcesses =
        {
            "audiodg", "dwm", "nvcontainer", "csrss", "winlogon", "fontdrvhost", "explorer", "System", "Idle"
        };

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        private sealed record YieldRecord(int Pid, string Name, ProcessPriorityClass Original);

        private static readonly object Gate = new();
        private static readonly List<YieldRecord> YieldedProcesses = new();

        public static bool IsProtectedName(string processName) =>
            ProtectedPrefixes.Any(p => processName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        private static bool IsSystemName(string processName) =>
            SystemProcesses.Any(s => processName.Equals(s, StringComparison.OrdinalIgnoreCase));

        /// <param name="targetHint">Process-name prefix; null/empty = NEXUS_GAME_PROCESS (default DeltaForceClient) if running, else the foreground process.</param>
        /// <param name="foregroundHwnd">Window to shield; IntPtr.Zero = current foreground window.</param>
        /// <param name="dryRun">Plan only: nothing is trimmed or re-prioritised.</param>
        /// <param name="simulateAccessDenied">
        /// ADR-11 deterministic test: behave as if SetPriorityClass failed with Win32 error 5 (Access Denied, what
        /// ACE-Guard returns) so the Background Yield branch can be verified without running a protected game.
        /// </param>
        public static BoostResult Run(string? targetHint, IntPtr foregroundHwnd, bool dryRun, bool simulateAccessDenied = false)
        {
            IntPtr hwnd = foregroundHwnd != IntPtr.Zero ? foregroundHwnd : ForegroundWindowInfo.Current();
            int foregroundPid = ForegroundWindowInfo.ProcessIdOf(hwnd);
            int selfPid = Environment.ProcessId;

            Process[] processes = Process.GetProcesses();
            try
            {
                string? foregroundName = SafeName(processes.FirstOrDefault(p => p.Id == foregroundPid));

                string? target = Normalize(targetHint);
                if (target == null)
                {
                    string game = Normalize(Environment.GetEnvironmentVariable("NEXUS_GAME_PROCESS")) ?? "DeltaForceClient";
                    target = processes.Any(p => NameStartsWith(p, game))
                        ? game
                        : (foregroundPid != 0 && foregroundPid != selfPid ? foregroundName : null);
                }

                List<Process> targets = target == null
                    ? new List<Process>()
                    : processes.Where(p => NameStartsWith(p, target)).ToList();

                if (targets.Count == 0)
                {
                    return new BoostResult(dryRun, "NotRunning", target, foregroundPid, foregroundName, 0, 0, 0, 0,
                        Array.Empty<BoostEntry>(), Array.Empty<BoostEntry>(), Array.Empty<BoostEntry>(),
                        $"[BOOST] Không thấy '{target ?? "(không có mục tiêu)"}' đang chạy: không đụng vào tiến trình nào.");
                }

                var targetPids = targets.Select(p => p.Id).ToHashSet();
                bool targetProtected = targets.Any(p => IsProtectedName(SafeName(p) ?? string.Empty));

                // 1. Target priority. Anti-cheat targets are never opened for write access.
                const string AceGuardStatus = "ACE-Guard Protected (Background Yield Active)";
                string status;
                int? deniedErrorCode = null;
                if (targetProtected)
                {
                    status = AceGuardStatus;
                }
                else if (dryRun && !simulateAccessDenied)
                {
                    status = "WouldElevate";
                }
                else
                {
                    int elevated = 0;
                    foreach (Process t in targets)
                    {
                        try
                        {
                            if (simulateAccessDenied) throw new Win32Exception(5); // ERROR_ACCESS_DENIED
                            if (!dryRun) t.PriorityClass = ProcessPriorityClass.High;
                            elevated++;
                        }
                        catch (Win32Exception ex)
                        {
                            // Access Denied (5) is what a kernel anti-cheat returns: catch quietly, fall back to Yield.
                            deniedErrorCode = ex.NativeErrorCode;
                        }
                        catch (InvalidOperationException)
                        {
                            // exited meanwhile
                        }
                    }
                    status = elevated > 0
                        ? (dryRun ? "WouldElevate" : $"Elevated ({elevated})")
                        : deniedErrorCode != null ? AceGuardStatus : "Exited";
                }
                bool yieldMode = status.Contains("Background Yield", StringComparison.Ordinal);

                // 2. Scan background processes.
                var shielded = new List<BoostEntry>();
                var trimmed = new List<BoostEntry>();
                var yielded = new List<BoostEntry>();
                long freedBytes = 0;
                int errors = 0;

                foreach (Process p in processes)
                {
                    string? name = SafeName(p);
                    if (name == null) continue;

                    int pid = p.Id;
                    long before = SafeWorkingSet(p);
                    bool isShieldTarget = pid == foregroundPid || targetPids.Contains(pid) || IsProtectedName(name);
                    if (isShieldTarget)
                    {
                        shielded.Add(new BoostEntry(pid, name, Mb(before)));
                        continue;
                    }
                    if (pid == selfPid || IsSystemName(name) || SafeSessionId(p) <= 0) continue;

                    if (yieldMode && Mb(before) > YieldThresholdMb)
                    {
                        try
                        {
                            if (p.PriorityClass == ProcessPriorityClass.Normal)
                            {
                                if (!dryRun)
                                {
                                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    lock (Gate) YieldedProcesses.Add(new YieldRecord(pid, name, ProcessPriorityClass.Normal));
                                }
                                yielded.Add(new BoostEntry(pid, name, Mb(before)));
                            }
                        }
                        catch (Exception)
                        {
                            errors++;
                        }
                    }

                    if (dryRun)
                    {
                        trimmed.Add(new BoostEntry(pid, name, Mb(before)));
                        continue;
                    }

                    try
                    {
                        EmptyWorkingSet(p.Handle);
                        p.Refresh();
                        freedBytes += Math.Max(0, before - p.WorkingSet64);
                        trimmed.Add(new BoostEntry(pid, name, Mb(before)));
                    }
                    catch (Exception)
                    {
                        errors++;
                    }
                }

                double freedMb = Math.Round(Mb(freedBytes), 1);
                string prefix = dryRun ? "[BOOST DRY-RUN]" : "[BOOST]";
                string message =
                    $"{prefix} {target}: {status}. " +
                    $"{(dryRun ? "Sẽ thu hồi" : "Thu hồi")} {freedMb:F0} MB RAM từ {trimmed.Count} tiến trình nền; " +
                    $"hạ ưu tiên {yielded.Count} tiến trình > {YieldThresholdMb:F0} MB; " +
                    $"bảo vệ foreground '{foregroundName ?? "?"}' + {Math.Max(0, shielded.Count - 1)} tiến trình game/anti-cheat.";

                return new BoostResult(dryRun, status, target, foregroundPid, foregroundName, trimmed.Count, freedMb,
                    yielded.Count, errors, shielded, trimmed, yielded, message, deniedErrorCode);
            }
            finally
            {
                foreach (Process p in processes) p.Dispose();
            }
        }

        /// <summary>Restores processes lowered by Background Yield. Returns how many were restored.</summary>
        public static int RestoreYield()
        {
            List<YieldRecord> records;
            lock (Gate)
            {
                records = YieldedProcesses.ToList();
                YieldedProcesses.Clear();
            }

            int restored = 0;
            foreach (YieldRecord record in records)
            {
                try
                {
                    using Process p = Process.GetProcessById(record.Pid);
                    if (p.ProcessName.Equals(record.Name, StringComparison.OrdinalIgnoreCase) &&
                        p.PriorityClass == ProcessPriorityClass.BelowNormal)
                    {
                        p.PriorityClass = record.Original;
                        restored++;
                    }
                }
                catch (Exception)
                {
                    // process exited or access changed: nothing to restore
                }
            }
            return restored;
        }

        public static int PendingYieldCount
        {
            get
            {
                lock (Gate) return YieldedProcesses.Count;
            }
        }

        private static string? Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
        }

        private static bool NameStartsWith(Process p, string prefix)
        {
            string? name = SafeName(p);
            return name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string? SafeName(Process? p)
        {
            if (p == null) return null;
            try
            {
                return p.ProcessName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long SafeWorkingSet(Process p)
        {
            try
            {
                return p.WorkingSet64;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int SafeSessionId(Process p)
        {
            try
            {
                return p.SessionId;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
    }
}
