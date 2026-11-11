using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-17 (ADR-67) /net radar + FIFINE mic selector + PTT VAD + clipboard worker ─────────────────────────────

        private const double NetPortCheckMaxMs = 15.0;   // spec E2: /net port and get_network_radar answer within 15 ms
        private const double NetPingCutMaxMs = 300.0;    // spec E2: a dead host is reported within 300 ms (200 ms cut + scheduling)
        private const double ClipboardWorkerMaxMs = 300.0; // decision 3.7: the caller waits at most 200 ms (+ thread start)

        /// <summary>
        /// TASK-17 "net-ptt" (no live Gemini call, no microphone opened, the user's cockpit_ui.json untouched):
        ///   E2  adapter pick (physical + gateway 192.168.1.1, Radmin 26.x skipped), a TcpListener on an ephemeral loopback port seen
        ///       LISTENING by /net port and by the radar port filter in &lt; 15 ms, FREE after Stop; a TEST-NET host times out in
        ///       &lt;= 300 ms, loopback answers; a scan with NITRO5 on TEST-NET = OFFLINE (AWAY), on loopback = ONLINE; /net status
        ///       through the command processor finishes in the background with the Radar text
        ///   E3  PreferredMicSelector: iVCam default + FIFINE → FIFINE, iVCam ignored; no FIFINE → the physical mic; /mic select
        ///       (temp cockpit_ui.json) wins; only virtual mics → none; /mic through the processor with a fixed device list
        ///   E4  VAD on synthetic 16 kHz PCM: silence False, 440 Hz True, 1 kHz True, white noise False; every unmanaged capture
        ///       buffer freed (ledger back to 0) also when cancelled; one session at a time
        ///   3.7 clipboard: a hung clipboard owner (thread that does not pump) makes Copy return within 300 ms instead of hanging;
        ///       skipped (reported) when this desktop gives no clipboard
        /// </summary>
        private static (object Report, bool Pass) NetPtt(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            object net = NetChecks(runtime, checks);
            object mic = MicChecks(runtime, checks);
            object vad = VadChecks(runtime, checks);
            object clipboard = ClipboardWorkerCheck(checks);
            bool pass = checks.Values.All(v => v);
            return (new { scenario = "net-ptt", pass, failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(), checks, net, mic, vad, clipboard }, pass);
        }

        private static object NetChecks(NexusRuntime runtime, Dictionary<string, bool> checks)
        {
            // adapter pick on a synthetic list shaped like Nhat Hoa's PC (Radmin VPN + Ethernet + a Hyper-V switch)
            var nics = new List<NetInterfaceInfo>
            {
                new("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", "Ethernet", "26.253.18.21", "26.0.0.1", true, NetworkRadar.VirtualReason("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", "26.253.18.21")),
                new("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "Ethernet", "172.20.16.1", null, true, "vEthernet"),
                new("Ethernet", "Realtek PCIe GbE Family Controller", "Ethernet", "192.168.1.212", "192.168.1.1", false, null)
            };
            NetInterfaceInfo? picked = NetworkRadar.PickActive(nics);
            checks["e2.nicPick"] = picked?.IPv4 == "192.168.1.212" && NetworkRadar.VirtualReason("Radmin VPN", "x", "26.253.18.21") != null &&
                                   NetworkRadar.VirtualReason("Ethernet", "Killer E2600 Gigabit Ethernet Controller", "192.168.1.22") == null;

            // ephemeral loopback listener: LISTENING, then FREE
            NetworkRadar.CheckPort(1); // warm the IP helper tables (first call loads iphlpapi)
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            NetPortCheck open;
            double radarMs;
            bool radarSeesPort;
            CockpitCommandResult viaCommand;
            try
            {
                open = NetworkRadar.CheckPort(port);
                long t0 = Stopwatch.GetTimestamp();
                var (top, _, _, _) = NetworkRadar.ReadPorts(NetworkRadar.TopPortCount, port);
                radarMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
                radarSeesPort = top.Any(p => p.Port == port && p.State == "LISTENING");
                viaCommand = new CockpitCommandProcessor(runtime).Execute($"/net port {port}", IntPtr.Zero);
            }
            finally
            {
                listener.Stop();
            }
            NetPortCheck closed = NetworkRadar.CheckPort(port);
            checks["e2.portListening"] = open.State == "LISTENING" && open.TcpListening && open.ElapsedMs < NetPortCheckMaxMs;
            checks["e2.radarPortFilter"] = radarSeesPort && radarMs < NetPortCheckMaxMs;
            checks["e2.portFree"] = closed.State == "FREE" && !closed.TcpListening;
            checks["e2.commandPort"] = viaCommand.Kind == "net" && viaCommand.Ok && viaCommand.Message.Contains("LISTENING", StringComparison.Ordinal) &&
                                       viaCommand.Local?.Snapshot?.TaskSummary == $"/net port {port}" && viaCommand.Local.Snapshot.TotalTokens == 0;

            // ping: a TEST-NET-1 address never answers (cut at 200 ms), loopback does
            NetPingSample dead = NetworkRadar.PingOnceAsync("TEST", "192.0.2.1").GetAwaiter().GetResult();
            NetPingSample loop = NetworkRadar.PingOnceAsync("LOOP", "127.0.0.1").GetAwaiter().GetResult();
            checks["e2.pingCut"] = !dead.Ok && dead.ElapsedMs <= NetPingCutMaxMs;
            checks["e2.pingLoopback"] = loop.Ok;

            // status scans: NITRO5 away (TEST-NET) and at home (loopback)
            var away = new[] { new NetworkRadar.Target("GW", "127.0.0.1"), new NetworkRadar.Target("NITRO5", "192.0.2.22"),
                               new NetworkRadar.Target("NITRO5-WIFI", "192.0.2.192"), new NetworkRadar.Target("TV", "192.0.2.97") };
            NetRadarSnapshot awayScan = runtime.Network.ScanAsync(away).GetAwaiter().GetResult();
            var home = new[] { new NetworkRadar.Target("GW", "127.0.0.1"), new NetworkRadar.Target("NITRO5", "127.0.0.1"),
                               new NetworkRadar.Target("NITRO5-WIFI", "192.0.2.192"), new NetworkRadar.Target("TV", "192.0.2.97") };
            NetRadarSnapshot homeScan = runtime.Network.ScanAsync(home).GetAwaiter().GetResult();
            checks["e2.nitro5Away"] = awayScan.Nitro5State == NetworkRadar.Nitro5Offline && awayScan.PingMs <= NetPingCutMaxMs &&
                                      NetworkRadar.Nitro5Line(awayScan).Contains(NetworkRadar.Nitro5Offline, StringComparison.Ordinal);
            checks["e2.nitro5Home"] = homeScan.Nitro5State.StartsWith("ONLINE", StringComparison.Ordinal) && homeScan.Nitro5Address == "127.0.0.1" &&
                                      ReferenceEquals(runtime.Network.Last, homeScan);
            string card = NetworkRadar.Describe(awayScan);
            checks["e2.radarCard"] = card.Contains("NIC", StringComparison.Ordinal) && card.Contains("PING", StringComparison.Ordinal) &&
                                     card.Contains("NITRO5", StringComparison.Ordinal) && card.Contains("PORT", StringComparison.Ordinal);

            // /net through the processor: returns at once, the radar finishes on a pool thread (real LAN targets of this machine)
            var sw = Stopwatch.StartNew();
            CockpitCommandResult status = new CockpitCommandProcessor(runtime).Execute("/net", IntPtr.Zero);
            double returnMs = sw.Elapsed.TotalMilliseconds;
            LocalUtilityResult? finished = null;
            bool done = status.LocalTask != null && status.LocalTask.Wait(3000);
            if (done) finished = status.LocalTask!.Result;
            checks["e2.commandStatusAsync"] = status.Kind == "net" && returnMs < 50 && done && finished != null &&
                                              finished.Snapshot?.TaskSummary == "/net status" && finished.Message.Contains("NITRO5", StringComparison.Ordinal) &&
                                              finished.Data is NetRadarSnapshot real && real.PingMs <= NetPingCutMaxMs + 200;
            NetRadarSnapshot? realScan = finished?.Data as NetRadarSnapshot;
            return new
            {
                picked = picked?.Name,
                port,
                open,
                radarMs,
                closed,
                commandPortMessage = viaCommand.Message,
                dead,
                loop,
                awayScan = new { awayScan.Nitro5State, awayScan.PingMs, awayScan.TotalMs, line = NetworkRadar.Nitro5Line(awayScan) },
                homeScan = new { homeScan.Nitro5State, homeScan.Nitro5RttMs, homeScan.PingMs, line = NetworkRadar.Nitro5Line(homeScan) },
                card,
                commandStatus = new { returnMs = Math.Round(returnMs, 2), done, message = finished?.Message, thisMachine = realScan == null ? null : new { nic = realScan.ActiveNic?.Name, realScan.PingMs, realScan.PortsMs, realScan.TotalMs, realScan.Nitro5State } }
            };
        }

        private static object MicChecks(NexusRuntime runtime, Dictionary<string, bool> checks)
        {
            static MicDevice Dev(string name, bool isDefault, int wave) =>
                new($"{{0.0.1.00000000}}.{{{wave:D8}-0000-0000-0000-000000000000}}", name, isDefault, PreferredMicSelector.VirtualReason(name) != null, PreferredMicSelector.VirtualReason(name), wave);
            var pc = new List<MicDevice>
            {
                Dev("e2eSoft iVCam", true, 0),
                Dev("Microphone (FIFINE K669 Microphone)", false, 1),
                Dev("Microphone (Realtek(R) Audio)", false, 2),
                Dev("Steam Streaming Microphone", false, 3)
            };
            MicSelection s1 = PreferredMicSelector.Select(pc, "FIFINE", null);
            checks["e3.fifineFirst"] = s1.Selected?.Name == "Microphone (FIFINE K669 Microphone)" &&
                                       s1.Ignored.Any(d => d.Name == "e2eSoft iVCam") && s1.Ignored.Any(d => d.Name == "Steam Streaming Microphone");
            MicSelection s2 = PreferredMicSelector.Select(pc.Where(d => !d.Name.Contains("FIFINE", StringComparison.Ordinal)).ToList(), "FIFINE", null);
            checks["e3.noFifinePhysical"] = s2.Selected?.Name == "Microphone (Realtek(R) Audio)" && s2.Ignored.Any(d => d.Name == "e2eSoft iVCam");
            MicSelection s3 = PreferredMicSelector.Select(pc, "FIFINE", "Realtek");
            checks["e3.forcedWins"] = s3.Selected?.Name == "Microphone (Realtek(R) Audio)";
            MicSelection s4 = PreferredMicSelector.Select(new[] { Dev("e2eSoft iVCam", true, 0), Dev("CABLE Virtual Audio Device", false, 1) }, "FIFINE", null);
            checks["e3.onlyVirtualNone"] = s4.Selected == null && s4.Ignored.Count == 2;
            checks["e3.denylist"] = new[] { "e2eSoft iVCam", "DroidCam Virtual Audio", "Steam Streaming Microphone", "Stereo Mix (Realtek(R) Audio)", "Virtual Audio Cable" }
                                        .All(n => PreferredMicSelector.VirtualReason(n) != null) &&
                                    PreferredMicSelector.VirtualReason("Microphone (FIFINE K669 Microphone)") == null;

            // /mic select on a temp cockpit_ui.json (the user's file is never written by this selftest)
            string temp = Path.Combine(Path.GetTempPath(), "nexus_task17_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            object selectReport;
            try
            {
                var store = new CockpitUiStore(temp);
                var mic = new MicService(store) { DeviceListOverride = () => pc };
                (bool okSel, string msgSel) = mic.Select("2");
                MicSelection afterSelect = mic.Inventory().Selection;
                string? savedAfterSelect = store.Load().MicDevice;
                (bool okAuto, _) = mic.Select("auto");
                MicSelection afterAuto = mic.Inventory().Selection;
                checks["e3.selectPersists"] = okSel && savedAfterSelect == "Microphone (FIFINE K669 Microphone)" &&
                                              afterSelect.Selected?.Name == "Microphone (FIFINE K669 Microphone)" && afterSelect.Reason.Contains("/mic select", StringComparison.Ordinal) &&
                                              okAuto && store.Load().MicDevice == null && afterAuto.Reason.Contains("FIFINE", StringComparison.Ordinal);
                (bool okReal, string msgReal) = mic.Select("Realtek");
                byte[] head = File.Exists(store.Path) ? File.ReadAllBytes(store.Path) : Array.Empty<byte>();
                bool bom = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                checks["e3.selectByName"] = okReal && mic.Inventory().Selection.Selected?.Name == "Microphone (Realtek(R) Audio)" && head.Length > 0 && !bom;
                selectReport = new { msgSel, msgReal, saved = store.Load().MicDevice };
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            // /mic through the processor with the PC-shaped list (status text of the spec)
            Func<IReadOnlyList<MicDevice>>? oldOverride = runtime.Mic.DeviceListOverride;
            runtime.Mic.DeviceListOverride = () => pc;
            CockpitCommandResult status;
            CockpitCommandResult list;
            try
            {
                status = new CockpitCommandProcessor(runtime).Execute("/mic", IntPtr.Zero);
                list = new CockpitCommandProcessor(runtime).Execute("/mic list", IntPtr.Zero);
            }
            finally
            {
                runtime.Mic.DeviceListOverride = oldOverride;
            }
            string expectedPreferred = runtime.UiLayout.Load().PreferredMicSubstring;
            checks["e3.commandStatus"] = status.Kind == "mic" &&
                                         (expectedPreferred != PreferredMicSelector.DefaultPreferredSubstring || !string.IsNullOrEmpty(runtime.UiLayout.Load().MicDevice) ||
                                          (status.Message.Contains("[ACTIVE MIC] Microphone (FIFINE K669 Microphone)", StringComparison.Ordinal) &&
                                           status.Message.Contains("e2eSoft iVCam [Ignored Virtual Mic]", StringComparison.Ordinal) &&
                                           status.Message.Contains("16000 Hz, 16-bit, Mono PCM", StringComparison.Ordinal)));
            checks["e3.commandList"] = list.Message.Contains("[Virtual-Ignored]", StringComparison.Ordinal) && list.Local?.TotalTokens == 0;

            (MicSelection real, string? realError, double realMs) = runtime.Mic.Inventory();
            return new
            {
                selected = s1.Selected?.Name,
                ignored = s1.Ignored.Select(d => d.Name).ToArray(),
                noFifine = s2.Selected?.Name,
                forced = s3.Selected?.Name,
                select = selectReport,
                statusMessage = status.Message,
                listMessage = list.Message,
                thisMachine = new { devices = real.All.Select(d => new { d.Name, d.IsDefault, d.Virtual, d.WaveInIndex }).ToArray(), selected = real.Selected?.Name, real.Reason, error = realError, ms = realMs }
            };
        }

        private static object VadChecks(NexusRuntime runtime, Dictionary<string, bool> checks)
        {
            var mic = new MicService(new CockpitUiStore(Path.GetTempPath()));
            long allocationsBefore = PcmBufferLedger.Allocations;
            PttResult silence = mic.Run(new SyntheticPcmSource("silence"), "test", 2, CancellationToken.None);
            PttResult tone440 = mic.Run(new SyntheticPcmSource("tone", 440, 0.25), "test", 2, CancellationToken.None);
            PttResult tone1k = mic.Run(new SyntheticPcmSource("tone", 1000, 0.25), "test", 2, CancellationToken.None);
            PttResult noise = mic.Run(new SyntheticPcmSource("noise", 0, 0.25), "test", 2, CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            PttResult stopped = mic.Run(new SyntheticPcmSource("tone", 440, 0.25), "test", 10, cancelled.Token);
            checks["e4.silenceNoVad"] = silence.Ok && !silence.Vad.Vad && silence.Vad.VoicedFrames == 0 && silence.Samples == 32000;
            checks["e4.tone440Vad"] = tone440.Ok && tone440.Vad.Vad && Math.Abs(tone440.Vad.AvgRmsDbfs - (-15.1)) < 1.0;
            checks["e4.tone1kVad"] = tone1k.Ok && tone1k.Vad.Vad;
            checks["e4.noiseNoVad"] = noise.Ok && !noise.Vad.Vad && noise.Vad.AvgZcr > VadAnalyzer.MaxZcr;
            checks["e4.buffersFreed"] = PcmBufferLedger.OutstandingBytes == 0 && PcmBufferLedger.Allocations > allocationsBefore &&
                                        new[] { silence, tone440, tone1k, noise, stopped }.All(r => r.OutstandingBytesAfter == 0);
            checks["e4.cancelFrees"] = stopped.Cancelled && stopped.Samples == 0 && stopped.OutstandingBytesAfter == 0;
            checks["e4.sttStub"] = tone440.SttProvider == "LocalVadTranscriptStub" && tone440.Transcript.Contains("0 token", StringComparison.Ordinal);

            // one session at a time: a second Run while the first is inside Capture is refused
            var gate = new ManualResetEventSlim(false);    // not disposed: a stuck pool thread may still touch them
            var release = new ManualResetEventSlim(false);
            var blocking = new BlockingPcmSource(gate, release);
            Task<PttResult> first = Task.Run(() => mic.Run(blocking, "test", 2, CancellationToken.None));
            bool entered = gate.Wait(2000);
            PttResult second = mic.Run(new SyntheticPcmSource("silence"), "test", 1, CancellationToken.None);
            release.Set();
            bool firstDone = first.Wait(3000);
            checks["e4.oneSession"] = entered && !second.Ok && (second.Error ?? string.Empty).Contains("phiên", StringComparison.Ordinal) && firstDone && first.Result.Ok;
            return new
            {
                silence = silence.Vad,
                tone440 = tone440.Vad,
                tone1k = tone1k.Vad,
                noise = noise.Vad,
                stopped = new { stopped.Cancelled, stopped.Samples, stopped.OutstandingBytesAfter },
                ledger = new { outstanding = PcmBufferLedger.OutstandingBytes, allocations = PcmBufferLedger.Allocations - allocationsBefore },
                message = MicService.DescribeResult(tone440),
                secondError = second.Error
            };
        }

        /// <summary>Source that waits inside Capture until the test lets it go (one-session guard).</summary>
        private sealed class BlockingPcmSource : IPcmSource
        {
            private readonly ManualResetEventSlim _entered;
            private readonly ManualResetEventSlim _release;

            public BlockingPcmSource(ManualResetEventSlim entered, ManualResetEventSlim release)
            {
                _entered = entered;
                _release = release;
            }

            public string Name => "blocking";

            public PcmCaptureStats Capture(TimeSpan duration, CancellationToken cancel, PcmChunkHandler onSamples)
            {
                _entered.Set();
                _release.Wait(3000);
                return new PcmCaptureStats(true, null, 0, 0, 0, 0, 0, 0);
            }
        }

        // ---- decision 3.7: a clipboard owner that does not pump must not hang the caller ----

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "OpenClipboard")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TestOpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "EmptyClipboard")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TestEmptyClipboard();

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "CloseClipboard")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TestCloseClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMsg msg, IntPtr hwnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMsg msg);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        private static readonly IntPtr HwndMessage = new(-3);

        private static void PumpPending()
        {
            while (PeekMessage(out NativeMsg m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */)) DispatchMessage(ref m);
        }

        private static object ClipboardWorkerCheck(Dictionary<string, bool> checks)
        {
            IntPtr ownerBefore = ClipboardService.OwnerWindow;
            var ready = new ManualResetEventSlim(false); // not disposed: the owner thread may still set it after a time-out
            string? setupError = null;
            const int HangMs = 1200;
            var hung = new Thread(() =>
            {
                IntPtr hwnd = CreateWindowEx(0, "STATIC", "nexus-task17-hung-owner", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) { setupError = $"CreateWindowEx {Marshal.GetLastWin32Error()}"; ready.Set(); return; }
                try
                {
                    // become the clipboard owner (EmptyClipboard with our window), then stop pumping: a "hung" owner
                    if (!TestOpenClipboard(hwnd)) { setupError = $"OpenClipboard {Marshal.GetLastWin32Error()}"; ready.Set(); return; }
                    bool emptied = TestEmptyClipboard();
                    TestCloseClipboard();
                    if (!emptied) { setupError = "EmptyClipboard failed"; ready.Set(); return; }
                    ready.Set();
                    Thread.Sleep(HangMs);
                    PumpPending(); // answers WM_DESTROYCLIPBOARD now: the stuck worker can finish
                    Thread.Sleep(100);
                    PumpPending();
                }
                finally
                {
                    DestroyWindow(hwnd);
                }
            }) { IsBackground = true, Name = "task17 hung clipboard owner" };
            hung.SetApartmentState(ApartmentState.STA);
            hung.Start();
            if (!ready.Wait(3000)) setupError ??= "owner setup took > 3 s (the current clipboard owner did not answer)";
            if (setupError != null)
            {
                hung.Join(3000);
                checks["c37.clipboardWorker"] = true; // no usable clipboard on this desktop: reported, not graded
                return new { skipped = true, setupError };
            }
            IntPtr caller = CreateWindowEx(0, "STATIC", "nexus-task17-caller", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                ClipboardService.OwnerWindow = caller;
                int timeoutsBefore = ClipboardService.WorkerTimeouts;
                var sw = Stopwatch.StartNew();
                ClipboardCopyResult whileHung = ClipboardService.Copy("NEXUS task17 clipboard worker test");
                double hungMs = sw.Elapsed.TotalMilliseconds;
                bool timedOut = ClipboardService.LastWorkerTimedOut;
                hung.Join(HangMs + 2000);
                Thread.Sleep(150);
                ClipboardCopyResult after = ClipboardService.Copy("NEXUS task17 clipboard after");
                checks["c37.clipboardWorker"] = hungMs <= ClipboardWorkerMaxMs && timedOut && !whileHung.SystemClipboard &&
                                                ClipboardService.WorkerTimeouts == timeoutsBefore + 1 && after.SystemClipboard;
                return new
                {
                    skipped = false,
                    hungMs = Math.Round(hungMs, 2),
                    timedOut,
                    whileHung = new { whileHung.SystemClipboard, whileHung.Error },
                    after = new { after.SystemClipboard, after.Error, after.ElapsedMs }
                };
            }
            finally
            {
                ClipboardService.OwnerWindow = ownerBefore;
                if (caller != IntPtr.Zero) DestroyWindow(caller);
            }
        }
    }
}
