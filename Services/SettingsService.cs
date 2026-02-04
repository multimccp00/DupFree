using System;
using System.IO;
using System.Text.Json;

namespace DupFree.Services
{
    public enum SizeUnit
    {
        Auto,
        Bytes,
        KB,
        MB,
        GB,
        TB
    }

    public static class SettingsService
    {
        public static SizeUnit CurrentSizeUnit { get; private set; } = SizeUnit.Auto;
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

        public static event Action OnSettingsChanged;

        public static void SetSizeUnit(SizeUnit u)
        {
            CurrentSizeUnit = u;
            OnSettingsChanged?.Invoke();
        }

        public static void SetTheme(string theme)
        {
            CurrentTheme = theme;
            OnSettingsChanged?.Invoke();
        }
        
        public static void SetMinFileSizeMB(long sizeMB)
        {
            MinFileSizeMB = sizeMB;
            OnSettingsChanged?.Invoke();
        }
        
        public static void SetMaxFileSizeMB(long sizeMB)
        {
            MaxFileSizeMB = sizeMB;
            OnSettingsChanged?.Invoke();
        }
        
        public static void SetMaxDuplicatesToShow(int count)
        {
            MaxDuplicatesToShow = count;
            OnSettingsChanged?.Invoke();
        }
        
        public static void SetGridPictureSize(int size)
        {
            GridPictureSize = size;
            OnSettingsChanged?.Invoke();
        }
        
        public static void SetShowGridFilePath(bool show)
        {
            ShowGridFilePath = show;
            OnSettingsChanged?.Invoke();
        }
        
        private static string GetSettingsFilePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "DupFree");
            if (!Directory.Exists(appFolder))
                Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "settings.json");
        }
        
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
                    ShowGridFilePath
                };
                
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetSettingsFilePath(), json);
            }
            catch { /* Silently fail if settings can't be saved */ }
        }
        
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
            }
            catch { /* Silently fail if settings can't be loaded */ }
        }
    }
}
