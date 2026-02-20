using System;
using System.IO;
using System.Reflection;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            // Locate DupFree.dll by searching parent directories for bin\Debug\net8.0-windows\DupFree.dll
            string? current = AppContext.BaseDirectory;
            string? dllPath = null;
            for (int i = 0; i < 8; i++)
            {
                var tryPath = Path.GetFullPath(Path.Combine(current!, "..", "bin", "Debug", "net8.0-windows", "DupFree.dll"));
                if (File.Exists(tryPath)) { dllPath = tryPath; break; }
                current = Path.GetFullPath(Path.Combine(current!, ".."));
            }
            if (!File.Exists(dllPath))
            {
                Log.Error("Could not find DupFree.dll at: " + dllPath);
                return 2;
            }
            var asm = Assembly.LoadFrom(dllPath);
            var t = asm.GetType("DupFree.Services.SimilarImageService");
            if (t == null)
            {
                Log.Error("Type DupFree.Services.SimilarImageService not found in assembly.");
                return 3;
            }
            var method = t.GetMethod("TryEnableOpenCL", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                Log.Error("TryEnableOpenCL method not found.");
                return 4;
            }
            var parameters = new object[] { null };
            var result = (bool)method.Invoke(null, parameters);
            var message = parameters[0] as string;
            Log.Info("TryEnableOpenCL returned: " + result);
            Log.Info("Message: " + message);

            // Also check Windows registry for OpenCL ICD vendors (common on Windows).
            try
            {
                Log.Info(string.Empty);
                Log.Info("Checking registry for OpenCL ICD vendors...");
                var vendorKeys = new[] {
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Khronos\OpenCL\Vendors",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Khronos\OpenCL\Vendors"
                };
                bool found = false;
                foreach (var keyPath in vendorKeys)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath.Replace("HKEY_LOCAL_MACHINE\\", ""));
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        var val = key.GetValue(name)?.ToString();
                        Log.Info($"Vendor: {name} => {val}");
                        found = true;
                    }
                }
                if (!found) Log.Info("No OpenCL ICD vendors found in registry.");
            }
            catch (Exception ex)
            {
                Log.Error("Registry check failed: " + ex.Message);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return 1;
        }
    }
}
