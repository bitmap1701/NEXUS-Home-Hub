using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NEXUSHome.CockpitUI;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool isBenchmarkMode = Array.Exists(args, a => a.Equals("--benchmark", StringComparison.OrdinalIgnoreCase));
            bool isConsoleMode = Array.Exists(args, a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));

            string vaultPath = Environment.GetEnvironmentVariable("NEXUS_VAULT_PATH")
                ?? @"C:\Agent\NEXUS_Home_Hub\03_Local_Data_Vault";
            HostLog.Initialize(vaultPath, isConsoleMode);
            HostLog.Write($"[HOST] NEXUSHome.HostService starting (pid {Environment.ProcessId}, benchmark={isBenchmarkMode})");
            string agentsPath = Environment.GetEnvironmentVariable("NEXUS_AGENTS_PATH")
                ?? @"C:\Agent\.agents\skills";

            string dbPath = Path.Combine(vaultPath, "structured_db", "nexus_memory.db");
            var memoryStore = new SqliteMemoryStore(dbPath);
            var agentRegistry = new AgentRegistry(agentsPath);

            var app = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            // Lazy Warm Window initialization on STA UI thread
            var cockpitWindow = new CockpitShellWindow(memoryStore, agentRegistry);
            cockpitWindow.WarmUpHidden();
            HostLog.Write($"[COCKPIT] Target monitor: {cockpitWindow.TargetMonitorInfo}");

            using var hotkeyListener = new AulaHero68HotkeyListener((action, triggerTicks) =>
            {
                HostLog.Write($"[HOTKEY] WM_HOTKEY received -> {action}");
                HandleHotkeyAction(cockpitWindow, memoryStore, action, triggerTicks);
            }, HostLog.Write);

            using var pipeServer = new CockpitPipeServer(cmd =>
            {
                var tcs = new TaskCompletionSource<string>();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    try
                    {
                        long ticks = Stopwatch.GetTimestamp();
                        if (cmd.Equals("TOGGLE", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleHotkeyAction(cockpitWindow, memoryStore, HotkeyAction.ToggleCockpit, ticks);
                            tcs.SetResult($"OK:Visibility={cockpitWindow.Visibility}:WakeMs={cockpitWindow.LastWakeupLatencyMs:F2}");
                        }
                        else if (cmd.Equals("SHOW", StringComparison.OrdinalIgnoreCase))
                        {
                            double ms = cockpitWindow.ShowAndFocusCockpit(ticks, "PipeShow");
                            tcs.SetResult($"OK:Shown:WakeMs={ms:F2}");
                        }
                        else if (cmd.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
                        {
                            cockpitWindow.HideCockpit("PipeHide");
                            tcs.SetResult("OK:Hidden");
                        }
                        else if (cmd.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                        {
                            tcs.SetResult(LocalPcMcpTools.GetSystemTelemetry());
                        }
                        else if (cmd.Equals("SHUTDOWN", StringComparison.OrdinalIgnoreCase))
                        {
                            tcs.SetResult("OK:ShuttingDown");
                            app.Shutdown();
                        }
                        else
                        {
                            tcs.SetResult($"ERR:UnknownCommand:{cmd}");
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.SetResult($"ERR:{ex.Message}");
                    }
                }));
                return tcs.Task;
            });

            using var notifyIcon = CreateSystemTrayIcon(app, cockpitWindow);

            if (isBenchmarkMode)
            {
                RunRealityCheckerBenchmark(app, cockpitWindow, hotkeyListener, memoryStore, vaultPath);
                notifyIcon.Visible = false;
                return;
            }

            HostLog.Write("[HOST] Ready. Waiting for hotkeys / pipe commands.");
            app.Run();
            notifyIcon.Visible = false;
            HostLog.Write("[HOST] Shutdown.");
        }

        private static void HandleHotkeyAction(
            CockpitShellWindow cockpitWindow,
            SqliteMemoryStore memoryStore,
            HotkeyAction action,
            long triggerTicks)
        {
            switch (action)
            {
                case HotkeyAction.ToggleCockpit:
                    if (cockpitWindow.Visibility == Visibility.Visible)
                    {
                        cockpitWindow.HideCockpit("Hotkey_AltSpace_Hide");
                    }
                    else
                    {
                        // Option B (approved 2026-11-07): view-only, the game keeps focus.
                        cockpitWindow.ShowPassiveCockpit(triggerTicks, "Hotkey_AltSpace_Show");
                    }
                    break;

                case HotkeyAction.QuickAgentPtt:
                    cockpitWindow.TriggerQuickAgentPtt(triggerTicks);
                    break;

                case HotkeyAction.CycleMfdMode:
                    if (cockpitWindow.Visibility == Visibility.Visible)
                    {
                        cockpitWindow.CycleMfdMode();
                    }
                    break;

                case HotkeyAction.PanicHide:
                    cockpitWindow.HideCockpit("Hotkey_PanicHide", trimWorkingSet: true);
                    memoryStore.WriteAuditLog("panic_hide", ActionRiskLevel.Green_SafeRead, "{}", "Executed");
                    break;
            }
        }

        private static System.Windows.Forms.NotifyIcon CreateSystemTrayIcon(
            System.Windows.Application app,
            CockpitShellWindow cockpitWindow)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Toggle NEXUS Cockpit (Alt+Space)", null, (_, _) =>
            {
                app.Dispatcher.Invoke(() =>
                {
                    if (cockpitWindow.Visibility == Visibility.Visible)
                        cockpitWindow.HideCockpit("TrayToggle");
                    else
                        cockpitWindow.ShowAndFocusCockpit(Stopwatch.GetTimestamp(), "TrayToggle");
                });
            });
            menu.Items.Add("Exit NEXUS HostService", null, (_, _) =>
            {
                app.Dispatcher.Invoke(app.Shutdown);
            });

            return new System.Windows.Forms.NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "NEXUS Home Hub Host (Alt+Space)",
                ContextMenuStrip = menu,
                Visible = true
            };
        }

        private static void RunRealityCheckerBenchmark(
            System.Windows.Application app,
            CockpitShellWindow cockpitWindow,
            AulaHero68HotkeyListener hotkeyListener,
            SqliteMemoryStore memoryStore,
            string vaultPath)
        {
            // 1. Measure Cold WM_HOTKEY (Alt+Space) -> Show + Focus latency
            hotkeyListener.ResetDebounceGateForBenchmark();
            hotkeyListener.DispatchSyntheticWmHotkey(HotkeyAction.ToggleCockpit);
            double coldWakeupMs = cockpitWindow.LastWakeupLatencyMs;

            // Measure Memory while Cockpit is OPEN
            using var procOpen = Process.GetCurrentProcess();
            procOpen.Refresh();
            long openWorkingSetBytes = procOpen.WorkingSet64;
            long openPrivateBytes = procOpen.PrivateMemorySize64;

            // 2. Measure Warm WM_HOTKEY (Alt+Space) -> Show + Focus latency (3 iterations of Alt+Space Toggle)
            double[] warmLatencies = new double[3];
            for (int i = 0; i < 3; i++)
            {
                // Toggle Hide via Alt+Space
                hotkeyListener.ResetDebounceGateForBenchmark();
                hotkeyListener.DispatchSyntheticWmHotkey(HotkeyAction.ToggleCockpit);

                // Toggle Show+Focus via Alt+Space
                hotkeyListener.ResetDebounceGateForBenchmark();
                hotkeyListener.DispatchSyntheticWmHotkey(HotkeyAction.ToggleCockpit);
                warmLatencies[i] = cockpitWindow.LastWakeupLatencyMs;
            }

            // 3. Hide Cockpit via WM_HOTKEY (PanicHide) and measure Hidden Memory
            hotkeyListener.ResetDebounceGateForBenchmark();
            hotkeyListener.DispatchSyntheticWmHotkey(HotkeyAction.PanicHide);

            using var procHidden = Process.GetCurrentProcess();
            procHidden.Refresh();
            long hiddenWorkingSetBytes = procHidden.WorkingSet64;
            long hiddenPrivateBytes = procHidden.PrivateMemorySize64;

            // Measured (not assumed) idle CPU of this process while hidden: 2 s sample of TotalProcessorTime.
            TimeSpan cpuStart = procHidden.TotalProcessorTime;
            var cpuSampleWatch = Stopwatch.StartNew();
            System.Threading.Thread.Sleep(2000);
            procHidden.Refresh();
            double idleCpuPercent = (procHidden.TotalProcessorTime - cpuStart).TotalMilliseconds
                / (cpuSampleWatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;

            var (journalMode, auditCount, memCount) = memoryStore.GetWalTelemetrySummary();

            var report = new
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                SqliteJournalMode = journalMode,
                SqliteBusyTimeoutMs = 5000,
                AuditLogCount = auditCount,
                MemoryRecordCount = memCount,
                ColdWakeupLatencyMs = Math.Round(coldWakeupMs, 3),
                WarmWakeupLatencyRun1Ms = Math.Round(warmLatencies[0], 3),
                WarmWakeupLatencyRun2Ms = Math.Round(warmLatencies[1], 3),
                WarmWakeupLatencyRun3Ms = Math.Round(warmLatencies[2], 3),
                AverageWarmWakeupLatencyMs = Math.Round((warmLatencies[0] + warmLatencies[1] + warmLatencies[2]) / 3.0, 3),
                TargetLatencyMs = 16.0,
                OpenStateWorkingSetMb = Math.Round(openWorkingSetBytes / (1024.0 * 1024.0), 2),
                OpenStatePrivateMemoryMb = Math.Round(openPrivateBytes / (1024.0 * 1024.0), 2),
                HiddenStateWorkingSetMb = Math.Round(hiddenWorkingSetBytes / (1024.0 * 1024.0), 2),
                HiddenStatePrivateMemoryMb = Math.Round(hiddenPrivateBytes / (1024.0 * 1024.0), 2),
                IdleCpuPercentWhenHidden = Math.Round(idleCpuPercent, 3),
                IdleGpuPercentWhenHidden = (double?)null,
                IdleGpuMeasurement = "Not measured in-process; verify in Task Manager > Details > GPU engine column",
                TargetMonitor = cockpitWindow.TargetMonitorInfo
            };

            string jsonPath = Path.Combine(vaultPath, "handoff", "task02_benchmark.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), utf8NoBom);
            HostLog.Write($"[BENCH] Report written to {jsonPath} (idle CPU {idleCpuPercent:F3}%, monitor {cockpitWindow.TargetMonitorInfo})");

            cockpitWindow.Close();
        }
    }
}
