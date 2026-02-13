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
                Console.Error.WriteLine("Could not find DupFree.dll at: " + dllPath);
                return 2;
            }
            var asm = Assembly.LoadFrom(dllPath);
            var t = asm.GetType("DupFree.Services.SimilarImageService");
            if (t == null)
            {
                Console.Error.WriteLine("Type DupFree.Services.SimilarImageService not found in assembly.");
                return 3;
            }
            var method = t.GetMethod("TryEnableOpenCL", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                Console.Error.WriteLine("TryEnableOpenCL method not found.");
                return 4;
            }
            var parameters = new object[] { null };
            var result = (bool)method.Invoke(null, parameters);
            var message = parameters[0] as string;
            Console.WriteLine("TryEnableOpenCL returned: " + result);
            Console.WriteLine("Message: " + message);

            // Also check Windows registry for OpenCL ICD vendors (common on Windows).
            try
            {
                Console.WriteLine();
                Console.WriteLine("Checking registry for OpenCL ICD vendors...");
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
                        Console.WriteLine($"Vendor: {name} => {val}");
                        found = true;
                    }
                }
                if (!found) Console.WriteLine("No OpenCL ICD vendors found in registry.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Registry check failed: " + ex.Message);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex);
            return 1;
        }
    }
}
