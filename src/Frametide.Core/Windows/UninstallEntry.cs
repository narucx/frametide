using Frametide.Core.Infrastructure;
using Microsoft.Win32;

namespace Frametide.Core.Windows;

/// <summary>
/// The "Installed apps" entry the MSI creates. Velopack 1.2.161 does not find it after an update, so Settings keeps
/// showing the first installed version; the elevated app puts the running version there.
/// </summary>
public static class UninstallEntry
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI:Frametide";

    public static void SetVersion(string version)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(Key, writable: true);
            if (key is null || key.GetValue("DisplayVersion") as string == version) return;
            key.SetValue("DisplayVersion", version);
            Log.Info($"Installed apps entry set to version {version}.");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Could not update the installed apps entry: {e.Message}");
        }
    }
}
