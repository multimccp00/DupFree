using System;

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
        public static string CurrentTheme { get; private set; } = "light";

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
    }
}
