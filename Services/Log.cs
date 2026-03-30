using System;
using System.IO;
using System.Diagnostics;
using System.Threading;

namespace DupFree.Services
{
    /// <summary>
    /// Simple, process-wide logger. Messages are written to Debug, the console (if present),
    /// and (after Init) appended to a persistent log file in application data.
    /// </summary>
    public static class Log
    {
        private static readonly object _sync = new();
        private static string? _filePath;
        private static string? _deletionLogPath;

        /// <summary>
        /// Gets the current log file path if initialization succeeded, otherwise <c>null</c>.
        /// </summary>
        public static string? FilePath => _filePath;

        /// <summary>
        /// Gets the deletion log file path (live-tailable).
        /// </summary>
        public static string? DeletionLogPath => _deletionLogPath;

        /// <summary>
        /// Initialize file logging. Subsequent messages are appended to <paramref name="logFilePath"/>.
        /// Best-effort only; failures are ignored to avoid throwing from the logger.
        /// </summary>
        public static void Init(string logFilePath)
        {
            try
            {
                _filePath = logFilePath;
                var dir = Path.GetDirectoryName(_filePath) ?? string.Empty;
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, $"--- DupFree log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");

                // Create a separate deletion log in the same directory (append to preserve history)
                _deletionLogPath = Path.Combine(dir, "deletions.log");
                File.AppendAllText(_deletionLogPath, $"{Environment.NewLine}--- DupFree session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
            }
            catch { /* best effort */ }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(Exception ex) => Write("ERROR", ex.ToString());

        /// <summary>
        /// Writes a deletion event to the dedicated deletions.log file.
        /// </summary>
        public static void Deletion(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
            try { Debug.WriteLine(line); } catch { }
            if (_deletionLogPath != null)
            {
                try
                {
                    lock (_sync)
                    {
                        File.AppendAllText(_deletionLogPath, line + Environment.NewLine);
                    }
                }
                catch { }
            }
        }

        private static void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

            try { Debug.WriteLine(line); } catch { }
            try { if (!Console.IsOutputRedirected) Console.WriteLine(line); } catch { }

            if (_filePath != null)
            {
                try
                {
                    lock (_sync)
                    {
                        File.AppendAllText(_filePath, line + Environment.NewLine);
                    }
                }
                catch { /* swallow to keep logging non-failing */ }
            }
        }
    }
}