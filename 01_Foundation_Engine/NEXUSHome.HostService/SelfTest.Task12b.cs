using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ─────────────── TASK-12b (ADR-09 / ADR-61 dual envelope: settled idle WS + transient burst, LOH slimming) ───────────────

        /// <summary>TASK-12b: what the zero-copy decode of an image held in unmanaged memory produced (vision selftest).</summary>
        internal sealed record UnmanagedDecode(string PngSha, int PngBytes, string DibSha, int DibBytes, int DibWidth, int DibHeight,
            long DibBlockBytes, bool DibSameAsFile);

        /// <summary>
        /// Settled growth that still fits the live target when added to the Gate 13 idle (45.0 - 40.7 = 4.3 MB). Reported only:
        /// the headless process is not the WPF Cockpit, and the live median of Verify-Task12 E5 (&lt;= 45.0 MB) is the gate.
        /// </summary>
        private const double VisionSettledDeltaMaxMb = 4.3;

        /// <summary>No-leak criterion: Private WS after the LOH compaction of the last 3 loads spans at most this much.</summary>
        private const double VisionPlateauMaxSpreadMb = 1.0;
        private const int VisionMemLoads2K = 6; // TASK-12c: Gate 12 E5 asks for >= 6 images in a row
        private const int VisionMemWarmLoads2K = 3; // TASK-12c: one-time HostService growth goes into the baseline

        /// <summary>TASK-12c, visionWorker = process: settled growth allowed in the HostService (the GDI+ work is in the child).</summary>
        private const double VisionSettledDeltaMaxMbProcess = 2.0;
        private const int VisionMemSettleSeconds = 10;
        private const int VisionMemSamples = 8;
        private const int VisionMemSampleIntervalMs = 250;

        /// <summary>
        /// The clipboard path without the clipboard: the PNG file bytes and a bottom-up 24-bit CF_DIB of the same image are put in
        /// unmanaged memory (AllocHGlobal, like the GlobalLock block) and decoded in place by DecodeAndScaleUnmanaged.
        /// </summary>
        private static UnmanagedDecode DecodeFromUnmanaged(string pngPath, NexusConfig config)
        {
            byte[] png = File.ReadAllBytes(pngPath);
            IntPtr pngBlock = Marshal.AllocHGlobal(png.Length);
            VisionImage fromPng;
            try
            {
                Marshal.Copy(png, 0, pngBlock, png.Length);
                using ScaledImage scaled = VisionImageProcessor.DecodeAndScaleUnmanaged(pngBlock, png.Length, false, "test-png",
                    config.MaxVisionDimensionPx, config.MaxSourceDimensionPx);
                fromPng = VisionImageProcessor.EncodeUnderBudget(scaled, config.MaxVisionImageBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(pngBlock);
            }

            (IntPtr dibBlock, long dibLength) = BuildDib(pngPath);
            VisionImage fromDib;
            try
            {
                using ScaledImage scaled = VisionImageProcessor.DecodeAndScaleUnmanaged(dibBlock, dibLength, true, "test-cf_dib",
                    config.MaxVisionDimensionPx, config.MaxSourceDimensionPx);
                fromDib = VisionImageProcessor.EncodeUnderBudget(scaled, config.MaxVisionImageBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(dibBlock);
            }
            return new UnmanagedDecode(fromPng.Sha256Hex, fromPng.ByteCount, fromDib.Sha256Hex, fromDib.ByteCount, fromDib.Width, fromDib.Height,
                dibLength, fromDib.Sha256Hex == fromPng.Sha256Hex);
        }

        /// <summary>BITMAPINFOHEADER (40 bytes, BI_RGB, 24 bpp, bottom-up) + pixel rows, in AllocHGlobal memory (caller frees).</summary>
        private static (IntPtr Block, long Length) BuildDib(string imagePath)
        {
            using var source = new Bitmap(imagePath);
            using Bitmap rgb = source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format24bppRgb);
            int width = rgb.Width;
            int height = rgb.Height;
            int stride = ((width * 3) + 3) & ~3;
            long length = 40 + (long)stride * height;
            IntPtr block = Marshal.AllocHGlobal((IntPtr)length);
            BitmapData data = rgb.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                Marshal.WriteInt32(block, 0, 40);            // biSize
                Marshal.WriteInt32(block, 4, width);         // biWidth
                Marshal.WriteInt32(block, 8, height);        // biHeight > 0 = bottom-up
                Marshal.WriteInt16(block, 12, 1);            // biPlanes
                Marshal.WriteInt16(block, 14, 24);           // biBitCount
                Marshal.WriteInt32(block, 16, 0);            // BI_RGB
                Marshal.WriteInt32(block, 20, stride * height);
                for (int o = 24; o < 40; o += 4) Marshal.WriteInt32(block, o, 0);
                var row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, Math.Min(stride, Math.Abs(data.Stride)));
                    Marshal.Copy(row, 0, block + 40 + (height - 1 - y) * stride, stride);
                }
                return (block, length);
            }
            catch
            {
                Marshal.FreeHGlobal(block);
                throw;
            }
            finally
            {
                rgb.UnlockBits(data);
            }
        }

        private sealed record SettledRead(double? MedianMb, double? MinMb, double? MaxMb, IReadOnlyList<double> Samples, double TrimAgeSeconds);

        /// <summary>Settled Median Mode in-process: Cockpit-style idle trim, wait &gt;= 10 s, 8 reads 250 ms apart, median / min / max.</summary>
        private static SettledRead ReadSettled(string reason)
        {
            MemoryTrimResult trim = MemorySlimming.Trim(reason);
            DateTime trimAt = DateTime.Parse(trim.AtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Thread.Sleep(TimeSpan.FromSeconds(VisionMemSettleSeconds));
            double age = Math.Round((DateTime.UtcNow - trimAt.ToUniversalTime()).TotalSeconds, 1);
            var samples = new List<double>(VisionMemSamples);
            for (int i = 0; i < VisionMemSamples; i++)
            {
                if (i > 0) Thread.Sleep(VisionMemSampleIntervalMs);
                if (MemoryDiagnostics.PrivateWorkingSetMb() is double mb) samples.Add(mb);
            }
            if (samples.Count == 0) return new SettledRead(null, null, null, samples, age);
            List<double> sorted = samples.OrderBy(v => v).ToList();
            double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : Math.Round((sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2, 2);
            return new SettledRead(median, sorted[0], sorted[^1], samples, age);
        }

        /// <summary>
        /// TASK-12b / TASK-12c E2 / E5 (headless HostService process, without the WPF Cockpit):
        /// baseline = settled median after a small warm-up /img and 3 x /img 2K (one-time HostService growth: JIT, SQLite pages,
        /// pool threads; GDI+ loaded by the test images themselves);
        /// load = 6 x /img --fresh on the 2560x1440 noise PNG (8.4 MB) + 1 x pure-noise 1920x1080 through the real background
        /// path (burst sampler + LOH compaction after each), plus, in inproc mode only, 2 x the CF_DIB zero-copy path; then
        /// settled median again. PASS: every call compacted the LOH and reported a peak, the last 3 loads end on a plateau
        /// (within 1.0 MB), and with visionWorker = process every call ran in the worker and the settled growth is &lt;= 2.0 MB.
        /// Reported only: growth against 4.3 MB (45.0 - 40.7) and the peak against the soft 120 / 200 MB envelope.
        /// TASK-12b on the PC (inproc): a ~5 MB GDI+ native step at the 6th 2K image, the reason for the worker.
        /// </summary>
        private static (object, bool) VisionMem(NexusRuntime runtime)
        {
            runtime.WarmUp();
            string? savedMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, VisionMockAnswer);
            try
            {
                var processor = new CockpitCommandProcessor(runtime);
                NexusConfig config = runtime.Config.Current;
                bool processMode = config.VisionWorker == "process";
                string runId = DateTime.UtcNow.ToString("HHmmssfff");
                Directory.CreateDirectory(Gate12ImageFolder);
                string small = Path.Combine(Gate12ImageFolder, "gate12b_warm_640x360.png");
                string noise2k = Path.Combine(Gate12ImageFolder, "gate12_noise_2560x1440.png");
                string pure = Path.Combine(Gate12ImageFolder, "gate12_purenoise_1920x1080.png");
                VisionImageProcessor.WriteTestPng(small, 640, 360, 9, 6);
                if (!File.Exists(noise2k)) VisionImageProcessor.WriteTestPng(noise2k, 2560, 1440, 1201, 6);
                if (!File.Exists(pure)) VisionImageProcessor.WriteTestPng(pure, 1920, 1080, 77, 127);

                // Warm-up: the first /img loads System.Drawing / GDI+ and JITs the path; that code stays, so it is in the baseline.
                ConsultOutcome? warm = AwaitVision(processor.Execute($"/img --fresh --file \"{small}\" Gate 12b warm {runId}", IntPtr.Zero));
                (IntPtr warmDib, long warmLength) = BuildDib(small);
                try
                {
                    using ScaledImage s = VisionImageProcessor.DecodeAndScaleUnmanaged(warmDib, warmLength, true, "warm", config.MaxVisionDimensionPx, config.MaxSourceDimensionPx);
                    _ = VisionImageProcessor.EncodeUnderBudget(s, config.MaxVisionImageBytes);
                }
                finally
                {
                    Marshal.FreeHGlobal(warmDib);
                }
                // TASK-12c: 3 more 2K images before the baseline. The CI runner showed ~3.4 MB of one-time HostService growth over
                // the first 4 calls even with every image in the worker (SQLite pages, pool threads, JSON metadata), the same in
                // inproc mode; it then stays flat. With it in the baseline, the 6 measured images show what each image leaves.
                var warmBursts = new List<VisionBurstStats?>();
                for (int i = 0; i < VisionMemWarmLoads2K; i++)
                {
                    AwaitVision(processor.Execute($"/img --fresh --file \"{noise2k}\" Gate 12b warm 2K {i} {runId}", IntPtr.Zero));
                    warmBursts.Add(runtime.Vision.LastBurst);
                }
                SettledRead baseline = ReadSettled("TASK-12b baseline");
                GCMemoryInfo gcBefore = GC.GetGCMemoryInfo();
                // 4 warm-up + 7 measured misses exceed maxRequestsPerMinute (10, per process): open a new RPM window, as the
                // Task11 selftests do, so the load measures memory and not the quota guard.
                runtime.QuotaGuard.ResetSlidingWindows();

                // Load through the real background path; the whole load is also sampled every 100 ms from here.
                var bursts = new List<VisionBurstStats?>();
                var outcomes = new List<bool>();
                int compactsBefore = MemorySlimming.VisionCompactCount;
                double? loadPeak;
                int loadSamples;
                using (var loadSampler = new PrivateWsSampler(VisionConsultService.BurstSampleIntervalMs))
                {
                    for (int i = 0; i < VisionMemLoads2K; i++)
                    {
                        ConsultOutcome? o = AwaitVision(processor.Execute($"/img --fresh --file \"{noise2k}\" Gate 12b load {i} {runId}", IntPtr.Zero));
                        outcomes.Add(o != null && o.AiOk);
                        bursts.Add(runtime.Vision.LastBurst);
                    }
                    ConsultOutcome? po = AwaitVision(processor.Execute($"/img --fresh --file \"{pure}\" Gate 12b pure {runId}", IntPtr.Zero));
                    outcomes.Add(po != null && po.AiOk);
                    bursts.Add(runtime.Vision.LastBurst);
                    if (!processMode)
                    {
                        // inproc only: the zero-copy CF_DIB path decoded in this process (in process mode it runs in the worker).
                        for (int i = 0; i < 2; i++)
                        {
                            (IntPtr dib, long length) = BuildDib(noise2k);
                            try
                            {
                                using ScaledImage s = VisionImageProcessor.DecodeAndScaleUnmanaged(dib, length, true, "load-cf_dib", config.MaxVisionDimensionPx, config.MaxSourceDimensionPx);
                                VisionImage jpeg = VisionImageProcessor.EncodeUnderBudget(s, config.MaxVisionImageBytes);
                                outcomes.Add(jpeg.ByteCount <= config.MaxVisionImageBytes);
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(dib);
                            }
                        }
                        MemorySlimming.CompactAfterVision("TASK-12b direct CF_DIB load"); // what the background path does after each call
                    }
                    (_, loadPeak, _, loadSamples) = loadSampler.Stop();
                }
                int compacts = MemorySlimming.VisionCompactCount - compactsBefore;
                GCMemoryInfo gcAfterLoad = GC.GetGCMemoryInfo();

                SettledRead settled = ReadSettled("TASK-12b after vision");
                GCMemoryInfo gcSettled = GC.GetGCMemoryInfo();
                double? delta = baseline.MedianMb is double b && settled.MedianMb is double a ? Math.Round(a - b, 2) : null;
                double? burstPeak = bursts.Where(x => x?.PeakMb != null).Select(x => x!.PeakMb).DefaultIfEmpty(null).Max();
                double? peakOverBaseline = loadPeak is double lp && baseline.MedianMb is double bm ? Math.Round(lp - bm, 1) : null;

                bool loadOk = outcomes.All(x => x) && warm != null && warm.AiOk;
                bool compactedOk = !MemorySlimming.Enabled || (compacts >= bursts.Count + (processMode ? 0 : 1) && bursts.All(x => x != null && x.Compacted));
                bool workerOk = !processMode || bursts.All(x => x != null && x.WorkerMode == "process" && x.WorkerExitCode == 0);
                bool peakReported = loadPeak != null && bursts.All(x => x != null && x.PeakMb != null && x.Samples >= 1);
                bool settledWithinTarget = delta is double d && d <= VisionSettledDeltaMaxMb;
                List<double> afterCompact = bursts.Where(x => x?.AfterCompactMb != null).Select(x => x!.AfterCompactMb!.Value).ToList();
                List<double> tail = afterCompact.Skip(Math.Max(0, afterCompact.Count - 3)).ToList();
                double? plateauSpread = tail.Count == 3 ? Math.Round(tail.Max() - tail.Min(), 2) : null;
                bool plateauOk = plateauSpread is double spread && spread <= VisionPlateauMaxSpreadMb;
                string envelope = loadPeak is not double pk ? "unknown" : pk > VisionBurstStats.SoftCeilingMb ? "over-soft-ceiling" : pk > VisionBurstStats.SoftWarnMb ? "soft-warn" : "ok";
                // TASK-12c: in process mode the HostService keeps no GDI+ residue, so its settled growth is gated too.
                bool processDeltaOk = !processMode || (delta is double pd && pd <= VisionSettledDeltaMaxMbProcess);
                var checks = new { loadOk, compactedOk, peakReported, plateauOk, workerOk, processDeltaOk };
                bool pass = loadOk && compactedOk && peakReported && plateauOk && workerOk && processDeltaOk;
                return (new
                {
                    scenario = "vision-mem",
                    pass,
                    checks,
                    slimmingEnabled = MemorySlimming.Enabled,
                    visionWorker = config.VisionWorker,
                    settledDeltaMaxMbProcess = VisionSettledDeltaMaxMbProcess,
                    baseline,
                    settled,
                    settledDeltaMb = delta,
                    settledDeltaMaxMb = VisionSettledDeltaMaxMb,
                    settledWithinTarget,
                    afterCompactMb = afterCompact,
                    plateauSpreadMb = plateauSpread,
                    plateauMaxSpreadMb = VisionPlateauMaxSpreadMb,
                    peak = new
                    {
                        loadPeakMb = loadPeak,
                        loadSamples,
                        burstPeakMb = burstPeak,
                        peakOverBaselineMb = peakOverBaseline,
                        softWarnMb = VisionBurstStats.SoftWarnMb,
                        softCeilingMb = VisionBurstStats.SoftCeilingMb,
                        envelope
                    },
                    bursts,
                    warmAfterCompactMb = warmBursts.Select(x => x?.AfterCompactMb).ToList(),
                    visionCompacts = compacts,
                    lastVisionCompact = MemorySlimming.LastVisionCompact,
                    gc = new
                    {
                        before = DescribeGc(gcBefore),
                        afterLoad = DescribeGc(gcAfterLoad),
                        settled = DescribeGc(gcSettled)
                    },
                    snapshot = MemoryDiagnostics.Capture(runtime.Registry, runtime.Grounding),
                    note = "Headless HostService (no WPF Cockpit): compare deltas, not the absolute MB, with the Cockpit numbers of Verify-Task12."
                }, pass);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, savedMock);
            }
        }

        private static object DescribeGc(GCMemoryInfo gc)
        {
            const double Mb = 1024.0 * 1024.0;
            ReadOnlySpan<GCGenerationInfo> gens = gc.GenerationInfo;
            return new
            {
                heapMb = Math.Round(gc.HeapSizeBytes / Mb, 2),
                committedMb = Math.Round(gc.TotalCommittedBytes / Mb, 2),
                fragmentedMb = Math.Round(gc.FragmentedBytes / Mb, 2),
                lohAfterMb = gens.Length > 3 ? Math.Round(gens[3].SizeAfterBytes / Mb, 2) : (double?)null,
                lohFragmentedMb = gens.Length > 3 ? Math.Round(gens[3].FragmentationAfterBytes / Mb, 2) : (double?)null,
                gcIndex = gc.Index,
                generation = gc.Generation,
                compacted = gc.Compacted
            };
        }
    }
}
