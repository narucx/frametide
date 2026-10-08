namespace Frametide.Core.Windows;

public enum StartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>Service start modes, read from the registry and set with sc.exe.</summary>
public static class Services
{
    public static StartMode? GetStartMode(string name)
    {
        var path = $@"HKLM:\SYSTEM\CurrentControlSet\Services\{name}";
        var start = Reg.Get(path, "Start");
        if (!start.Exists) return null;
        var mode = Convert.ToInt32(start.Value) switch
        {
            0 => StartMode.Boot, 1 => StartMode.System, 2 => StartMode.Automatic, 3 => StartMode.Manual, _ => StartMode.Disabled,
        };
        if (mode == StartMode.Automatic && Reg.Get(path, "DelayedAutostart").Value is int d && d == 1) mode = StartMode.AutomaticDelayed;
        return mode;
    }

    public static void SetStartMode(string name, StartMode mode)
    {
        var arg = mode switch
        {
            StartMode.Automatic => "auto", StartMode.AutomaticDelayed => "delayed-auto", StartMode.Manual => "demand",
            StartMode.Disabled => "disabled",
            _ => throw new ArgumentException($"Start mode {mode} cannot be set"),
        };
        var r = NativeProcess.Run("sc.exe", ["config", name, "start=", arg]);
        if (r.ExitCode != 0) throw new InvalidOperationException($"sc config {name} failed: {r.Output.Trim()}");
        // A disabled service is also stopped right away (sc.exe instead of ServiceController: no extra package).
        if (mode == StartMode.Disabled) NativeProcess.Run("sc.exe", ["stop", name]);
    }
}
