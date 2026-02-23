using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DupFree
{
    public partial class App : Application
    {
        // runtime DPI awareness avoids embedding <dpiAware> in manifest, which
        // previously caused a SideBySide activation error on older Windows.
        protected override void OnStartup(StartupEventArgs e)
        {
            TrySetProcessDpiAwareness();

            base.OnStartup(e);

            // Initialize persistent log file in %AppData%\DupFree\dupfree.log (best-effort)
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DupFree");
                Directory.CreateDirectory(logDir);
                Services.Log.Init(Path.Combine(logDir, "dupfree.log"));
                Services.Log.Info("Application starting");
            }
            catch { }

            // Load settings from file
            Services.SettingsService.LoadFromFile();

            // if telemetry is enabled, note that the application started
            if (Services.SettingsService.EnableTelemetry)
                Services.TelemetryService.TrackEvent("AppStart");

            // Global exception handlers to capture crashes (writes to temp file)
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    var ex = ev.ExceptionObject as Exception;
                    var path = Path.Combine(Path.GetTempPath(), "dupfree_crash.log");
                    File.AppendAllText(path, $"[Unhandled] {DateTime.Now}: {ex}\n\n");
                }
                catch { }
                ShowUnexpectedErrorDialog();
            };

            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), "dupfree_crash.log");
                    File.AppendAllText(path, $"[UnobservedTask] {DateTime.Now}: {ev.Exception}\n\n");
                }
                catch { }
                ShowUnexpectedErrorDialog();
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), "dupfree_crash.log");
                    File.AppendAllText(path, $"[Dispatcher] {DateTime.Now}: {ev.Exception}\n\n");
                }
                catch { }
                ShowUnexpectedErrorDialog();
                // prevent default crash dialog
                ev.Handled = true;
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // record exit code for diagnostics
            try
            {
                Services.Log.Info($"Application exiting with code {e.ApplicationExitCode}");
            }
            catch { }

            // Save settings when app closes
            Services.SettingsService.SaveToFile();
            base.OnExit(e);
        }

        /// <summary>
        /// Attempts to mark the process DPI aware via Win32 APIs.  We prefer the
        /// newer SetProcessDpiAwarenessContext call but fall back to the older
        /// SetProcessDPIAware.  Any missing APIs are handled gracefully so that
        /// the application still starts on legacy Windows releases.
        /// </summary>
        private static void TrySetProcessDpiAwareness()
        {
            try
            {
                // Windows 10 1607+ API
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                return;
            }
            catch (EntryPointNotFoundException)
            {
                // not available
            }
            catch (Exception ex)
            {
                Services.Log.Error($"SetProcessDpiAwarenessContext failed: {ex}");
            }

            try
            {
                SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
                // not available either
            }
            catch (Exception ex)
            {
                Services.Log.Error($"SetProcessDPIAware failed: {ex}");
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 from WinUser.h
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // ensure we only show the error dialog once per run
        private static bool _errorDialogShown;

        private static void ShowUnexpectedErrorDialog()
        {
            if (_errorDialogShown)
                return;
            _errorDialogShown = true;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                try
                {
                    var logPath = Services.Log.FilePath ?? Path.Combine(Path.GetTempPath(), "dupfree_crash.log");
                    var dlg = new Views.UnexpectedErrorWindow(logPath)
                    {
                        Owner = Application.Current?.MainWindow
                    };
                    dlg.ShowDialog();
                }
                catch
                {
                    // if even showing the window fails, ignore
                }
            });
        }
    }
}
