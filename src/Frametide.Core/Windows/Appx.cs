using Microsoft.Win32;

namespace Frametide.Core.Windows;

/// <summary>Store app (Appx) checks: packages registered for the current user.</summary>
public static class Appx
{
    private const string Repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static bool IsInstalledForCurrentUser(string packageName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Repository);
        return key?.GetSubKeyNames().Any(n => n.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase)) ?? false;
    }
}
