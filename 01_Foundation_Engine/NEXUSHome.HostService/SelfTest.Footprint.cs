using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using NEXUSHome.CockpitUI;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        /// <summary>
        /// Gate 16c run 2 diagnosis "ui-footprint" (CI A/B, mock Gemini, is_test rows only): where the Cockpit window's memory goes.
        /// The same file runs on the old window (v0.16.0) and on the new one, so the per-phase Private WS / GC heap / JIT deltas can
        /// be compared on one machine. Phases: window created, shown (Half), Full, a mock /ask with a long markdown answer + /calc,
        /// UISTATE x3, hidden. Each reading follows an aggressive blocking GC (what the idle trim does) and a short dispatcher pump.
        /// Report only: pass = every phase was measured.
        /// </summary>
        private static (object Report, bool Pass) UiFootprint(NexusRuntime runtime)
        {
            string? oldMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable,
                "**Gợi ý tối ưu** cho NEXUS: dùng `ArrayPool<byte>.Shared` và `Span<T>` để giảm cấp phát.\n\n" +
                "- Bước 1: đo `Private WS` trước và sau.\n- Bước 2: tắt `Storyboard` lặp.\n- Bước 3: kiểm tra `GC.GetGCMemoryInfo()`.\n\n" +
                "```csharp\nvar buffer = ArrayPool<byte>.Shared.Rent(4096);\ntry { Process(buffer.AsSpan(0, 4096)); }\nfinally { ArrayPool<byte>.Shared.Return(buffer); }\n```\n\n" +
                "Kết luận: giữ RAM rảnh dưới 45 MB, CPU rảnh 0 %, độ trễ đánh thức dưới 10 ms. Tiếng Việt có dấu: ắ ằ ẳ ẵ ặ ố ồ ổ ỗ ộ ứ ừ ử ữ ự.");
            var phases = new List<object>();
            CockpitShellWindow? w = null;
            bool shown = false;
            int seeded = 0;
            string? showError = null;
            try
            {
                runtime.QuotaGuard.ResetSlidingWindows();
                phases.Add(Phase("baseline"));
                // CI only (GITHUB_ACTIONS): 20 answered non-test rows, like a real Q&A history, on the runner's throw-away
                // database. The user's database is never changed by this selftest.
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") seeded = SeedHistory(runtime, 20);
                w = new CockpitShellWindow(runtime);
                phases.Add(Phase("created"));
                try
                {
                    w.Left = -4000; // off screen: the CI desktop and the user's screens stay untouched
                    w.Top = 0;
                    w.ShowActivated = false;
                    w.Show();
                    shown = true;
                }
                catch (Exception ex) { showError = ex.GetType().Name + ": " + ex.Message; }
                w.ApplyUiFromPipe("half");
                Pump(600);
                phases.Add(Phase("shownHalf"));
                w.ApplyUiFromPipe("full");
                Pump(600);
                phases.Add(Phase("full"));
                w.ExecuteFromPipe("/ask! Gate 16c footprint " + Guid.NewGuid().ToString("N").Substring(0, 8));
                Pump(1500);
                w.ExecuteFromPipe("/calc sqrt(144)+2^8");
                w.ExecuteFromPipe("/calc 12*12");
                w.HistoryFromPipe("refresh");
                Pump(600);
                phases.Add(Phase("answer"));
                w.LazyFromPipe("f1");          // F1 board open ...
                Pump(300);
                w.LazyFromPipe("f1");          // ... and closed
                w.AllianceFromPipe("toggle");  // Alt+A board open ...
                Pump(300);
                w.AllianceFromPipe("toggle");  // ... and closed
                Pump(300);
                phases.Add(Phase("boardsClosed"));
                // Verify-Task16b E3 / E5 in small: browse the whole Q&A history (3 rounds), back to the live answer, PASSIVE, then
                // wait for the idle trim (5 s timer) exactly as Verify measures its settled idle Private WS
                w.HistoryFromPipe("refresh");
                Pump(600);
                for (int round = 0; round < 3; round++)
                {
                    for (int i = 0; i < 20; i++) w.HistoryFromPipe(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                w.HistoryFromPipe("live");
                Pump(300);
                phases.Add(Phase("history20"));
                w.ReturnToPassive("selftest");
                Pump(6500);
                phases.Add(Phase("idleTrimmed"));
                for (int i = 0; i < 3; i++) _ = w.GetUiStateJson();
                Pump(200);
                phases.Add(Phase("uistate"));
                w.ApplyUiFromPipe("half");
                if (shown) w.Hide();
                Pump(1000);
                phases.Add(Phase("hidden"));
            }
            finally
            {
                try { w?.Close(); } catch (InvalidOperationException) { }
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, oldMock);
            }
            return (new { scenario = "ui-footprint", pass = phases.Count == 10, shown, showError, seeded, phases }, phases.Count == 10);

            static object Phase(string name)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                Pump(150);
                long? pws = MemoryDiagnostics.PrivateWorkingSetBytes();
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                using Process p = Process.GetCurrentProcess();
                return new
                {
                    phase = name,
                    privateWsMb = pws.HasValue ? Math.Round(pws.Value / 1048576.0, 2) : (double?)null,
                    gcHeapMb = Math.Round(gc.HeapSizeBytes / 1048576.0, 2),
                    gcCommittedMb = Math.Round(gc.TotalCommittedBytes / 1048576.0, 2),
                    commitMb = Math.Round(p.PrivateMemorySize64 / 1048576.0, 2),
                    jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(false),
                    jitIlKb = Math.Round(System.Runtime.JitInfo.GetCompiledILBytes(false) / 1024.0, 1),
                    handles = p.HandleCount,
                    threads = p.Threads.Count
                };
            }

            static int SeedHistory(NexusRuntime runtime, int count)
            {
                var processor = new CockpitCommandProcessor(runtime);
                string answer = "**Trả lời mẫu** cho lịch sử: dùng `Span<T>` và `ArrayPool<byte>` để giảm cấp phát.\n\n- Ý một\n- Ý hai\n\n" +
                                "```csharp\nvar x = ArrayPool<byte>.Shared.Rent(1024);\n```\n\nTiếng Việt có dấu: ắ ằ ẳ ẵ ặ ố ồ ổ ỗ ộ ứ ừ ử ữ ự. " + new string('x', 400);
                int done = 0;
                using var connection = runtime.Store.OpenHeldConnection();
                for (int i = 0; i < count; i++)
                {
                    CockpitCommandResult r = processor.Execute($"Câu hỏi lịch sử mẫu số {i + 1}: tối ưu RAM của Cockpit thế nào", IntPtr.Zero);
                    if (r.Dispatch == null) continue;
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "UPDATE agent_dispatch_logs SET ai_response = $a, ai_model = 'gemini-3.5-flash-lite', is_test = 0 WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$a", answer);
                    cmd.Parameters.AddWithValue("$id", r.Dispatch.DispatchId);
                    done += cmd.ExecuteNonQuery();
                }
                return done;
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
