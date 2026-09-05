using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Fortiq.Platform.Windows;

/// <summary>
/// Manages user-level automatic startup of Fortiq Desktop on Windows logon
/// via the HKCU\Software\Microsoft\Windows\CurrentVersion\Run registry key.
/// </summary>
public static class WindowsAutostartController
{
    public const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Fortiq";

    public static bool IsAutostartEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutostartEnabled(bool enable, string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key is null)
            {
                return false;
            }

            if (enable)
            {
                var targetExe = executablePath;
                if (string.IsNullOrWhiteSpace(targetExe))
                {
                    var installedExe = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Fortiq",
                        "Fortiq.Desktop.exe");

                    targetExe = File.Exists(installedExe)
                        ? installedExe
                        : (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe"));
                }

                var command = $"\"{targetExe}\" --tray";
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
