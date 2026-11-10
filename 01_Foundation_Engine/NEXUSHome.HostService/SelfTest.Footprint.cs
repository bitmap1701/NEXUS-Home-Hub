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
            string? showError = null;
            try
            {
                runtime.QuotaGuard.ResetSlidingWindows();
                phases.Add(Phase("baseline"));
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
            return (new { scenario = "ui-footprint", pass = phases.Count == 7, shown, showError, phases }, phases.Count == 7);

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
