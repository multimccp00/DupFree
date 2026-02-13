using System.Windows;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DupFree
{
    public partial class App : Application
    {
        
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Load settings from file
            Services.SettingsService.LoadFromFile();

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
            };

            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), "dupfree_crash.log");
                    File.AppendAllText(path, $"[UnobservedTask] {DateTime.Now}: {ev.Exception}\n\n");
                }
                catch { }
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), "dupfree_crash.log");
                    File.AppendAllText(path, $"[Dispatcher] {DateTime.Now}: {ev.Exception}\n\n");
                }
                catch { }
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Save settings when app closes
            Services.SettingsService.SaveToFile();
            base.OnExit(e);
        }
    }
}
