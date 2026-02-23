using System;
using System.Diagnostics;

namespace DupFree.Services
{
    /// <summary>
    /// Lightweight telemetry helper. When enabled via settings it writes
    /// anonymous events and timing statistics to the normal application log
    /// (prefixed with "TELEMETRY"). No file paths or personal data are ever
    /// recorded; only high‑level event names and durations are stored so the
    /// developer can spot slow paths.
    /// </summary>
    public static class TelemetryService
    {
        /// <summary>
        /// Returns <c>true</c> when the user has opted in to anonymous telemetry.
        /// </summary>
        public static bool Enabled => SettingsService.EnableTelemetry;

        /// <summary>
        /// Track a simple event by name.
        /// </summary>
        public static void TrackEvent(string name)
        {
            if (!Enabled) return;
            try { Log.Info($"TELEMETRY: {name}"); } catch { }
        }

        /// <summary>
        /// Track a numeric metric; the value may be a count or a duration.
        /// </summary>
        public static void TrackMetric(string name, double value)
        {
            if (!Enabled) return;
            try { Log.Info($"TELEMETRY_METRIC: {name}={value:F1}"); } catch { }
        }

        /// <summary>
        /// Convenience helper that measures an operation and logs the elapsed
        /// time when disposed. Usage:
        /// <code>using (TelemetryService.Measure("SomeOp")) { ... }</code>
        /// </summary>
        public static IDisposable Measure(string name)
        {
            if (!Enabled)
                return NullDisposable.Instance;
            return new Timer(name);
        }

        private sealed class Timer : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw;
            public Timer(string name)
            {
                _name = name;
                _sw = Stopwatch.StartNew();
            }
            public void Dispose()
            {
                _sw.Stop();
                try { Log.Info($"TELEMETRY: {_name} elapsed={_sw.ElapsedMilliseconds}ms"); } catch { }
            }
        }

        private sealed class NullDisposable : IDisposable
        {
            public static NullDisposable Instance { get; } = new NullDisposable();
            private NullDisposable() { }
            public void Dispose() { }
        }
    }
}
