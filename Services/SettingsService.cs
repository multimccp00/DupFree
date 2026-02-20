using System;
using System.IO;
using System.Text.Json;

namespace DupFree.Services
{
    /// <summary>Units that can be used when formatting file sizes.</summary>
    public enum SizeUnit
    {
        Auto,
        Bytes,
        KB,
        MB,
        GB,
        TB
    }

    /// <summary>Application-wide settings storage and helpers (in-memory + file persistence).</summary>
    public static class SettingsService
    {
        /// <summary>Current unit used when formatting file sizes.</summary>
        public static SizeUnit CurrentSizeUnit { get; private set; } = SizeUnit.Auto;
        /// <summary>Current UI theme name (e.g. "dark" or "light").</summary>
        public static string CurrentTheme { get; private set; } = "dark";

        // File size limits (in MB, 0 = no limit)
        public static long MinFileSizeMB { get; private set; } = 0;
        public static long MaxFileSizeMB { get; private set; } = 0;

        // Duplicate count limit (0 = no limit)
        public static int MaxDuplicatesToShow { get; private set; } = 0;

        // Grid view picture size (in pixels)
        public static int GridPictureSize { get; private set; } = 150;

        // Grid view show file path setting
        public static bool ShowGridFilePath { get; private set; } = true;

        // Confirm delete dialog
        public static bool ConfirmDelete { get; private set; } = true;

        // Auto-select options for similar images
        public static bool AutoSelectKeepUncompressed { get; private set; } = false;
        public static bool AutoSelectKeepHigherResolution { get; private set; } = true;
        public static bool AutoSelectKeepLargerFilesize { get; private set; } = false;
        // Debug timer display for similar images scan
        public static bool ShowScanTimer { get; private set; } = false;

        // Maximum number of items to keep in the recycle bin
        public static int MaxRecycleBinSize { get; private set; } = 30;

        /// <summary>
        /// Restore every setting to its original default value and notify listeners.
        /// Use this when the user wants to reset all preferences.
        /// </summary>
        public static void ResetToDefaults()
        {
            CurrentSizeUnit = SizeUnit.Auto;
            CurrentTheme = "dark";
            MinFileSizeMB = 0;
            MaxFileSizeMB = 0;
            MaxDuplicatesToShow = 0;
            GridPictureSize = 150;
            ShowGridFilePath = true;
            ConfirmDelete = true;
            AutoSelectKeepUncompressed = false;
            AutoSelectKeepHigherResolution = true;
            AutoSelectKeepLargerFilesize = false;
            ShowScanTimer = false;
            MaxRecycleBinSize = 30;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }


        public static event Action? OnSettingsChanged;

        /// <summary>Set the unit used for file-size formatting and notify listeners.</summary>
        /// <param name="u">Desired <see cref="SizeUnit"/>.</param>
        public static void SetSizeUnit(SizeUnit u)
        {
            CurrentSizeUnit = u;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetTheme(string theme)
        {
            CurrentTheme = theme;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetMinFileSizeMB(long sizeMB)
        {
            MinFileSizeMB = sizeMB;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetMaxFileSizeMB(long sizeMB)
        {
            MaxFileSizeMB = sizeMB;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetMaxDuplicatesToShow(int count)
        {
            MaxDuplicatesToShow = count;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetGridPictureSize(int size)
        {
            GridPictureSize = size;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetShowGridFilePath(bool show)
        {
            ShowGridFilePath = show;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetConfirmDelete(bool confirm)
        {
            ConfirmDelete = confirm;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetAutoSelectKeepUncompressed(bool keep)
        {
            AutoSelectKeepUncompressed = keep;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetAutoSelectKeepHigherResolution(bool keep)
        {
            AutoSelectKeepHigherResolution = keep;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetAutoSelectKeepLargerFilesize(bool keep)
        {
            AutoSelectKeepLargerFilesize = keep;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetShowScanTimer(bool show)
        {
            ShowScanTimer = show;
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }

        public static void SetMaxRecycleBinSize(int size)
        {
            MaxRecycleBinSize = Math.Max(0, size);
            OnSettingsChanged?.Invoke();
            SaveToFile();
        }


        public static bool GetShowScanTimer()
        {
            return ShowScanTimer;
        }

        private static string GetSettingsFilePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "DupFree");
            if (!Directory.Exists(appFolder))
                Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "settings.json");
        }

        /// <summary>Persist current settings to disk (AppData/DupFree/settings.json).</summary>
        public static void SaveToFile()
        {
            try
            {
                var settings = new
                {
                    SizeUnit = CurrentSizeUnit.ToString(),
                    Theme = CurrentTheme,
                    MinFileSizeMB,
                    MaxFileSizeMB,
                    MaxDuplicatesToShow,
                    GridPictureSize,
                    ShowGridFilePath,
                    ConfirmDelete,
                    AutoSelectKeepUncompressed,
                    AutoSelectKeepHigherResolution,
                    AutoSelectKeepLargerFilesize,
                    ShowScanTimer,
                    MaxRecycleBinSize,

                };

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetSettingsFilePath(), json);
            }
            catch { /* Silently fail if settings can't be saved */ }
        }

        /// <summary>Load settings from disk (if present) into the in-memory settings.</summary>
        public static void LoadFromFile()
        {
            try
            {
                var filePath = GetSettingsFilePath();
                if (!File.Exists(filePath))
                    return;

                var json = File.ReadAllText(filePath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("SizeUnit", out var sizeUnit))
                {
                    if (Enum.TryParse<SizeUnit>(sizeUnit.GetString(), out var unit))
                        CurrentSizeUnit = unit;
                }

                if (root.TryGetProperty("Theme", out var theme))
                    CurrentTheme = theme.GetString() ?? "dark";

                if (root.TryGetProperty("MinFileSizeMB", out var minSize))
                    MinFileSizeMB = minSize.GetInt64();

                if (root.TryGetProperty("MaxFileSizeMB", out var maxSize))
                    MaxFileSizeMB = maxSize.GetInt64();

                if (root.TryGetProperty("MaxDuplicatesToShow", out var maxDupes))
                    MaxDuplicatesToShow = maxDupes.GetInt32();

                if (root.TryGetProperty("GridPictureSize", out var gridSize))
                    GridPictureSize = gridSize.GetInt32();

                if (root.TryGetProperty("ShowGridFilePath", out var showPath))
                    ShowGridFilePath = showPath.GetBoolean();

                if (root.TryGetProperty("ConfirmDelete", out var confirmDelete))
                    ConfirmDelete = confirmDelete.GetBoolean();

                if (root.TryGetProperty("AutoSelectKeepUncompressed", out var keepUncompressed))
                    AutoSelectKeepUncompressed = keepUncompressed.GetBoolean();

                if (root.TryGetProperty("AutoSelectKeepHigherResolution", out var keepHigherRes))
                    AutoSelectKeepHigherResolution = keepHigherRes.GetBoolean();

                if (root.TryGetProperty("AutoSelectKeepLargerFilesize", out var keepLarger))
                    AutoSelectKeepLargerFilesize = keepLarger.GetBoolean();

                if (root.TryGetProperty("ShowScanTimer", out var showTimer))
                    ShowScanTimer = showTimer.GetBoolean();

                if (root.TryGetProperty("MaxRecycleBinSize", out var maxBinSize))
                    MaxRecycleBinSize = Math.Max(0, maxBinSize.GetInt32());

            }
            catch { /* Silently fail if settings can't be loaded */ }
        }
    }
}
