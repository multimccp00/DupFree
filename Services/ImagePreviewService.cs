using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using ImageMagick;

namespace DupFree.Services
{
    public class ImagePreviewService
    {
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv" };

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

        public static bool IsVideoFile(string filePath)
        {
            try { return VideoExtensions.Contains(Path.GetExtension(filePath).ToLower()); } catch { return false; }
        }

        public static bool IsAnimatedWebP(string filePath)
        {
            try
            {
                if (Path.GetExtension(filePath).ToLower() != ".webp") return false;
                using var coll = new MagickImageCollection(filePath);
                return coll.Count > 1;
            }
            catch { return false; }
        }

        // Return a frozen BitmapImage for static images (safe to create on background threads).
        // For animated GIF/WebP we return null — callers should use GetAnimatedImageBytes + CreateBitmapImageFromBytes on UI thread.
        public static BitmapImage GetThumbnail(string filePath, int maxWidth = 256, int maxHeight = 256)
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

        // For animated images (GIF / animated WebP) return bytes suitable for WPF animation (GIF bytes)
        public static byte[] GetAnimatedImageBytes(string filePath, int maxWidth = 256, int maxHeight = 256)
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

        public static BitmapImage GetFirstFrameBitmap(string filePath, int maxWidth = 256, int maxHeight = 256)
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
        public static BitmapImage CreateBitmapImageFromBytes(byte[] bytes, int decodePixelWidth = 0, bool freeze = true)
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
        public static BitmapImage GetVideoThumbnail(string filePath, int width = 256, int height = 256)
        {
            try
            {
                var riid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"); // IShellItemImageFactory
                SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref riid, out IShellItemImageFactory factory);
                var size = new SIZE { cx = width, cy = height };
                factory.GetImage(size, SIIGBF.SIIGBF_RESIZETOFIT, out IntPtr hBmp);
                var bmpSource = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(width, height));
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

        public static string FormatFileSize(long bytes, SizeUnit? unit = null)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            var useUnit = unit ?? Services.SettingsService.CurrentSizeUnit;
            double len = bytes;
            int order = 0;

            if (useUnit == SizeUnit.Auto)
            {
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len = len / 1024;
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
                        len = len / 1024;
                    }
                    return $"{len:0.##} {sizes[order]}";
            }
        }
    }
}
