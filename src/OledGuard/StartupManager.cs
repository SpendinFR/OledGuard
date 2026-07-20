using System.IO;
using Microsoft.Win32;

namespace OledGuard;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OledGuard";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{processPath}\"");
        }
        catch
        {
            // Startup is a convenience feature; protection remains usable if this fails.
        }
    }
}
