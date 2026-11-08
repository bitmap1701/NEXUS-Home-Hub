using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-12 ADR-61: one image ready for Gemini <c>inline_data</c>: JPEG bytes within the byte budget, longest edge within
    /// the dimension cap, and the lowercase SHA-256 of exactly those bytes. <see cref="Bytes"/> is never serialized.
    /// </summary>
    public sealed record VisionImage(
        [property: JsonIgnore] byte[] Bytes,
        string MimeType,
        string Sha256Hex,
        int Width,
        int Height,
        int SourceWidth,
        int SourceHeight,
        long SourceBytes,
        string SourceFormat,
        int Quality,
        int Attempts,
        double DecodeMs,
        double EncodeMs)
    {
        public int ByteCount => Bytes.Length;

        public VisionImageInfo Info => new(MimeType, Bytes.Length, Sha256Hex, Width, Height, SourceWidth, SourceHeight, SourceBytes,
            SourceFormat, Quality, Attempts, DecodeMs, EncodeMs);
    }

    /// <summary>TASK-12: what is kept about a compressed image after its bytes are released (memo, reports, MCP).</summary>
    public sealed record VisionImageInfo(
        string MimeType,
        int Bytes,
        string Sha256Hex,
        int Width,
        int Height,
        int SourceWidth,
        int SourceHeight,
        long SourceBytes,
        string SourceFormat,
        int Quality,
        int Attempts,
        double DecodeMs,
        double EncodeMs);

    /// <summary>
    /// TASK-12 §3.2 (ADR-61): image pipeline for /img and /snap on the GDI+ codecs that ship with Windows (System.Drawing,
    /// no third-party imaging library). Scale so the longest edge is at most maxVisionDimensionPx (aspect ratio kept),
    /// encode JPEG at quality 78, then 60, then 45; if the bytes are still above maxVisionImageBytes, shrink the image by a
    /// quarter and try again, so the result always fits the budget. Every Bitmap, Graphics and stream is disposed before
    /// the method returns: only the JPEG bytes (&lt;= 250 KB) leave it (Private WS gate &lt;= 52 MB).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class VisionImageProcessor
    {
        public const string JpegMime = "image/jpeg";
        public const int DefaultMaxBytes = 256000;
        public const int DefaultMaxDimension = 1280;

        /// <summary>Refuse sources that would need hundreds of MB to decode (a screenshot is far below both limits).</summary>
        public const long MaxSourceBytes = 64L * 1024 * 1024;
        public const int MaxSourceDimension = 16384;

        private static long _captureCount;

        /// <summary>Screen copies made by this process (selftest evidence that a blocked /snap never reached the screen).</summary>
        public static long CaptureCount => System.Threading.Interlocked.Read(ref _captureCount);

        private static readonly int[] Qualities = { 78, 60, 45 };
        private const double DownscaleStep = 0.75;
        private const int MinDimension = 64;
        private static readonly Lazy<ImageCodecInfo> JpegCodec = new(() =>
            ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid));

        /// <summary>Decodes an image file (PNG, JPEG, BMP, GIF...) straight from disk and compresses it.</summary>
        public static VisionImage CompressFile(string path, int maxDimensionPx, int maxBytes)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Image file not found.", path);
            if (info.Length > MaxSourceBytes) throw new InvalidDataException($"Image file is larger than {MaxSourceBytes / (1024 * 1024)} MB.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            return CompressStream(stream, info.Length, Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), maxDimensionPx, maxBytes);
        }

        /// <summary>Decodes encoded image bytes (clipboard PNG or BMP-wrapped CF_DIB) and compresses them.</summary>
        public static VisionImage CompressBytes(byte[] encoded, string sourceFormat, int maxDimensionPx, int maxBytes)
        {
            if (encoded.LongLength > MaxSourceBytes) throw new InvalidDataException($"Image is larger than {MaxSourceBytes / (1024 * 1024)} MB.");
            using var stream = new MemoryStream(encoded, writable: false);
            return CompressStream(stream, encoded.LongLength, sourceFormat, maxDimensionPx, maxBytes);
        }

        public static VisionImage CompressStream(Stream stream, long sourceBytes, string sourceFormat, int maxDimensionPx, int maxBytes)
        {
            long t0 = Stopwatch.GetTimestamp();
            // GDI+ keeps reading from the stream while the Image lives: both are disposed together below.
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if (source.Width <= 0 || source.Height <= 0 || source.Width > MaxSourceDimension || source.Height > MaxSourceDimension)
                throw new InvalidDataException($"Image size {source.Width}x{source.Height} is outside 1..{MaxSourceDimension} px.");
            double decodeMs = Ms(t0);
            return CompressToJpegUnderBudget(source, maxDimensionPx, maxBytes, sourceBytes, sourceFormat, decodeMs);
        }

        /// <summary>
        /// §3.2 CompressToJpegUnderBudget: longest edge &lt;= <paramref name="maxDimensionPx"/>, JPEG quality 78 → 60 → 45, then
        /// 3/4 of the size per extra step until the bytes fit <paramref name="maxBytes"/> (always, down to a 64 px edge).
        /// </summary>
        public static VisionImage CompressToJpegUnderBudget(Image source, int maxDimensionPx, int maxBytes, long sourceBytes = 0,
            string sourceFormat = "bitmap", double decodeMs = 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            int maxDim = Math.Clamp(maxDimensionPx, MinDimension, MaxSourceDimension);
            int budget = Math.Max(4096, maxBytes);
            int w0 = source.Width;
            int h0 = source.Height;
            double scale = Math.Min(1.0, (double)maxDim / Math.Max(w0, h0));
            int attempts = 0;
            for (int step = 0; step < 12; step++)
            {
                int w = Math.Max(1, (int)Math.Round(w0 * scale));
                int h = Math.Max(1, (int)Math.Round(h0 * scale));
                using (Bitmap resized = Resize(source, w, h))
                {
                    foreach (int quality in Qualities)
                    {
                        attempts++;
                        byte[] jpeg = EncodeJpeg(resized, quality);
                        if (jpeg.Length <= budget)
                        {
                            return new VisionImage(jpeg, JpegMime, Sha256Hex(jpeg), w, h, w0, h0, sourceBytes, sourceFormat,
                                quality, attempts, Math.Round(decodeMs, 3), Ms(t0));
                        }
                    }
                }
                if (Math.Max(w, h) <= MinDimension) break;
                scale *= DownscaleStep;
            }
            throw new InvalidDataException($"Could not fit the image into {budget} bytes.");
        }

        /// <summary>
        /// §3.3 safe /snap: copies a screen rectangle (physical pixels) with GDI BitBlt (Graphics.CopyFromScreen) and paints
        /// <paramref name="blackouts"/> (the Cockpit window, in screen coordinates) black so the HUD never ends up in the image.
        /// </summary>
        public static Bitmap CaptureScreen(int left, int top, int width, int height, IEnumerable<VisionRect> blackouts)
        {
            System.Threading.Interlocked.Increment(ref _captureCount);
            var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format24bppRgb);
            try
            {
                using Graphics g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                foreach (VisionRect r in blackouts)
                {
                    var rect = Rectangle.Intersect(new Rectangle(r.Left - left, r.Top - top, r.Right - r.Left, r.Bottom - r.Top),
                        new Rectangle(0, 0, width, height));
                    if (rect.Width > 0 && rect.Height > 0) g.FillRectangle(Brushes.Black, rect);
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        /// <summary>Writes a deterministic test PNG (Verify-Task12 E2/E3): colour bands + fine noise, one pixel optionally set to the opposite extreme (white / black).</summary>
        public static long WriteTestPng(string path, int width, int height, int seed, int noiseAmplitude, (int X, int Y)? flipPixel = null)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var row = new byte[stride];
                var random = new Random(seed);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int noise = noiseAmplitude > 0 ? random.Next(-noiseAmplitude, noiseAmplitude + 1) : 0;
                        int b = (x * 255 / Math.Max(1, width - 1)) + noise;
                        int gr = (y * 255 / Math.Max(1, height - 1)) + noise;
                        int r = 40 + ((x / 160 + y / 90) % 3) * 70 + noise;
                        if (flipPixel is { } p && p.X == x && p.Y == y)
                        {
                            // Maximum contrast (white on a dark spot, black on a bright one), so the change is large
                            // enough to survive JPEG quantization when the image is not downscaled.
                            int v = (299 * r + 587 * gr + 114 * b) / 1000 < 128 ? 255 : 0;
                            b = v;
                            gr = v;
                            r = v;
                        }
                        row[x * 3] = (byte)Math.Clamp(b, 0, 255);
                        row[x * 3 + 1] = (byte)Math.Clamp(gr, 0, 255);
                        row[x * 3 + 2] = (byte)Math.Clamp(r, 0, 255);
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * stride, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path, ImageFormat.Png);
            return new FileInfo(path).Length;
        }

        /// <summary>Reads the size of an encoded image (JPEG check in the selftests) without keeping it.</summary>
        public static (int Width, int Height, string Format) Probe(byte[] encoded)
        {
            using var stream = new MemoryStream(encoded, writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            string format = image.RawFormat.Guid == ImageFormat.Jpeg.Guid ? "jpeg" : image.RawFormat.Guid == ImageFormat.Png.Guid ? "png" : image.RawFormat.Guid.ToString();
            return (image.Width, image.Height, format);
        }

        public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static Bitmap Resize(Image source, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            try
            {
                using Graphics g = Graphics.FromImage(bitmap);
                g.Clear(Color.White); // transparent PNG areas become white, not black
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark seam on the edges
                g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static byte[] EncodeJpeg(Bitmap bitmap, int quality)
        {
            using var output = new MemoryStream(64 * 1024);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(output, JpegCodec.Value, parameters);
            return output.ToArray();
        }

        private static double Ms(long startTicks) =>
            Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
