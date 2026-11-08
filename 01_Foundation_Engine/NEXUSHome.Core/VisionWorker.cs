using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-12c: screen copies made for /snap, in this process (VisionImageProcessor.CaptureScreen) or reported back by a
    /// vision worker. Lives outside VisionImageProcessor so a HostService in "process" mode can count captures without loading
    /// System.Drawing.
    /// </summary>
    public static class VisionCaptureCounter
    {
        private static long _count;

        public static long Count => Interlocked.Read(ref _count);

        public static void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>TASK-12c: names and JSON options shared by the worker and the HostService (platform-neutral).</summary>
    public static class VisionWorkerProtocol
    {
        public const string Argument = "--vision-worker";
        public const string Name = "nexus-vision-worker/1";

        internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    /// <summary>TASK-12c: one job for <c>NEXUSHome.McpTools --vision-worker</c> (one JSON line on stdin).</summary>
    public sealed record VisionWorkerRequest(
        string Source,                 // "file" | "clipboard" | "snap"
        string? FilePath,
        VisionSnapRegion? Region,      // snap: physical-pixel area + HostService windows to paint black (shields already passed)
        int MaxDimensionPx,
        int MaxBytes,
        int MaxSourceDimensionPx);

    /// <summary>
    /// TASK-12c: the worker's answer: this header as one JSON line on stdout, then exactly <see cref="JpegLength"/> raw JPEG
    /// bytes. <see cref="ErrorKind"/>: null (ok) | "SourceTooLarge" | "NoClipboardImage" | "Failed".
    /// </summary>
    public sealed record VisionWorkerResponse(
        string Protocol,
        bool Ok,
        string? ErrorKind,
        string? Error,
        int JpegLength,
        string? MimeType,
        string? Sha256Hex,
        int Width,
        int Height,
        int SourceWidth,
        int SourceHeight,
        long SourceBytes,
        string? SourceFormat,
        int Quality,
        int Attempts,
        double DecodeMs,
        double EncodeMs,
        string? ClipboardFormat,
        double ClipboardHoldMs,
        uint ClipboardSequence,
        bool Captured,
        int? TooLargeWidth,
        int? TooLargeHeight,
        int? TooLargeLimit,
        double WorkerMs,
        double WorkerPeakWorkingSetMb);

    /// <summary>
    /// TASK-12c (ADR-61, Gate 12): the "thợ nén" side, run by <c>NEXUSHome.McpTools --vision-worker</c> before any MCP or
    /// SQLite setup. It reads one <see cref="VisionWorkerRequest"/>, decodes / captures and compresses with the same
    /// VisionImageProcessor code as "inproc" mode (same bytes, same SHA-256), writes the header + JPEG to stdout and the
    /// process exits: every GDI+ native allocation goes back to Windows with it. Exit code 0 = ok, 2 = handled failure
    /// (described in the header), 1 = unexpected crash.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class VisionWorkerServer
    {
        public const string Argument = VisionWorkerProtocol.Argument;
        public const string ProtocolName = VisionWorkerProtocol.Name;

        internal static JsonSerializerOptions Json => VisionWorkerProtocol.Json;

        public static int Run(Stream input, Stream output)
        {
            long t0 = Stopwatch.GetTimestamp();
            VisionWorkerResponse response;
            ReadOnlyMemory<byte> jpeg = ReadOnlyMemory<byte>.Empty;
            try
            {
                using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                string line = reader.ReadLine() ?? throw new InvalidDataException("Empty vision worker request.");
                VisionWorkerRequest request = JsonSerializer.Deserialize<VisionWorkerRequest>(line, Json)
                    ?? throw new InvalidDataException("Vision worker request is not valid JSON.");
                string? clipboardFormat = null;
                double holdMs = 0;
                uint sequence = 0;
                bool captured = false;
                VisionImage? image;
                switch (request.Source)
                {
                    case "file":
                        image = VisionImageProcessor.CompressFile(request.FilePath ?? string.Empty, request.MaxDimensionPx, request.MaxBytes,
                            request.MaxSourceDimensionPx);
                        break;
                    case "clipboard":
                    {
                        ScaledImage? scaled = ClipboardService.TryReadImage(
                            (data, size, isDib, format) => VisionImageProcessor.DecodeAndScaleUnmanaged(data, size, isDib,
                                "clipboard-" + format.ToLowerInvariant(), request.MaxDimensionPx, request.MaxSourceDimensionPx),
                            out string fmt, out holdMs, out sequence);
                        clipboardFormat = fmt;
                        if (scaled == null)
                        {
                            image = null;
                            break;
                        }
                        using (scaled) image = VisionImageProcessor.EncodeUnderBudget(scaled, request.MaxBytes);
                        break;
                    }
                    case "snap":
                    {
                        VisionSnapRegion region = request.Region ?? throw new InvalidDataException("snap request without a region.");
                        using var dpi = new VisionSafetyGuard.PerMonitorDpiScope(); // same physical-pixel space as the HostService
                        using Bitmap shot = VisionImageProcessor.CaptureScreen(region.Left, region.Top, region.Width, region.Height, region.Blackouts);
                        captured = true;
                        image = VisionImageProcessor.CompressToJpegUnderBudget(shot, request.MaxDimensionPx, request.MaxBytes,
                            (long)region.Width * region.Height * 3, "screen");
                        break;
                    }
                    default:
                        throw new InvalidDataException($"Unknown vision worker source '{request.Source}'.");
                }

                if (image == null)
                {
                    response = Failure("NoClipboardImage", clipboardFormat ?? "no image", t0) with
                    {
                        ClipboardFormat = clipboardFormat,
                        ClipboardHoldMs = holdMs,
                        ClipboardSequence = sequence
                    };
                }
                else
                {
                    jpeg = image.Bytes;
                    response = new VisionWorkerResponse(ProtocolName, true, null, null, image.ByteCount, image.MimeType, image.Sha256Hex,
                        image.Width, image.Height, image.SourceWidth, image.SourceHeight, image.SourceBytes, image.SourceFormat,
                        image.Quality, image.Attempts, image.DecodeMs, image.EncodeMs, clipboardFormat, holdMs, sequence, captured,
                        null, null, null, Ms(t0), PeakWorkingSetMb());
                }
            }
            catch (VisionSourceTooLargeException ex)
            {
                response = Failure("SourceTooLarge", ex.Message, t0) with
                {
                    TooLargeWidth = ex.Width,
                    TooLargeHeight = ex.Height,
                    TooLargeLimit = ex.Limit
                };
            }
            catch (Exception ex)
            {
                response = Failure("Failed", $"{ex.GetType().Name}: {ex.Message}", t0);
            }

            try
            {
                byte[] header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, Json) + "\n");
                output.Write(header, 0, header.Length);
                if (response.Ok && jpeg.Length > 0) output.Write(jpeg.Span);
                output.Flush();
            }
            catch (IOException)
            {
                return 1; // the HostService went away
            }
            return response.Ok ? 0 : 2;
        }

        private static VisionWorkerResponse Failure(string kind, string message, long t0) =>
            new(ProtocolName, false, kind, message, 0, null, null, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, null, 0, 0, false,
                null, null, null, Ms(t0), PeakWorkingSetMb());

        private static double PeakWorkingSetMb()
        {
            try { return Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0), 1); }
            catch (InvalidOperationException) { return 0; }
        }

        private static double Ms(long startTicks) =>
            Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }

    /// <summary>TASK-12c: what one worker call returned. <see cref="Unavailable"/> = no answer at all (the caller may fall back).</summary>
    public sealed record VisionWorkerResult(
        VisionImage? Image,
        VisionWorkerResponse? Header,
        bool Unavailable,
        string? Error,
        int? ExitCode,
        double ElapsedMs,
        string? WorkerPath);

    /// <summary>
    /// TASK-12c: the HostService side. Starts the worker (no window), writes the request, reads the header + JPEG, waits for
    /// the exit. It never references System.Drawing types, so a HostService in "process" mode does not load GDI+ for vision.
    /// The worker is our own short-lived child: after <see cref="TimeoutMs"/> without an answer it is stopped (only that child).
    /// </summary>
    public static class VisionWorkerClient
    {
        public const string PathVariable = "NEXUS_VISION_WORKER_PATH";
        public const string ExecutableName = "NEXUSHome.McpTools.exe";
        public const int TimeoutMs = 15000;

        /// <summary>The McpTools executable to run, or null when none is found.</summary>
        public static string? ResolvePath()
        {
            string? env = Environment.GetEnvironmentVariable(PathVariable);
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);
            string? self = Environment.ProcessPath;
            if (self != null && string.Equals(Path.GetFileName(self), ExecutableName, StringComparison.OrdinalIgnoreCase)) return self;

            string baseDir = AppContext.BaseDirectory;
            string sameDir = Path.Combine(baseDir, ExecutableName);
            if (File.Exists(sameDir)) return sameDir;

            // Build output layout: <engine>\NEXUSHome.HostService\bin\<Config>\<tfm>\ → <engine>\NEXUSHome.McpTools\bin\<Config>\net8.0\
            var dir = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string? config = dir.Parent?.Name;
            for (DirectoryInfo? d = dir; d != null; d = d.Parent)
            {
                string mcp = Path.Combine(d.FullName, "NEXUSHome.McpTools");
                if (!Directory.Exists(mcp)) continue;
                foreach (string cfg in new[] { config ?? "Release", "Release", "Debug" })
                {
                    string candidate = Path.Combine(mcp, "bin", cfg, "net8.0", ExecutableName);
                    if (File.Exists(candidate)) return candidate;
                }
                break;
            }
            return null;
        }

        public static VisionWorkerResult Run(VisionWorkerRequest request)
        {
            long t0 = Stopwatch.GetTimestamp();
            string? path = ResolvePath();
            if (path == null) return new VisionWorkerResult(null, null, true, $"{ExecutableName} not found (set {PathVariable})", null, Ms(t0), null);

            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
            };
            info.ArgumentList.Add(VisionWorkerProtocol.Argument);

            Process? process = null;
            try
            {
                process = Process.Start(info);
                if (process == null) return new VisionWorkerResult(null, null, true, "Process.Start returned null", null, Ms(t0), path);
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                process.StandardInput.WriteLine(JsonSerializer.Serialize(request, VisionWorkerProtocol.Json));
                process.StandardInput.Close();

                using var buffer = new MemoryStream(Math.Max(64 * 1024, request.MaxBytes + 4096));
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
                if (!copy.Wait(TimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return new VisionWorkerResult(null, null, true, $"no answer within {TimeoutMs} ms (worker stopped)", null, Ms(t0), path);
                }
                process.WaitForExit(5000);
                int? exit = process.HasExited ? process.ExitCode : null;

                if (!buffer.TryGetBuffer(out ArraySegment<byte> all)) all = new ArraySegment<byte>(buffer.ToArray());
                int newline = Array.IndexOf(all.Array!, (byte)'\n', all.Offset, all.Count);
                if (newline < 0)
                {
                    string err = stderr.Wait(1000) ? stderr.Result : string.Empty;
                    return new VisionWorkerResult(null, null, true, $"no header (exit {exit}){(err.Length > 0 ? ": " + Trim(err) : string.Empty)}", exit, Ms(t0), path);
                }
                VisionWorkerResponse? header = JsonSerializer.Deserialize<VisionWorkerResponse>(
                    new ReadOnlySpan<byte>(all.Array, all.Offset, newline - all.Offset), VisionWorkerProtocol.Json);
                if (header == null || header.Protocol != VisionWorkerProtocol.Name)
                    return new VisionWorkerResult(null, header, true, $"protocol mismatch ({header?.Protocol ?? "none"}): rebuild McpTools", exit, Ms(t0), path);
                if (!header.Ok) return new VisionWorkerResult(null, header, false, header.Error, exit, Ms(t0), path);

                int start = newline + 1;
                int available = all.Offset + all.Count - start;
                if (header.JpegLength <= 0 || header.JpegLength > available)
                    return new VisionWorkerResult(null, header, true, $"JPEG cut short ({available} of {header.JpegLength} bytes)", exit, Ms(t0), path);
                var bytes = new byte[header.JpegLength];
                Buffer.BlockCopy(all.Array!, start, bytes, 0, bytes.Length);
                var image = new VisionImage(bytes, header.MimeType ?? "image/jpeg", header.Sha256Hex ?? string.Empty, header.Width, header.Height,
                    header.SourceWidth, header.SourceHeight, header.SourceBytes, header.SourceFormat ?? request.Source, header.Quality,
                    header.Attempts, header.DecodeMs, header.EncodeMs);
                return new VisionWorkerResult(image, header, false, null, exit, Ms(t0), path);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or JsonException or AggregateException)
            {
                return new VisionWorkerResult(null, null, true, $"{ex.GetType().Name}: {ex.Message}", null, Ms(t0), path);
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static string Trim(string text) => text.Length > 300 ? text.Substring(0, 300) : text.Trim();

        private static double Ms(long startTicks) =>
            Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
