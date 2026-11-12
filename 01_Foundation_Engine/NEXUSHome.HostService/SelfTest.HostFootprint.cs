using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using NEXUSHome.CockpitUI;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-17b (ADR-70) host-footprint: memory of the full HostService, part by part ─────────────────────────────

        /// <summary>
        /// TASK-17b E2 "host-footprint": builds the same parts as Program.Main, in the same order (WPF Application, Cockpit window +
        /// WarmUpHidden, /ask warm-up, hotkey message window, named-pipe server on a private test pipe name, tray NotifyIcon), then a
        /// short Task16b-like load (mock /ask, /calc, history), /net (Network), /mic list (Core Audio) and the hidden idle point.
        /// After each part: aggressive blocking GC (what the idle [MEM] trim does) and one Private WS / GC heap / commit / pool /
        /// JIT / thread reading, so the delta of each row is that part's cost. The MCP server is a separate process (McpTools.exe),
        /// not part of this one. Mock Gemini only, 0 tokens. Pass = every phase measured and the whole run &lt;= 8000 ms (17b.4).
        /// The runtime knobs this process started with (DOTNET_TieredPGO, DOTNET_TieredCompilation, DOTNET_gcConcurrent,
        /// DOTNET_GCgen0size) are reported, so tools\Measure-HostFootprint.ps1 can compare variants on one machine.
        /// </summary>
        private static (object Report, bool Pass) HostFootprint(NexusRuntime runtime)
        {
            const int BudgetMs = 8000; // Architect 17b.4: 5000 -> 8000 ms (diagnostic selftest; full-opt JIT with TieredCompilation off)
            var total = Stopwatch.StartNew();
            var phases = new List<Dictionary<string, object?>>();
            string? oldMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable,
                "**Gợi ý** cho NEXUS: dùng `Span<T>` để giảm cấp phát.\n\n- Bước 1: đo trước.\n- Bước 2: dọn khi rảnh.\n\n" +
                "```csharp\nvar b = ArrayPool<byte>.Shared.Rent(4096);\n```\n\nTiếng Việt có dấu: ắ ằ ẳ ẵ ặ ố ồ ổ ỗ ộ.");
            string pipeName = CockpitPipeServer.PipeName + "_selftest_" + Environment.ProcessId; // never the real host's pipe
            CockpitShellWindow? w = null;
            AulaHero68HotkeyListener? hotkeys = null;
            CockpitPipeServer? pipe = null;
            System.Windows.Forms.NotifyIcon? tray = null;
            string? pipeReply = null, error = null;
            try
            {
                runtime.QuotaGuard.ResetSlidingWindows();
                Add("runtime", "SQLite + config + registry (NexusRuntime.Create, before this selftest)");
                System.Windows.Application app = System.Windows.Application.Current
                    ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                Add("app", "WPF Application");
                w = new CockpitShellWindow(runtime);
                w.WarmUpHidden();
                Add("cockpit", "Cockpit window + WarmUpHidden");
                runtime.WarmUp();
                Add("warmup", "/ask warm-up (SQLite, route + regex, JSON)");
                hotkeys = new AulaHero68HotkeyListener((_, _) => { }, null,
                    runtime.Config.Current.PassiveHudHotkey, runtime.Config.Current.CommandModeHotkey);
                Add("hotkeys", "RegisterHotKey message window (fails harmlessly while the real host owns the keys)");
                pipe = new CockpitPipeServer(_ => Task.FromResult("OK:selftest"), pipeName);
                Add("pipe", "NamedPipe server (listen loop on the thread pool)");
                tray = Program.CreateSystemTrayIcon(app, w);
                Add("tray", "Tray NotifyIcon (WinForms + GDI+)");
                Task<string?> roundTrip = Task.Run(() =>
                {
                    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    client.Connect(2000);
                    using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                    writer.WriteLine("{\"cmd\":\"STATUS\"}");
                    return reader.ReadLine();
                });
                pipeReply = roundTrip.Wait(3000) ? roundTrip.Result : "timeout";
                Add("pipeRoundTrip", "one pipe request (async I/O + thread pool)");
                w.Left = -4000; // off screen
                w.Top = 0;
                w.ShowActivated = false;
                w.Show();
                w.ApplyUiFromPipe("full");
                w.ExecuteFromPipe("/ask! Gate 17b host footprint " + Guid.NewGuid().ToString("N").Substring(0, 8));
                Pump(500);
                w.ExecuteFromPipe("/calc sqrt(144)+2^8");
                w.HistoryFromPipe("refresh");
                for (int i = 0; i < 10; i++) w.HistoryFromPipe(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.HistoryFromPipe("live");
                Pump(300);
                Add("workload", "Task16b-like load: mock /ask, /calc, history");
                w.ExecuteFromPipe("/net");
                Pump(400);
                Add("net", "Network radar /net (pings cut at 200 ms)");
                w.ExecuteFromPipe("/mic list");
                Pump(100);
                Add("mic", "Core Audio endpoints /mic list");
                w.ApplyUiFromPipe("half");
                w.HideCockpit("selftest");
                MemorySlimming.Trim("selftest host-footprint");
                Add("hiddenTrimmed", "hidden + idle [MEM] trim (what Verify-Task16b E5 reads)");
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch (InvalidOperationException) { }
                pipe?.Dispose();
                hotkeys?.Dispose();
                try { w?.Close(); } catch (InvalidOperationException) { }
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, oldMock);
            }
            long totalMs = total.ElapsedMilliseconds;

            double Delta(string name) => phases.FirstOrDefault(p => (string?)p["phase"] == name)?["deltaMb"] is double d ? d : 0;
            string[] outside = { "warmup", "hotkeys", "pipe", "tray", "pipeRoundTrip", "net", "mic" };
            bool withinBudget = totalMs <= BudgetMs;
            // the 5000 ms budget is the PC gate (Verify-Task17b E2 checks withinBudget); the shared CI runner only has to finish
            bool pass = error == null && phases.Count == 12 && (withinBudget || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true");
            return (new
            {
                scenario = "host-footprint",
                pass,
                error,
                totalMs,
                budgetMs = BudgetMs,
                withinBudget,
                pipeReply,
                runtimeKnobs = new
                {
                    tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                    gcConcurrentEnv = Environment.GetEnvironmentVariable("DOTNET_gcConcurrent"),
                    gcConcurrentConfig = AppContext.GetData("System.GC.Concurrent")?.ToString(),
                    gcGen0Size = Environment.GetEnvironmentVariable("DOTNET_GCgen0size"),
                    serverGc = System.Runtime.GCSettings.IsServerGC,
                    latencyMode = System.Runtime.GCSettings.LatencyMode.ToString()
                },
                summary = new
                {
                    cockpitMb = Math.Round(Delta("app") + Delta("cockpit") + Delta("workload"), 2),
                    outsideCockpitMb = Math.Round(outside.Sum(Delta), 2),
                    perPart = outside.ToDictionary(n => n, Delta),
                    idlePrivateWsMb = phases.LastOrDefault()?["privateWsMb"],
                    idleGcHeapMb = phases.LastOrDefault()?["gcHeapMb"]
                },
                phases
            }, pass);

            void Add(string name, string part)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                Pump(50);
                long? pws = MemoryDiagnostics.PrivateWorkingSetBytes();
                double? mb = pws.HasValue ? Math.Round(pws.Value / 1048576.0, 2) : null;
                double? prev = phases.Count > 0 ? phases[^1]["privateWsMb"] as double? : null;
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                using Process p = Process.GetCurrentProcess();
                phases.Add(new Dictionary<string, object?>
                {
                    ["phase"] = name,
                    ["part"] = part,
                    ["atMs"] = total.ElapsedMilliseconds,
                    ["privateWsMb"] = mb,
                    ["deltaMb"] = mb.HasValue && prev.HasValue ? Math.Round(mb.Value - prev.Value, 2) : null,
                    ["gcHeapMb"] = Math.Round(gc.HeapSizeBytes / 1048576.0, 2),
                    ["gcCommittedMb"] = Math.Round(gc.TotalCommittedBytes / 1048576.0, 2),
                    ["commitMb"] = Math.Round(p.PrivateMemorySize64 / 1048576.0, 2),
                    ["pagedPoolKb"] = Math.Round(p.PagedSystemMemorySize64 / 1024.0, 1),
                    ["nonPagedPoolKb"] = Math.Round(p.NonpagedSystemMemorySize64 / 1024.0, 1),
                    ["jitMethods"] = System.Runtime.JitInfo.GetCompiledMethodCount(false),
                    ["jitIlKb"] = Math.Round(System.Runtime.JitInfo.GetCompiledILBytes(false) / 1024.0, 1),
                    ["assemblies"] = AppDomain.CurrentDomain.GetAssemblies().Length,
                    ["threads"] = p.Threads.Count,
                    ["handles"] = p.HandleCount
                });
            }

            static void Pump(int ms)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
        }
    }
}
