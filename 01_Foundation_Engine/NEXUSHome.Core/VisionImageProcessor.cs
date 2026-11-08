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
    /// TASK-12b: <see cref="Bytes"/> is a view on the encoder's own buffer (MemoryStream.TryGetBuffer), not a ToArray copy.
    /// </summary>
    public sealed record VisionImage(
        [property: JsonIgnore] ReadOnlyMemory<byte> Bytes,
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

    /// <summary>TASK-12b: a source rejected before any bitmap is allocated (edge &gt; maxSourceDimensionPx).</summary>
    public sealed class VisionSourceTooLargeException : IOException
    {
        public VisionSourceTooLargeException(int width, int height, int limit)
            : base($"{MessagePrefix(limit)} ({width}x{height} px).")
        {
            Width = width;
            Height = height;
            Limit = limit;
        }

        public int Width { get; }
        public int Height { get; }
        public int Limit { get; }

        /// <summary>TASK-12b §3.1.3 message (the size in pixels follows in parentheses).</summary>
        public static string MessagePrefix(int limit) => $"[VISION ERROR] Source image dimensions exceed {limit}px safety limit";
    }

    /// <summary>
    /// TASK-12b: a source image already scaled to the first target size (longest edge &lt;= maxVisionDimensionPx). The source
    /// (and the clipboard memory it was decoded from) is released as soon as this exists; only this bitmap goes on to JPEG.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ScaledImage : IDisposable
    {
        public ScaledImage(Bitmap bitmap, int sourceWidth, int sourceHeight, long sourceBytes, string sourceFormat, double decodeMs, double scaleMs)
        {
            Bitmap = bitmap;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            SourceBytes = sourceBytes;
            SourceFormat = sourceFormat;
            DecodeMs = decodeMs;
            ScaleMs = scaleMs;
        }

        public Bitmap Bitmap { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public long SourceBytes { get; }
        public string SourceFormat { get; }
        public double DecodeMs { get; }
        public double ScaleMs { get; }

        public void Dispose() => Bitmap.Dispose();
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
    /// TASK-12b (LOH slimming): no managed copy of the source (files are streamed, clipboard memory is read in place through
    /// an UnmanagedMemoryStream), sources with an edge above maxSourceDimensionPx (4096) are refused from their header before
    /// any bitmap exists, the source is dropped right after the first scale, and every JPEG attempt reuses one MemoryStream
    /// whose buffer becomes the result (no ToArray copy).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class VisionImageProcessor
    {
        public const string JpegMime = "image/jpeg";
        public const int DefaultMaxBytes = 256000;
        public const int DefaultMaxDimension = 1280;

        /// <summary>Refuse sources that would need hundreds of MB to decode (a screenshot is far below both limits).</summary>
        public const long MaxSourceBytes = 64L * 1024 * 1024;

        /// <summary>TASK-12b: default maxSourceDimensionPx (a 4096 px edge = at most 64 MB of 32-bit pixels while decoding).</summary>
        public const int DefaultMaxSourceDimension = 4096;

        /// <summary>Highest maxSourceDimensionPx nexus_config.json may set.</summary>
        public const int HardMaxSourceDimension = 8192;

        public static string SourceTooLargeMessage(int limit) => VisionSourceTooLargeException.MessagePrefix(limit);

        private static long _captureCount;

        /// <summary>Screen copies made by this process (selftest evidence that a blocked /snap never reached the screen).</summary>
        public static long CaptureCount => System.Threading.Interlocked.Read(ref _captureCount);

        private static readonly int[] Qualities = { 78, 60, 45 };
        private const double DownscaleStep = 0.75;
        private const int MinDimension = 64;
        private static readonly Lazy<ImageCodecInfo> JpegCodec = new(() =>
            ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid));

        /// <summary>Decodes an image file (PNG, JPEG, BMP, GIF...) straight from disk and compresses it.</summary>
        public static VisionImage CompressFile(string path, int maxDimensionPx, int maxBytes, int maxSourceDimensionPx = DefaultMaxSourceDimension)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Image file not found.", path);
            if (info.Length > MaxSourceBytes) throw new InvalidDataException($"Image file is larger than {MaxSourceBytes / (1024 * 1024)} MB.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            return CompressStream(stream, info.Length, Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), maxDimensionPx, maxBytes, maxSourceDimensionPx);
        }

        public static VisionImage CompressStream(Stream stream, long sourceBytes, string sourceFormat, int maxDimensionPx, int maxBytes,
            int maxSourceDimensionPx = DefaultMaxSourceDimension)
        {
            using ScaledImage scaled = DecodeAndScale(stream, sourceBytes, sourceFormat, maxDimensionPx, maxSourceDimensionPx);
            return EncodeUnderBudget(scaled, maxBytes);
        }

        /// <summary>
        /// TASK-12b: decodes and scales an encoded image that lives in unmanaged memory (the locked clipboard block) without
        /// copying it into a managed byte[]. <paramref name="isDib"/> = CF_DIB: a 14-byte BITMAPFILEHEADER is put in front by a
        /// read-only stream, so GDI+ reads it as a .bmp. The memory only has to stay valid until this method returns.
        /// </summary>
        public static unsafe ScaledImage DecodeAndScaleUnmanaged(IntPtr data, long length, bool isDib, string sourceFormat,
            int maxDimensionPx, int maxSourceDimensionPx = DefaultMaxSourceDimension)
        {
            if (data == IntPtr.Zero || length <= 0) throw new InvalidDataException("Empty image block.");
            if (length > MaxSourceBytes) throw new InvalidDataException($"Image is larger than {MaxSourceBytes / (1024 * 1024)} MB.");
            using var raw = new UnmanagedMemoryStream((byte*)data, length, length, FileAccess.Read);
            if (!isDib) return DecodeAndScale(raw, length, sourceFormat, maxDimensionPx, maxSourceDimensionPx);
            byte[] header = DibFileHeader(data, length) ?? throw new InvalidDataException("CF_DIB block has no valid BITMAPINFOHEADER.");
            using var bmp = new PrefixedReadStream(header, raw);
            return DecodeAndScale(bmp, bmp.Length, sourceFormat, maxDimensionPx, maxSourceDimensionPx);
        }

        /// <summary>
        /// TASK-12b: header check (PNG / BMP: before GDI+ sees the stream; other formats: before the first pixel is drawn),
        /// decode, scale to the first target size, release the source. GDI+ keeps reading from the stream while the Image
        /// lives, so the source Image is disposed here, before the caller releases the stream.
        /// </summary>
        public static ScaledImage DecodeAndScale(Stream stream, long sourceBytes, string sourceFormat, int maxDimensionPx,
            int maxSourceDimensionPx = DefaultMaxSourceDimension)
        {
            int limit = Math.Clamp(maxSourceDimensionPx, MinDimension, HardMaxSourceDimension);
            if (TryPeekSize(stream, out int peekW, out int peekH) && (peekW > limit || peekH > limit))
                throw new VisionSourceTooLargeException(peekW, peekH, limit);
            long t0 = Stopwatch.GetTimestamp();
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if (source.Width <= 0 || source.Height <= 0) throw new InvalidDataException($"Image size {source.Width}x{source.Height} is not valid.");
            if (source.Width > limit || source.Height > limit) throw new VisionSourceTooLargeException(source.Width, source.Height, limit);
            return Scale(source, maxDimensionPx, sourceBytes, sourceFormat, Ms(t0));
        }

        /// <summary>First scale: longest edge &lt;= <paramref name="maxDimensionPx"/> (aspect ratio kept, never enlarged).</summary>
        public static ScaledImage Scale(Image source, int maxDimensionPx, long sourceBytes, string sourceFormat, double decodeMs = 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            int maxDim = Math.Clamp(maxDimensionPx, MinDimension, HardMaxSourceDimension);
            int w0 = source.Width;
            int h0 = source.Height;
            double scale = Math.Min(1.0, (double)maxDim / Math.Max(w0, h0));
            int w = Math.Max(1, (int)Math.Round(w0 * scale));
            int h = Math.Max(1, (int)Math.Round(h0 * scale));
            Bitmap scaled = Resize(source, w, h);
            return new ScaledImage(scaled, w0, h0, sourceBytes, sourceFormat, Math.Round(decodeMs, 3), Ms(t0));
        }

        /// <summary>
        /// §3.2 CompressToJpegUnderBudget: longest edge &lt;= <paramref name="maxDimensionPx"/>, JPEG quality 78 → 60 → 45, then
        /// 3/4 of the size per extra step until the bytes fit <paramref name="maxBytes"/> (always, down to a 64 px edge).
        /// </summary>
        public static VisionImage CompressToJpegUnderBudget(Image source, int maxDimensionPx, int maxBytes, long sourceBytes = 0,
            string sourceFormat = "bitmap", double decodeMs = 0)
        {
            using ScaledImage scaled = Scale(source, maxDimensionPx, sourceBytes, sourceFormat, decodeMs);
            return EncodeUnderBudget(scaled, maxBytes);
        }

        /// <summary>
        /// JPEG quality 78 → 60 → 45 on the scaled bitmap, then 3/4 of its size per extra step. All attempts write into one
        /// MemoryStream (sized for the budget, so a fitting JPEG never grows it) and the result is a view on its buffer.
        /// </summary>
        public static VisionImage EncodeUnderBudget(ScaledImage scaled, int maxBytes)
        {
            long t0 = Stopwatch.GetTimestamp();
            int budget = Math.Max(4096, maxBytes);
            Bitmap first = scaled.Bitmap;
            int w1 = first.Width;
            int h1 = first.Height;
            double scale = 1.0;
            int attempts = 0;
            // The buffer is not released by Dispose (MemoryStream holds no native resource): the returned Memory keeps it alive.
            using var output = new MemoryStream(Math.Min(budget + 16 * 1024, 8 * 1024 * 1024));
            for (int step = 0; step < 12; step++)
            {
                int w = step == 0 ? w1 : Math.Max(1, (int)Math.Round(w1 * scale));
                int h = step == 0 ? h1 : Math.Max(1, (int)Math.Round(h1 * scale));
                Bitmap current = step == 0 ? first : Resize(first, w, h);
                try
                {
                    foreach (int quality in Qualities)
                    {
                        attempts++;
                        output.SetLength(0);
                        EncodeJpeg(current, quality, output);
                        if (output.Length <= budget && output.TryGetBuffer(out ArraySegment<byte> buffer))
                        {
                            var jpeg = new ReadOnlyMemory<byte>(buffer.Array!, buffer.Offset, (int)output.Length);
                            return new VisionImage(jpeg, JpegMime, Sha256Hex(jpeg.Span), w, h, scaled.SourceWidth, scaled.SourceHeight,
                                scaled.SourceBytes, scaled.SourceFormat, quality, attempts, scaled.DecodeMs,
                                Math.Round(scaled.ScaleMs + Ms(t0), 3));
                        }
                    }
                }
                finally
                {
                    if (!ReferenceEquals(current, first)) current.Dispose();
                }
                if (Math.Max(w, h) <= MinDimension) break;
                scale *= DownscaleStep;
            }
            throw new InvalidDataException($"Could not fit the image into {budget} bytes.");
        }

        /// <summary>
        /// Width and height from a PNG (IHDR) or BMP (BITMAPINFOHEADER) header without decoding; the stream position is
        /// restored. False for other formats (GDI+ reads their size, still before any pixel is decoded or drawn).
        /// </summary>
        internal static bool TryPeekSize(Stream stream, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!stream.CanSeek) return false;
            long start = stream.Position;
            Span<byte> head = stackalloc byte[26];
            int read = 0;
            try
            {
                while (read < head.Length)
                {
                    int n = stream.Read(head.Slice(read));
                    if (n <= 0) break;
                    read += n;
                }
            }
            finally
            {
                stream.Position = start;
            }
            if (read >= 24 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
            {
                width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                return width > 0 && height > 0;
            }
            if (read >= 26 && head[0] == (byte)'B' && head[1] == (byte)'M' && BitConverter.ToInt32(head.Slice(14, 4)) >= 40)
            {
                width = Math.Abs(BitConverter.ToInt32(head.Slice(18, 4)));
                height = Math.Abs(BitConverter.ToInt32(head.Slice(22, 4))); // negative height = top-down DIB
                return width > 0 && height > 0;
            }
            return false;
        }

        /// <summary>BITMAPFILEHEADER (14 bytes) for a CF_DIB block: header, BI_BITFIELDS masks and colour table come before the pixels.</summary>
        internal static byte[]? DibFileHeader(IntPtr dib, long length)
        {
            if (length < 40 || length > int.MaxValue - 14) return null;
            int headerSize = System.Runtime.InteropServices.Marshal.ReadInt32(dib, 0);
            if (headerSize < 40 || headerSize > length) return null;
            short bitCount = System.Runtime.InteropServices.Marshal.ReadInt16(dib, 14);
            int compression = System.Runtime.InteropServices.Marshal.ReadInt32(dib, 16);
            int colorsUsed = System.Runtime.InteropServices.Marshal.ReadInt32(dib, 32);
            int masks = headerSize == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0; // BI_BITFIELDS / BI_ALPHABITFIELDS
            int colors = colorsUsed > 0 ? colorsUsed : bitCount is > 0 and <= 8 ? 1 << bitCount : 0;
            int offBits = 14 + headerSize + masks + colors * 4;
            var header = new byte[14];
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BitConverter.GetBytes((int)(14 + length)).CopyTo(header, 2);
            BitConverter.GetBytes(offBits).CopyTo(header, 10);
            return header;
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
        public static (int Width, int Height, string Format) Probe(ReadOnlyMemory<byte> encoded)
        {
            using Stream stream = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(encoded, out ArraySegment<byte> segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(encoded.ToArray(), writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            string format = image.RawFormat.Guid == ImageFormat.Jpeg.Guid ? "jpeg" : image.RawFormat.Guid == ImageFormat.Png.Guid ? "png" : image.RawFormat.Guid.ToString();
            return (image.Width, image.Height, format);
        }

        public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

        private static void EncodeJpeg(Bitmap bitmap, int quality, MemoryStream output)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(output, JpegCodec.Value, parameters);
        }

        private static double Ms(long startTicks) =>
            Math.Round((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency, 3);
    }

    /// <summary>TASK-12b: read-only, seekable view of a small prefix (BITMAPFILEHEADER) followed by another stream (the CF_DIB block).</summary>
    internal sealed class PrefixedReadStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private long _position;

        public PrefixedReadStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _prefix.Length + _inner.Length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            if (_position < _prefix.Length && buffer.Length > 0)
            {
                int n = (int)Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsSpan((int)_position, n).CopyTo(buffer);
                _position += n;
                total += n;
                buffer = buffer.Slice(n);
            }
            if (buffer.Length > 0 && _position >= _prefix.Length)
            {
                _inner.Position = _position - _prefix.Length;
                int n = _inner.Read(buffer);
                _position += n;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
