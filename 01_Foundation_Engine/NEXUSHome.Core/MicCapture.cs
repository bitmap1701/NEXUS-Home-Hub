using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-17 V1: one active capture endpoint (Windows Core Audio name + id; waveIn index when it maps to one).</summary>
    public sealed record MicDevice(string Id, string Name, bool IsDefault, bool Virtual, string? VirtualReason, int? WaveInIndex);

    /// <summary>What <see cref="PreferredMicSelector"/> chose and why; <see cref="Ignored"/> = virtual mics left out.</summary>
    public sealed record MicSelection(MicDevice? Selected, string Reason, IReadOnlyList<MicDevice> Ignored, IReadOnlyList<MicDevice> All);

    /// <summary>
    /// TASK-17 V1 (ADR-67) PreferredMicSelector. On Nhat Hoa's PC the e2eSoft iVCam virtual camera takes the Windows default
    /// capture device while the real microphone is the FIFINE K669. Order: (1) the device forced with /mic select, (2) the first
    /// device whose name contains preferredMicSubstring (cockpit_ui.json, default "FIFINE"), (3) the Windows default when it is
    /// not a virtual mic, (4) the first physical device. Virtual mics (denylist below) are never picked automatically.
    /// </summary>
    public static class PreferredMicSelector
    {
        public const string DefaultPreferredSubstring = "FIFINE";

        /// <summary>Spec §3.2 denylist (case-insensitive substrings of the device name).</summary>
        public static readonly string[] VirtualDenylist = { "iVCam", "DroidCam", "Steam Streaming", "Stereo Mix", "Virtual Audio" };

        public static string? VirtualReason(string name)
        {
            foreach (string marker in VirtualDenylist)
            {
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return marker;
            }
            return null;
        }

        public static MicSelection Select(IReadOnlyList<MicDevice> devices, string? preferredSubstring, string? forcedName)
        {
            MicDevice? pick = null;
            string reason;
            if (!string.IsNullOrWhiteSpace(forcedName) &&
                (pick = devices.FirstOrDefault(d => d.Name.Equals(forcedName, StringComparison.OrdinalIgnoreCase))
                        ?? devices.FirstOrDefault(d => d.Name.Contains(forcedName!, StringComparison.OrdinalIgnoreCase))) != null)
            {
                reason = "đã chọn bằng /mic select";
            }
            else if (!string.IsNullOrWhiteSpace(preferredSubstring) &&
                     (pick = devices.FirstOrDefault(d => d.Name.Contains(preferredSubstring!, StringComparison.OrdinalIgnoreCase))) != null)
            {
                reason = $"khớp preferredMicSubstring \"{preferredSubstring}\"";
            }
            else if ((pick = devices.FirstOrDefault(d => d.IsDefault && !d.Virtual)) != null)
            {
                reason = "mặc định của Windows";
            }
            else if ((pick = devices.FirstOrDefault(d => !d.Virtual)) != null)
            {
                reason = "micro vật lý đầu tiên (mặc định của Windows là micro ảo)";
            }
            else
            {
                reason = devices.Count == 0 ? "không có thiết bị thu âm nào đang bật" : "chỉ có micro ảo (bị bỏ qua)";
            }
            MicDevice? selected = pick;
            List<MicDevice> ignored = devices.Where(d => d.Virtual && !ReferenceEquals(d, selected)).ToList();
            return new MicSelection(selected, reason, ignored, devices);
        }
    }

    /// <summary>Per-frame energy / zero-crossing summary of one capture (no audio is kept).</summary>
    public sealed record VadSummary(int Frames, int VoicedFrames, bool Vad, double AvgRmsDbfs, double PeakRmsDbfs, double AvgZcr,
        int LongestVoicedRun, double ThresholdDbfs);

    /// <summary>
    /// TASK-17 V1 energy + zero-crossing VAD on 16 kHz mono PCM16, 20 ms frames (320 samples). A frame is voiced when its RMS is at
    /// least <see cref="ThresholdDbfs"/> and its zero-crossing rate is in the speech band (0.01–0.30 crossings per sample: a 440 Hz
    /// tone is ≈ 0.055, 1 kHz ≈ 0.125, white noise ≈ 0.5). VAD = at least <see cref="MinVoicedRun"/> voiced frames in a row
    /// (100 ms). One 320-sample carry buffer, nothing else allocated per chunk.
    /// </summary>
    public sealed class VadAnalyzer
    {
        public const int FrameSamples = 320;
        public const double DefaultThresholdDbfs = -45.0;
        public const double MinZcr = 0.01;
        public const double MaxZcr = 0.30;
        public const int MinVoicedRun = 5;
        private const double SilenceDbfs = -120.0;

        private readonly short[] _carry = new short[FrameSamples];
        private int _carryCount;
        private int _frames;
        private int _voiced;
        private int _run;
        private int _longestRun;
        private double _sumDb;
        private double _peakDb = SilenceDbfs;
        private double _sumZcr;

        public VadAnalyzer(double thresholdDbfs = DefaultThresholdDbfs) => ThresholdDbfs = thresholdDbfs;

        public double ThresholdDbfs { get; }

        public void Process(ReadOnlySpan<short> samples)
        {
            int i = 0;
            if (_carryCount > 0)
            {
                int take = Math.Min(FrameSamples - _carryCount, samples.Length);
                samples.Slice(0, take).CopyTo(_carry.AsSpan(_carryCount));
                _carryCount += take;
                i = take;
                if (_carryCount < FrameSamples) return;
                Frame(_carry);
                _carryCount = 0;
            }
            for (; i + FrameSamples <= samples.Length; i += FrameSamples) Frame(samples.Slice(i, FrameSamples));
            int rest = samples.Length - i;
            if (rest > 0)
            {
                samples.Slice(i).CopyTo(_carry);
                _carryCount = rest;
            }
        }

        private void Frame(ReadOnlySpan<short> f)
        {
            double sumSq = 0;
            int crossings = 0;
            short prev = f[0];
            for (int k = 0; k < f.Length; k++)
            {
                short s = f[k];
                sumSq += (double)s * s;
                if (k > 0 && ((prev >= 0 && s < 0) || (prev < 0 && s >= 0))) crossings++;
                prev = s;
            }
            double rms = Math.Sqrt(sumSq / f.Length) / 32768.0;
            double db = rms > 0 ? Math.Max(SilenceDbfs, 20 * Math.Log10(rms)) : SilenceDbfs;
            double zcr = crossings / (double)(f.Length - 1);
            _frames++;
            _sumDb += db;
            _sumZcr += zcr;
            if (db > _peakDb) _peakDb = db;
            bool voiced = db >= ThresholdDbfs && zcr >= MinZcr && zcr <= MaxZcr;
            if (voiced)
            {
                _voiced++;
                _run++;
                if (_run > _longestRun) _longestRun = _run;
            }
            else
            {
                _run = 0;
            }
        }

        public VadSummary Summary() => new(_frames, _voiced, _longestRun >= MinVoicedRun,
            _frames > 0 ? Math.Round(_sumDb / _frames, 1) : SilenceDbfs, Math.Round(_peakDb, 1),
            _frames > 0 ? Math.Round(_sumZcr / _frames, 4) : 0, _longestRun, ThresholdDbfs);
    }

    /// <summary>Unmanaged capture memory in use right now (process-wide). After every session it must be back to 0 (Gate 17 E4).</summary>
    public static class PcmBufferLedger
    {
        private static long _outstanding;
        private static long _allocations;

        public static long OutstandingBytes => Interlocked.Read(ref _outstanding);

        public static long Allocations => Interlocked.Read(ref _allocations);

        public static IntPtr Alloc(int bytes)
        {
            IntPtr p = Marshal.AllocHGlobal(bytes);
            Interlocked.Add(ref _outstanding, bytes);
            Interlocked.Increment(ref _allocations);
            return p;
        }

        public static void Free(ref IntPtr p, int bytes)
        {
            if (p == IntPtr.Zero) return;
            Marshal.FreeHGlobal(p);
            p = IntPtr.Zero;
            Interlocked.Add(ref _outstanding, -bytes);
        }
    }

    public delegate void PcmChunkHandler(ReadOnlySpan<short> samples);

    /// <summary>16 kHz / 16-bit / mono source for one PTT session (the waveIn device, or a synthetic signal for tests).</summary>
    public interface IPcmSource
    {
        string Name { get; }

        /// <summary>Delivers chunks until <paramref name="duration"/> or cancellation; every buffer is freed before it returns.</summary>
        PcmCaptureStats Capture(TimeSpan duration, CancellationToken cancel, PcmChunkHandler onSamples);
    }

    public sealed record PcmCaptureStats(bool Ok, string? Error, long Samples, int Buffers, int BufferBytes, double OpenMs, double CaptureMs, double CloseMs);

    /// <summary>Speech-to-text hook (TASK-17 §3.2): offline / mock first. Whisper.net or a cloud STT can implement it later.</summary>
    public interface ISpeechToTextProvider
    {
        string Name { get; }

        /// <summary>True only when the provider needs the samples (the stub does not, so no audio is kept at all).</summary>
        bool NeedsAudio { get; }

        string Transcribe(VadSummary vad, ReadOnlySpan<short> audio);
    }

    /// <summary>Default provider: no model, no network, 0 tokens. It only reports what the VAD heard.</summary>
    public sealed class LocalVadTranscriptStub : ISpeechToTextProvider
    {
        public string Name => "LocalVadTranscriptStub";

        public bool NeedsAudio => false;

        public string Transcribe(VadSummary vad, ReadOnlySpan<short> audio) =>
            vad.Vad ? $"[STT stub] có giọng nói ({vad.VoicedFrames} khung × 20 ms), chưa gắn bộ nhận dạng — 0 token, không gọi mạng"
                    : "[STT stub] không có giọng nói — 0 token, không gọi mạng";
    }

    /// <summary>Result of /ptt test or one F1 "3. Mic 16kHz VAD" stream.</summary>
    public sealed record PttResult(
        bool Ok,
        string? Error,
        string Device,
        string SelectionReason,
        int SampleRate,
        int BitsPerSample,
        int Channels,
        double RequestedSeconds,
        double CapturedMs,
        long Samples,
        VadSummary Vad,
        string Transcript,
        string SttProvider,
        int Buffers,
        int BufferBytes,
        long OutstandingBytesAfter,
        bool Cancelled,
        double OpenMs,
        double CloseMs,
        string AtUtc);

    /// <summary>Synthetic 16 kHz source (selftests): silence, a sine tone or white noise, delivered in unmanaged buffers like waveIn.</summary>
    public sealed class SyntheticPcmSource : IPcmSource
    {
        private readonly string _kind;
        private readonly double _frequency;
        private readonly double _amplitude;

        public SyntheticPcmSource(string kind, double frequencyHz = 440, double amplitude = 0.25)
        {
            _kind = kind;
            _frequency = frequencyHz;
            _amplitude = amplitude;
        }

        public string Name => _kind == "tone" ? $"synthetic {_frequency:0} Hz" : $"synthetic {_kind}";

        public PcmCaptureStats Capture(TimeSpan duration, CancellationToken cancel, PcmChunkHandler onSamples)
        {
            const int chunk = 1600; // 100 ms, like the waveIn buffers
            const int buffers = 4;
            int bytes = chunk * 2;
            var data = new IntPtr[buffers];
            long total = (long)(duration.TotalSeconds * MicService.SampleRate);
            long produced = 0;
            var rng = new Random(17);
            var sw = Stopwatch.StartNew();
            try
            {
                for (int b = 0; b < buffers; b++) data[b] = PcmBufferLedger.Alloc(bytes);
                int next = 0;
                while (produced < total && !cancel.IsCancellationRequested)
                {
                    int n = (int)Math.Min(chunk, total - produced);
                    IntPtr p = data[next];
                    for (int k = 0; k < n; k++)
                    {
                        double v = _kind switch
                        {
                            "tone" => _amplitude * Math.Sin(2 * Math.PI * _frequency * (produced + k) / MicService.SampleRate),
                            "noise" => _amplitude * (rng.NextDouble() * 2 - 1),
                            _ => 0
                        };
                        Marshal.WriteInt16(p, k * 2, (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue));
                    }
                    unsafe
                    {
                        onSamples(new ReadOnlySpan<short>((void*)p, n));
                    }
                    produced += n;
                    next = (next + 1) % buffers;
                }
                return new PcmCaptureStats(true, null, produced, buffers, bytes, 0, sw.Elapsed.TotalMilliseconds, 0);
            }
            finally
            {
                for (int b = 0; b < buffers; b++) PcmBufferLedger.Free(ref data[b], bytes);
            }
        }
    }

    /// <summary>
    /// TASK-17 V1 capture through winmm waveIn (16 kHz, 16-bit, mono; Windows converts from the device format). Four 100 ms
    /// buffers in unmanaged memory, CALLBACK_EVENT, no extra thread. Every header is unprepared and every buffer freed before
    /// <see cref="Capture"/> returns, also on errors and cancellation (Gate 17 E4: 0 bytes outstanding).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WaveInPcmSource : IPcmSource
    {
        private const uint CallbackEvent = 0x00050000;
        private const int WhdrDone = 0x00000001;
        private const int ChunkSamples = 1600;
        private const int BufferCount = 4;

        private readonly int _deviceIndex;

        public WaveInPcmSource(int deviceIndex, string name)
        {
            _deviceIndex = deviceIndex;
            Name = name;
        }

        public string Name { get; }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public IntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr handle, UIntPtr deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr handle);

        public PcmCaptureStats Capture(TimeSpan duration, CancellationToken cancel, PcmChunkHandler onSamples)
        {
            int dataBytes = ChunkSamples * 2;
            int hdrBytes = Marshal.SizeOf<WaveHdr>();
            int flagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.Flags));
            int recordedOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.BytesRecorded));
            var data = new IntPtr[BufferCount];
            var headers = new IntPtr[BufferCount];
            var prepared = new bool[BufferCount];
            IntPtr handle = IntPtr.Zero;
            long samples = 0;
            double openMs = 0, captureMs = 0, closeMs = 0;
            string? error = null;
            var sw = Stopwatch.StartNew();
            using var ready = new AutoResetEvent(false);
            try
            {
                var format = new WaveFormatEx
                {
                    FormatTag = 1, Channels = 1, SamplesPerSec = MicService.SampleRate, AvgBytesPerSec = MicService.SampleRate * 2,
                    BlockAlign = 2, BitsPerSample = 16, Size = 0
                };
                int rc = waveInOpen(out handle, (UIntPtr)(uint)_deviceIndex, ref format, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
                if (rc != 0)
                {
                    handle = IntPtr.Zero;
                    return new PcmCaptureStats(false, $"waveInOpen lỗi {rc} (thiết bị {_deviceIndex}: {Name})", 0, 0, dataBytes, sw.Elapsed.TotalMilliseconds, 0, 0);
                }
                for (int b = 0; b < BufferCount; b++)
                {
                    data[b] = PcmBufferLedger.Alloc(dataBytes);
                    headers[b] = PcmBufferLedger.Alloc(hdrBytes);
                    Marshal.StructureToPtr(new WaveHdr { Data = data[b], BufferLength = (uint)dataBytes }, headers[b], false);
                    if (waveInPrepareHeader(handle, headers[b], (uint)hdrBytes) != 0) throw new InvalidOperationException("waveInPrepareHeader");
                    prepared[b] = true;
                    if (waveInAddBuffer(handle, headers[b], (uint)hdrBytes) != 0) throw new InvalidOperationException("waveInAddBuffer");
                }
                if (waveInStart(handle) != 0) throw new InvalidOperationException("waveInStart");
                openMs = sw.Elapsed.TotalMilliseconds;
                var capture = Stopwatch.StartNew();
                while (capture.Elapsed < duration && !cancel.IsCancellationRequested)
                {
                    ready.WaitOne(60);
                    for (int b = 0; b < BufferCount; b++)
                    {
                        int flags = Marshal.ReadInt32(headers[b], flagsOffset);
                        if ((flags & WhdrDone) == 0) continue;
                        int recorded = Marshal.ReadInt32(headers[b], recordedOffset);
                        if (recorded > 0)
                        {
                            unsafe
                            {
                                onSamples(new ReadOnlySpan<short>((void*)data[b], recorded / 2));
                            }
                            samples += recorded / 2;
                        }
                        Marshal.WriteInt32(headers[b], flagsOffset, flags & ~WhdrDone);
                        waveInAddBuffer(handle, headers[b], (uint)hdrBytes);
                    }
                }
                captureMs = capture.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                error = ex.Message;
            }
            finally
            {
                long tClose = Stopwatch.GetTimestamp();
                if (handle != IntPtr.Zero) waveInReset(handle); // every queued buffer comes back marked done
                for (int b = 0; b < BufferCount; b++)
                {
                    if (prepared[b] && handle != IntPtr.Zero) waveInUnprepareHeader(handle, headers[b], (uint)hdrBytes);
                    PcmBufferLedger.Free(ref headers[b], hdrBytes);
                    PcmBufferLedger.Free(ref data[b], dataBytes);
                }
                if (handle != IntPtr.Zero) waveInClose(handle);
                closeMs = (Stopwatch.GetTimestamp() - tClose) * 1000.0 / Stopwatch.Frequency;
            }
            return new PcmCaptureStats(error == null, error, samples, BufferCount, dataBytes, Math.Round(openMs, 2), Math.Round(captureMs, 1), Math.Round(closeMs, 2));
        }
    }

    /// <summary>Windows Core Audio endpoint list (names longer than waveIn's 31 characters) and the waveIn index of each.</summary>
    [SupportedOSPlatform("windows")]
    internal static class CoreAudioEndpoints
    {
        private const int EDataFlowCapture = 1;
        private const int ERoleConsole = 0;
        private const uint DeviceStateActive = 0x1;
        private const uint DrvQueryFunctionInstanceId = 0x0811;
        private const uint DrvQueryFunctionInstanceIdSize = 0x0812;
        private static readonly Guid FriendlyNameFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
        private const uint FriendlyNamePid = 14;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort VarType;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public IntPtr Pointer;
            public IntPtr Pointer2;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInMessage(IntPtr deviceId, uint message, IntPtr param1, IntPtr param2);

        /// <summary>Active capture endpoints with the default flag; errors leave an empty list (the message carries the reason).</summary>
        public static IReadOnlyList<MicDevice> List(out string? error)
        {
            error = null;
            var result = new List<MicDevice>();
            IMMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                string? defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(EDataFlowCapture, ERoleConsole, out IMMDevice def) == 0 && def != null)
                {
                    def.GetId(out defaultId);
                    Marshal.ReleaseComObject(def);
                }
                if (enumerator.EnumAudioEndpoints(EDataFlowCapture, DeviceStateActive, out IMMDeviceCollection collection) != 0 || collection == null)
                {
                    error = "EnumAudioEndpoints lỗi";
                    return result;
                }
                try
                {
                    Dictionary<string, int> waveIds = WaveInIndexById();
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out IMMDevice device) != 0 || device == null) continue;
                        try
                        {
                            device.GetId(out string id);
                            string name = FriendlyName(device) ?? id;
                            string? reason = PreferredMicSelector.VirtualReason(name);
                            result.Add(new MicDevice(id, name, id == defaultId, reason != null, reason,
                                waveIds.TryGetValue(id, out int wave) ? wave : null));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        private static string? FriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store) != 0 || store == null) return null;
            try
            {
                var key = new PropertyKey { FormatId = FriendlyNameFormat, PropertyId = FriendlyNamePid };
                if (store.GetValue(ref key, out PropVariant value) != 0) return null;
                try
                {
                    return value.VarType == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        /// <summary>waveIn device index → Core Audio endpoint id (DRV_QUERYFUNCTIONINSTANCEID), so the right device is opened.</summary>
        private static Dictionary<string, int> WaveInIndexById()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            uint n = waveInGetNumDevs();
            IntPtr sizePtr = Marshal.AllocHGlobal(4);
            try
            {
                for (uint i = 0; i < n; i++)
                {
                    Marshal.WriteInt32(sizePtr, 0);
                    if (waveInMessage((IntPtr)i, DrvQueryFunctionInstanceIdSize, sizePtr, IntPtr.Zero) != 0) continue;
                    int size = Marshal.ReadInt32(sizePtr);
                    if (size <= 0 || size > 4096) continue;
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (waveInMessage((IntPtr)i, DrvQueryFunctionInstanceId, buffer, (IntPtr)size) != 0) continue;
                        string? id = Marshal.PtrToStringUni(buffer);
                        if (!string.IsNullOrEmpty(id)) map[id] = (int)i;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sizePtr);
            }
            return map;
        }
    }

    /// <summary>
    /// TASK-17 V1 (ADR-67): /mic status | list | select, /ptt test [s] and the F1 switch "3. Mic 16kHz VAD". The microphone is
    /// opened only for an explicit session (never by Ctrl+Shift+Space, TASK-16c decision 4) and closed with every buffer freed
    /// when it ends; one session at a time.
    /// </summary>
    public sealed class MicService
    {
        public const int SampleRate = 16000;
        public const double DefaultTestSeconds = 2.0;
        public const double MaxSessionSeconds = 30.0;

        private readonly CockpitUiStore _ui;
        private int _busy;

        public MicService(CockpitUiStore ui, ISpeechToTextProvider? stt = null)
        {
            _ui = ui;
            Stt = stt ?? new LocalVadTranscriptStub();
        }

        public ISpeechToTextProvider Stt { get; }

        public bool Busy => Volatile.Read(ref _busy) != 0;

        /// <summary>Test hook (selftest E3): a fixed device list instead of the Windows one.</summary>
        public Func<IReadOnlyList<MicDevice>>? DeviceListOverride { get; set; }

        public PttResult? LastSession { get; private set; }

        public (MicSelection Selection, string? Error, double Ms) Inventory()
        {
            long t0 = Stopwatch.GetTimestamp();
            string? error = null;
            IReadOnlyList<MicDevice> devices;
            Func<IReadOnlyList<MicDevice>>? fixedList = DeviceListOverride;
            if (fixedList != null)
            {
                devices = fixedList();
            }
            else if (OperatingSystem.IsWindows())
            {
                devices = CoreAudioEndpoints.List(out error);
            }
            else
            {
                devices = Array.Empty<MicDevice>();
                error = "không phải Windows";
            }
            CockpitUiState state = _ui.Load();
            MicSelection selection = PreferredMicSelector.Select(devices, state.PreferredMicSubstring, state.MicDevice);
            return (selection, error, Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3));
        }

        public string DescribeStatus(MicSelection s, string? error)
        {
            CockpitUiState state = _ui.Load();
            var sb = new StringBuilder();
            sb.Append(s.Selected != null ? $"[ACTIVE MIC] {s.Selected.Name} ({s.Reason})" : $"[MIC] Không chọn được micro: {s.Reason}");
            foreach (MicDevice v in s.Ignored) sb.Append($" · {v.Name} [Ignored Virtual Mic]");
            sb.Append($" · {SampleRate} Hz, 16-bit, Mono PCM · ưu tiên \"{state.PreferredMicSubstring}\"");
            if (!string.IsNullOrWhiteSpace(state.MicDevice)) sb.Append($" · ép chọn \"{state.MicDevice}\" (/mic select auto để bỏ)");
            if (error != null) sb.Append($" · lỗi liệt kê: {error}");
            return sb.ToString();
        }

        public static string DescribeList(MicSelection s)
        {
            if (s.All.Count == 0) return $"[MIC] Không có thiết bị thu âm nào đang bật ({s.Reason}).";
            return "[MIC] " + string.Join(" | ", s.All.Select((d, i) =>
                $"{i + 1}. {d.Name}" +
                (ReferenceEquals(d, s.Selected) ? " [Selected]" : d.Virtual ? " [Virtual-Ignored]" : string.Empty) +
                (d.IsDefault ? " (mặc định Windows)" : string.Empty)));
        }

        /// <summary>/mic select &lt;tên | số thứ tự&gt; saves micDevice in cockpit_ui.json; "auto" clears it.</summary>
        public (bool Ok, string Message) Select(string? argument)
        {
            string arg = (argument ?? string.Empty).Trim();
            if (arg.Length == 0) return (false, "[MIC] Dùng: /mic select <tên hoặc số thứ tự trong /mic list> | /mic select auto");
            CockpitUiState state = _ui.Load();
            if (arg.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                string? err = _ui.Save(state with { MicDevice = null });
                return (err == null, err == null ? $"[MIC] Bỏ ép chọn: tự chọn theo \"{state.PreferredMicSubstring}\" và danh sách micro ảo." : $"[MIC] Không lưu được: {err}");
            }
            (MicSelection selection, _, _) = Inventory();
            MicDevice? pick = int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= selection.All.Count
                ? selection.All[n - 1]
                : selection.All.FirstOrDefault(d => d.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
            if (pick == null) return (false, $"[MIC] Không có thiết bị khớp \"{arg}\". {DescribeList(selection)}");
            string? error = _ui.Save(state with { MicDevice = pick.Name });
            return error == null
                ? (true, $"[MIC] Đã chọn {pick.Name}" + (pick.Virtual ? " (micro ảo, theo yêu cầu)" : string.Empty) + $" → {CockpitUiStore.FileName}.")
                : (false, $"[MIC] Không lưu được: {error}");
        }

        /// <summary>Opens the selected microphone for <paramref name="seconds"/> (or until cancelled) and returns the VAD summary.</summary>
        public PttResult RunDevice(double seconds, CancellationToken cancel)
        {
            (MicSelection selection, string? error, _) = Inventory();
            if (selection.Selected == null || !OperatingSystem.IsWindows())
                return Failed(selection.Selected?.Name ?? "-", selection.Reason, error ?? "không có micro để mở", seconds);
            if (selection.Selected.WaveInIndex is not int index)
                return Failed(selection.Selected.Name, selection.Reason, "không tìm thấy chỉ số waveIn của thiết bị này", seconds);
            return Run(new WaveInPcmSource(index, selection.Selected.Name), selection.Reason, seconds, cancel);
        }

        /// <summary>One session on any source (device or synthetic). Never two at once.</summary>
        public PttResult Run(IPcmSource source, string reason, double seconds, CancellationToken cancel)
        {
            seconds = Math.Clamp(seconds, 0.2, MaxSessionSeconds);
            if (Interlocked.Exchange(ref _busy, 1) != 0) return Failed(source.Name, reason, "đang có một phiên thu âm khác", seconds);
            try
            {
                var vad = new VadAnalyzer();
                ISpeechToTextProvider stt = Stt;
                short[]? kept = null;
                int keptCount = 0;
                if (stt.NeedsAudio) kept = ArrayPool<short>.Shared.Rent((int)(seconds * SampleRate) + VadAnalyzer.FrameSamples);
                PcmCaptureStats stats;
                VadSummary summary;
                string transcript;
                try
                {
                    stats = source.Capture(TimeSpan.FromSeconds(seconds), cancel, chunk =>
                    {
                        vad.Process(chunk);
                        if (kept != null)
                        {
                            int take = Math.Min(chunk.Length, kept.Length - keptCount);
                            chunk.Slice(0, take).CopyTo(kept.AsSpan(keptCount));
                            keptCount += take;
                        }
                    });
                    summary = vad.Summary();
                    transcript = stats.Ok ? stt.Transcribe(summary, kept != null ? kept.AsSpan(0, keptCount) : ReadOnlySpan<short>.Empty) : string.Empty;
                }
                finally
                {
                    if (kept != null)
                    {
                        // transcript first, then the samples go back to the pool (cleared): no audio outlives the session
                        Array.Clear(kept, 0, keptCount);
                        ArrayPool<short>.Shared.Return(kept);
                    }
                }
                var result = new PttResult(stats.Ok, stats.Error, source.Name, reason, SampleRate, 16, 1, seconds,
                    stats.CaptureMs, stats.Samples, summary, transcript, stt.Name, stats.Buffers, stats.BufferBytes,
                    PcmBufferLedger.OutstandingBytes, cancel.IsCancellationRequested, stats.OpenMs, stats.CloseMs,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                LastSession = result;
                return result;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        public static string DescribeResult(PttResult r)
        {
            if (!r.Ok) return $"[PTT] {r.Device}: lỗi {r.Error}";
            string seconds = (r.CapturedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            return $"[PTT] {r.Device} · {seconds} s · RMS {r.Vad.AvgRmsDbfs.ToString("0.0", CultureInfo.InvariantCulture)} dBFS " +
                   $"(đỉnh {r.Vad.PeakRmsDbfs.ToString("0.0", CultureInfo.InvariantCulture)}) · VAD = {(r.Vad.Vad ? "True" : "False")} " +
                   $"({r.Vad.VoicedFrames}/{r.Vad.Frames} khung) · {r.Transcript} · bộ đệm còn giữ {r.OutstandingBytesAfter} B" +
                   (r.Cancelled ? " · dừng sớm" : string.Empty);
        }

        private PttResult Failed(string device, string reason, string error, double seconds)
        {
            var r = new PttResult(false, error, device, reason, SampleRate, 16, 1, seconds, 0, 0,
                new VadSummary(0, 0, false, -120, -120, 0, 0, VadAnalyzer.DefaultThresholdDbfs), string.Empty, Stt.Name, 0, 0,
                PcmBufferLedger.OutstandingBytes, false, 0, 0, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            LastSession = r;
            return r;
        }
    }
}
