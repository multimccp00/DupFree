using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using ImageMagick;

namespace DupFree.Services
{
    /// <summary>
    /// Helper methods to produce thumbnails, extract frames and handle animated images for the UI.
    /// Methods are safe to call from background threads unless otherwise documented.
    /// </summary>
    public class ImagePreviewService
    {
        private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico"];
        private static readonly string[] VideoExtensions =
        [
            ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv",
            ".mpeg", ".mpg", ".mpe", ".3gp", ".3g2", ".mts", ".m2ts",
            ".ts", ".flv", ".f4v", ".ogv", ".ogm", ".asf"
        ];

        /// <summary>Returns true if the file extension denotes an image format supported by the previewer.</summary>
        /// <param name="filePath">Path to the file to inspect.</param>
        /// <returns>True when the file is a supported static image (jpg, png, bmp, etc.).</returns>
        public static bool IsPreviewableImage(string filePath)
        {
            try
            {
                var extension = Path.GetExtension(filePath).ToLower();
                return ImageExtensions.Any(ext => ext == extension);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Check if file is a scannable image for duplicate-image search.</summary>
        public static bool IsScannableImage(string filePath)
        {
            try
            {
                var extension = Path.GetExtension(filePath).ToLower();
            // Exclude animated GIFs and known video formats from duplicate-image scanning.
                return ImageExtensions.Where(ext => ext != ".gif").Any(ext => ext == extension) &&
                       !VideoExtensions.Any(ext => ext == extension);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns true when the file is a supported video format (used to display poster/thumbnail).</summary>
        /// <param name="filePath">Path to the file to inspect.</param>
        /// <returns>True for common video file extensions (mp4, mkv, etc.).</returns>
        public static bool IsVideoFile(string filePath)
        {
            try { return VideoExtensions.Contains(Path.GetExtension(filePath).ToLower()); } catch { return false; }
        }

        // Cache for animated WebP detection — avoids re-reading headers on repeated calls (e.g. per-tile + GetThumbnail).
        private static readonly ConcurrentDictionary<string, bool> _animatedWebPCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Determines whether a .webp file contains multiple frames (animated WebP).</summary>
        /// <remarks>Uses a fast RIFF/ANIM byte-header check (~100 bytes read) with a per-path cache.
        /// An animated WebP has an "ANIM" FourCC chunk in its RIFF container.</remarks>
        /// <param name="filePath">Path to the .webp file.</param>
        /// <returns>True if the WebP contains more than one frame.</returns>
        public static bool IsAnimatedWebP(string filePath)
        {
            try
            {
                if (Path.GetExtension(filePath).ToLower() != ".webp") return false;
                return _animatedWebPCache.GetOrAdd(filePath, static path =>
                {
                    try
                    {
                        // An animated WebP is a RIFF WEBP container with an ANIM chunk.
                        // We only need to scan at most ~200 bytes to find it.
                        Span<byte> buf = stackalloc byte[200];
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 200);
                        int read = fs.Read(buf);
                        if (read < 16) return false;
                        // Verify RIFF....WEBP header
                        if (buf[0] != 'R' || buf[1] != 'I' || buf[2] != 'F' || buf[3] != 'F') return false;
                        if (buf[8] != 'W' || buf[9] != 'E' || buf[10] != 'B' || buf[11] != 'P') return false;
                        // Scan chunks looking for "ANIM"
                        int pos = 12;
                        while (pos + 8 <= read)
                        {
                            if (buf[pos] == 'A' && buf[pos + 1] == 'N' && buf[pos + 2] == 'I' && buf[pos + 3] == 'M')
                                return true;
                            // move to next chunk (chunk size is little-endian uint32 at pos+4, padded to even)
                            uint chunkSize = (uint)(buf[pos + 4] | buf[pos + 5] << 8 | buf[pos + 6] << 16 | buf[pos + 7] << 24);
                            pos += 8 + (int)((chunkSize + 1) & ~1u);
                        }
                        return false;
                    }
                    catch { return false; }
                });
            }
            catch { return false; }
        }

        /// <summary>Returns a frozen BitmapImage thumbnail for a static image file (safe on background threads).</summary>
        /// <remarks>Returns null for animated GIF/WebP; callers should use GetAnimatedImageBytes for animated content.</remarks>
        /// <param name="filePath">File path of the image.</param>
        /// <param name="maxWidth">Maximum width of the returned thumbnail.</param>
        /// <param name="maxHeight">Maximum height of the returned thumbnail.</param>
        /// <returns>Frozen <see cref="BitmapImage"/> or null if preview cannot be produced or image is animated.</returns>
        public static BitmapImage? GetThumbnail(string filePath, int maxWidth = 256, int maxHeight = 256)
        {
            try
            {
                var ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".gif" || (ext == ".webp" && IsAnimatedWebP(filePath)))
                    return null; // animated — handle on UI thread to preserve animation

                if (ext == ".webp")
                {
                    using var img = new MagickImage(filePath);
                    img.Resize((uint)maxWidth, (uint)maxHeight);
                    using var ms = new MemoryStream();
                    img.Format = MagickFormat.Png;
                    img.Write(ms);
                    ms.Position = 0;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = maxWidth;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }

                if (!IsPreviewableImage(filePath))
                    return null;

                var bitmapImg = new BitmapImage();
                bitmapImg.BeginInit();
                bitmapImg.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmapImg.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImg.DecodePixelWidth = maxWidth;
                bitmapImg.EndInit();
                bitmapImg.Freeze();
                return bitmapImg;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Returns GIF-compatible bytes for animated images (GIF or animated WebP) scaled to the requested size.</summary>
        /// <param name="filePath">Animated image path.</param>
        /// <param name="maxWidth">Max width for output frames.</param>
        /// <param name="maxHeight">Max height for output frames.</param>
        /// <returns>Byte[] containing GIF data suitable for WPF animation or null on failure.</returns>
        public static byte[]? GetAnimatedImageBytes(string filePath, int maxWidth = 256, int maxHeight = 256)
        {
            try
            {
                var ext = Path.GetExtension(filePath).ToLower();
                if (ext == ".gif")
                {
                    using var coll = new MagickImageCollection(filePath);
                    // coalesce frames so each frame is a full image (fixes partial/overlay frames)
                    coll.Coalesce();
                    foreach (var f in coll) f.Resize((uint)maxWidth, (uint)maxHeight);
                    using var ms = new MemoryStream();
                    coll.Write(ms, MagickFormat.Gif);
                    return ms.ToArray();
                }

                if (ext == ".webp")
                {
                    using var coll = new MagickImageCollection(filePath);
                    if (coll.Count <= 1)
                    {
                        using var img = new MagickImage(filePath);
                        img.Resize((uint)maxWidth, (uint)maxHeight);
                        using var ms2 = new MemoryStream();
                        img.Format = MagickFormat.Png;
                        img.Write(ms2);
                        return ms2.ToArray();
                    }
                    // coalesce for animated webp too (some animated webp provide delta frames)
                    coll.Coalesce();
                    foreach (var f in coll) f.Resize((uint)maxWidth, (uint)maxHeight);
                    using var ms = new MemoryStream();
                    coll.Write(ms, MagickFormat.Gif);
                    return ms.ToArray();
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>Returns a frozen BitmapImage of the first frame of an image (useful for multi-frame formats).</summary>
        /// <param name="filePath">Path to the image file.</param>
        /// <param name="maxWidth">Desired maximum width.</param>
        /// <param name="maxHeight">Desired maximum height.</param>
        /// <returns>A frozen <see cref="BitmapImage"/> of the first frame, or null on failure.</returns>
        public static BitmapImage? GetFirstFrameBitmap(string filePath, int maxWidth = 256, int maxHeight = 256)
        {
            try
            {
                using var coll = new MagickImageCollection(filePath);
                if (coll.Count == 0) return null;
                // coalesce to ensure the first returned frame is the full composited image
                coll.Coalesce();
                using var first = new MagickImage(coll[0]);
                first.Resize((uint)maxWidth, (uint)maxHeight);
                using var ms = new MemoryStream();
                first.Format = MagickFormat.Png;
                first.Write(ms);
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = ms;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = maxWidth;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }

        // Create a BitmapImage from raw bytes on the UI thread. Do NOT Freeze when the bytes represent an animated GIF.
        // Use BitmapCacheOption.OnLoad so stream-backed images (animated GIF bytes) are fully loaded during EndInit.
        // This prevents the MemoryStream from being required after initialization which otherwise breaks animation.
        public static BitmapImage? CreateBitmapImageFromBytes(byte[]? bytes, int decodePixelWidth = 0, bool freeze = true)
        {
            if (bytes == null) return null;

            // If caller requests a frozen/static image and the bytes represent a GIF (multi-frame),
            // decode only the first frame so the returned BitmapImage is a single-frame static PNG.
            if (freeze && bytes.Length >= 3 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            {
                using var msIn = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(msIn, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 1)
                {
                    var firstFrame = decoder.Frames[0];
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(firstFrame);
                    using var msOut = new MemoryStream();
                    encoder.Save(msOut);
                    msOut.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = msOut;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                // else fall through to normal loader for single-frame GIF
            }

            // Default: load bytes (animated when freeze==false, static when freeze==true)
            using var ms = new MemoryStream(bytes);
            var bitmap2 = new BitmapImage();
            bitmap2.BeginInit();
            bitmap2.StreamSource = ms;
            // Always load the bytes immediately so the returned BitmapImage does not depend on the source stream.
            bitmap2.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0) bitmap2.DecodePixelWidth = decodePixelWidth;
            bitmap2.EndInit();
            if (freeze) bitmap2.Freeze();
            return bitmap2;
        }

        // Extract individual frames and delays from an animated GIF or animated WebP.
        // Returns frozen BitmapSource frames and delay in milliseconds for each frame.
        public static (BitmapSource[] Frames, int[] Delays) GetAnimatedFrames(string filePath, int maxWidth = 256, int maxHeight = 256)
        {
            try
            {
                using var coll = new MagickImageCollection(filePath);
                if (coll == null || coll.Count == 0)
                    return (Array.Empty<BitmapSource>(), Array.Empty<int>());

                // Coalesce first so every frame is a full composited image (prevents partial/delta-frame artifacts).
                coll.Coalesce();

                var frames = new List<BitmapSource>((int)coll.Count);
                var delays = new List<int>((int)coll.Count);

                foreach (var frame in coll)
                {
                    // Read the original frame delay (in 1/100th second) before any resizing/copying.
                    int delayCentis = Math.Max(1, (int)frame.AnimationDelay); // at least 1 (10ms)
                    int delayMs = delayCentis * 10; // convert to milliseconds

                    using var img = new MagickImage(frame);
                    img.Resize((uint)maxWidth, (uint)maxHeight);

                    using var ms = new MemoryStream();
                    img.Format = MagickFormat.Png; // export frames as PNG for WPF
                    img.Write(ms);
                    ms.Position = 0;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    frames.Add(bmp);
                    delays.Add(delayMs);
                }

                return (frames.ToArray(), delays.ToArray());
            }
            catch
            {
                return (Array.Empty<BitmapSource>(), Array.Empty<int>());
            }
        }

        // Return a video/poster thumbnail using the Windows shell (IShellItemImageFactory).
        // This gives a robust poster frame for many video formats even when MediaElement cannot play them.
        public static BitmapImage? GetVideoThumbnail(string filePath, int width = 256, int height = 256)
        {
            try
            {
                var riid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"); // IShellItemImageFactory
                SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref riid, out IShellItemImageFactory factory);
                var size = new SIZE { cx = width, cy = height };
                factory.GetImage(size, SIIGBF.SIIGBF_RESIZETOFIT, out IntPtr hBmp);
                var bmpSource = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                DeleteObject(hBmp);

                // Convert to BitmapImage so callers can assign to existing BitmapImage properties.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Position = 0;
                var bm = new BitmapImage();
                bm.BeginInit();
                bm.StreamSource = ms;
                bm.CacheOption = BitmapCacheOption.OnLoad;
                bm.EndInit();
                bm.Freeze();
                return bm;
            }
            catch
            {
                return null;
            }
        }

        #region Shell thumbnail interop
        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage([In] SIZE size, [In] SIIGBF flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [Flags]
        private enum SIIGBF : uint
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10,
            SIIGBF_CROPTOSQUARE = 0x20,
            SIIGBF_WIDETHUMBNAILS = 0x40,
            SIIGBF_SCALEUP = 0x80
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        #endregion

        /// <summary>Formats a file size value into a human-readable string according to the current size unit setting.</summary>
        /// <param name="bytes">Number of bytes.</param>
        /// <param name="unit">Optional size unit override (Auto chooses best unit).</param>
        /// <returns>Formatted file size (e.g. "1.2 MB").</returns>
        public static string FormatFileSize(long bytes, SizeUnit? unit = null)
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            var useUnit = unit ?? Services.SettingsService.CurrentSizeUnit;
            double len = bytes;
            int order = 0;

            if (useUnit == SizeUnit.Auto)
            {
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len /= 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }

            switch (useUnit)
            {
                case SizeUnit.Bytes: return $"{bytes} B";
                case SizeUnit.KB: return $"{(bytes / 1024.0):0.##} KB";
                case SizeUnit.MB: return $"{(bytes / (1024.0 * 1024.0)):0.##} MB";
                case SizeUnit.GB: return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):0.##} GB";
                case SizeUnit.TB: return $"{(bytes / (1024.0 * 1024.0 * 1024.0 * 1024.0)):0.##} TB";
                default:
                    while (len >= 1024 && order < sizes.Length - 1)
                    {
                        order++;
                        len /= 1024;
                    }
                    return $"{len:0.##} {sizes[order]}";
            }
        }
    }
}
