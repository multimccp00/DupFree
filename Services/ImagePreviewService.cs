using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace DupFree.Services
{
    public class ImagePreviewService
    {
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico" };

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

        public static BitmapImage GetThumbnail(string filePath, int maxWidth = 256, int maxHeight = 256)
        {
            try
            {
                if (!IsPreviewableImage(filePath))
                    return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Decode by width only to preserve aspect ratio and avoid distortion
                bitmap.DecodePixelWidth = maxWidth;
                bitmap.EndInit();
                bitmap.Freeze();

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

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

            // Force a particular unit
            switch (useUnit)
            {
                case SizeUnit.Bytes:
                    return $"{bytes} B";
                case SizeUnit.KB:
                    return $"{(bytes / 1024.0):0.##} KB";
                case SizeUnit.MB:
                    return $"{(bytes / (1024.0 * 1024.0)):0.##} MB";
                case SizeUnit.GB:
                    return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):0.##} GB";
                case SizeUnit.TB:
                    return $"{(bytes / (1024.0 * 1024.0 * 1024.0 * 1024.0)):0.##} TB";
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
