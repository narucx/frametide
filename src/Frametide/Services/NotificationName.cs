using System.Windows.Media.Imaging;
using Frametide.Core.Infrastructure;
using Microsoft.Win32;

namespace Frametide.Services;

/// <summary>
/// Windows titles notifications with the app's AppUserModelID. Velopack sets it to "velopack.Frametide"; without a
/// registered display name Windows shows that ID instead of "Frametide".
/// </summary>
public static class NotificationName
{
    private const string AppId = "velopack.Frametide";

    public static void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            if (key.GetValue("DisplayName") as string != "Frametide") key.SetValue("DisplayName", "Frametide");
            if (IconPng() is { } png && key.GetValue("IconUri") as string != png) key.SetValue("IconUri", png);
        }
        // Only cosmetic: never let it stop the start.
        catch (Exception e)
        {
            Log.Warn($"Could not register the notification name: {e.Message}");
        }
    }

    /// <summary>The app icon as PNG in the data folder (notifications take PNG files, not icons in resources).</summary>
    private static string? IconPng()
    {
        var path = System.IO.Path.Combine(AppPaths.DataDir, "frametide.png");
        if (System.IO.File.Exists(path)) return path;
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/frametide.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var largest = decoder.Frames.MaxBy(f => f.PixelWidth);
        if (largest is null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(largest);
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
        return path;
    }
}
