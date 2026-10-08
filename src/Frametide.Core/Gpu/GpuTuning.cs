using Frametide.Core.Hardware;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Gpu;

public enum TuneKind { Default, Profile, Manual, Unknown }

/// <summary>Applies GPU tuning through NVML and remembers what is applied. Everything is volatile (gone after a reboot).</summary>
public static class GpuTuning
{
    /// <summary>Lowest clock of the clock lock; the GPU still idles down.</summary>
    public const int IdleMinClock = 210;

    public const string ManualMarker = "*manual*";
    public const string ApplyProfileArgument = "--apply-gpu-profile";
    private const string TaskName = @"\Frametide\GPU profile";

    public static event Action? Changed;

    /// <summary>maxClock 0 = no clock lock; powerLimitW 0 = leave the power limit alone.</summary>
    public static void Apply(int maxClock, int offsetMHz, int powerLimitW = 0)
    {
        Nvidia.SetClockOffset(offsetMHz);
        if (maxClock > 0) Nvidia.LockClocks(IdleMinClock, maxClock); else Nvidia.ResetLockedClocks();
        if (powerLimitW > 0) Nvidia.SetPowerLimit(powerLimitW);
        Log.Ok($"GPU tuning: offset {offsetMHz:+#;-#;0} MHz, {(maxClock > 0 ? $"max {maxClock} MHz" : "no clock lock")}{(powerLimitW > 0 ? $", power limit {powerLimitW} W" : "")}.");
    }

    public static void Reset(bool keepPowerLimit = false)
    {
        try { Nvidia.SetClockOffset(0); }
        catch (InvalidOperationException e) { Log.Warn(e.Message); }
        Nvidia.ResetLockedClocks();
        if (!keepPowerLimit && Nvidia.GetInfo() is { } info)
        {
            try { Nvidia.SetPowerLimit(info.PowerDefaultW); }
            catch (InvalidOperationException e) { Log.Warn(e.Message); }
        }
    }

    public static void ApplyProfile(string name)
    {
        var p = GpuProfiles.Find(name) ?? throw new InvalidOperationException(L.T("GPU profile '{0}' not found.", name));
        if (Nvidia.GetInfo() is { } info)
        {
            if (p.Gpu is { Length: > 0 } gpu && gpu != info.Name) Log.Warn($"Profile was created on '{gpu}', the current GPU is '{info.Name}'.");
            if (p.Driver is { Length: > 0 } drv && drv != info.Driver) Log.Warn($"Driver changed since the test ({drv} -> {info.Driver}). Testing again is recommended.");
        }
        Apply(p.MaxClock, p.OffsetMHz, p.PowerLimitW);
        SetActive(name);
    }

    /// <summary>Remembers what is applied ("" = default, <see cref="ManualMarker"/> = manual values).</summary>
    public static void SetActive(string name)
    {
        Settings.Update(root =>
        {
            root["ActiveGpuTune"] = name;
            root["ActiveGpuTuneAt"] = DateTimeOffset.Now.ToString("o");
        });
        Changed?.Invoke();
    }

    /// <summary>What is applied right now. The marker is only trusted when it was set after the last boot and the offset still matches.</summary>
    public static (TuneKind Kind, string Name) Active()
    {
        var offset = Nvidia.GetSnapshot()?.OffsetMHz ?? 0;
        var name = Settings.GetString("ActiveGpuTune", "");
        var lastBoot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var valid = name.Length > 0 && DateTimeOffset.TryParse(Settings.GetString("ActiveGpuTuneAt", ""), out var at) && at > lastBoot;
        if (valid && name != ManualMarker && GpuProfiles.Find(name) is { } p && p.OffsetMHz == offset) return (TuneKind.Profile, name);
        if (valid && name == ManualMarker && offset != 0) return (TuneKind.Manual, "");
        return offset != 0 ? (TuneKind.Unknown, "") : (TuneKind.Default, "");
    }

    /// <summary>Profile applied at Windows sign-in ("" = none).</summary>
    public static string SignInProfile => Settings.GetString("AutoGpuProfile", "");

    public static void SetSignInProfile(string name, string exePath)
    {
        if (name.Length == 0) LogonTask.Delete(TaskName);
        else LogonTask.Register(TaskName, "Applies the Frametide GPU profile after sign-in.", exePath,
            $"{ApplyProfileArgument} \"{GpuProfiles.CleanName(name)}\"", TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        Settings.Set("AutoGpuProfile", name);
        Log.Ok(name.Length == 0 ? "GPU profile at sign-in removed." : $"GPU profile '{name}' is now applied at sign-in.");
        Changed?.Invoke();
    }
}
