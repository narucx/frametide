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
    public const string TaskName = @"\Frametide\GPU profile";

    public static event Action? Changed;

    /// <summary>maxClock 0 = no clock lock; powerLimitW 0 = leave the power limit alone. Refused while a GPU test runs.</summary>
    public static void Apply(int maxClock, int offsetMHz, int powerLimitW = 0)
    {
        ThrowIfTestRunning();
        ApplyCore(maxClock, offsetMHz, powerLimitW);
    }

    /// <summary>
    /// The clock lock goes first: an offset without its lock would overclock. When any step fails, offset and lock are
    /// reset, so a half-applied undervolt never stays.
    /// </summary>
    internal static void ApplyCore(int maxClock, int offsetMHz, int powerLimitW)
    {
        try
        {
            if (maxClock > 0) Nvidia.LockClocks(IdleMinClock, maxClock);
            else Try(Nvidia.ResetLockedClocks);
            Nvidia.SetClockOffset(offsetMHz);
            if (powerLimitW > 0) Nvidia.SetPowerLimit(powerLimitW);
        }
        catch
        {
            Try(() => Nvidia.SetClockOffset(0));
            Try(Nvidia.ResetLockedClocks);
            throw;
        }
        Log.Ok($"GPU tuning: offset {offsetMHz:+#;-#;0} MHz, {(maxClock > 0 ? $"max {maxClock} MHz" : "no clock lock")}{(powerLimitW > 0 ? $", power limit {powerLimitW} W" : "")}.");
    }

    /// <summary>Refused while a GPU test runs.</summary>
    public static void Reset(bool keepPowerLimit = false)
    {
        ThrowIfTestRunning();
        ResetCore(keepPowerLimit);
    }

    /// <summary>Every step runs even when an earlier one fails. Returns false when a step failed (logged).</summary>
    internal static bool ResetCore(bool keepPowerLimit)
    {
        var ok = Try(() => Nvidia.SetClockOffset(0));
        ok &= Try(Nvidia.ResetLockedClocks);
        if (!keepPowerLimit)
        {
            if (Nvidia.GetInfo() is { PowerDefaultW: > 0 } info) ok &= Try(() => Nvidia.SetPowerLimit(info.PowerDefaultW));
            else ok = false;
        }
        return ok;
    }

    private static bool Try(Action step)
    {
        try { step(); return true; }
        catch (InvalidOperationException e) { Log.Warn(e.Message); return false; }
    }

    private static void ThrowIfTestRunning()
    {
        if (GpuTests.Running) throw new InvalidOperationException(L.T("A GPU test is running. Wait until it has finished or cancel it first."));
    }

    /// <summary>Applies a saved profile. Refused while a GPU test runs.</summary>
    public static void ApplyProfile(string name) => ApplyProfile(name, unattended: false);

    /// <summary>
    /// Applies a saved profile; refused when it was made on another GPU or while a GPU test runs. Unattended (sign-in
    /// task): a profile tested with another driver is skipped and false is returned, since nobody is there to notice a crash.
    /// </summary>
    public static bool ApplyProfile(string name, bool unattended)
    {
        ThrowIfTestRunning();
        return ApplyProfileCore(name, unattended);
    }

    internal static bool ApplyProfileCore(string name, bool unattended)
    {
        var p = GpuProfiles.Find(name) ?? throw new InvalidOperationException(L.T("GPU profile '{0}' not found.", name));
        if (Nvidia.GetInfo() is { } info)
        {
            EnsureSameGpu(p, info);
            if (DriverChanged(p, info))
            {
                if (unattended)
                {
                    Log.Warn($"GPU profile '{name}' skipped: the driver changed since the test ({p.Driver} -> {info.Driver}). Test it again and apply it once by hand.");
                    return false;
                }
                Log.Warn($"Driver changed since the test ({p.Driver} -> {info.Driver}). Testing again is recommended.");
            }
        }
        ApplyCore(p.MaxClock, p.OffsetMHz, p.PowerLimitW);
        SetActive(name);
        return true;
    }

    /// <summary>A profile from another GPU model is never applied: its clocks and offsets mean something else there.</summary>
    internal static void EnsureSameGpu(GpuProfile p, GpuInfo info)
    {
        if (OtherGpu(p, info))
            throw new InvalidOperationException(L.T("Profile '{0}' was created on '{1}', the current GPU is '{2}'. Run the smart undervolt again on this GPU.", p.Name, p.Gpu, info.Name));
    }

    public static bool OtherGpu(GpuProfile p, GpuInfo info) => p.Gpu is { Length: > 0 } gpu && gpu != info.Name;

    /// <summary>The profile was tested with another driver version: testing again is recommended.</summary>
    public static bool DriverChanged(GpuProfile p, GpuInfo info) => p.Driver is { Length: > 0 } drv && drv != info.Driver;

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
        var valid = name.Length > 0 && DateTimeOffset.TryParse(Settings.GetString("ActiveGpuTuneAt", ""), out var at) && at > LastBoot;
        if (valid && name != ManualMarker && GpuProfiles.Find(name) is { } p && p.OffsetMHz == offset) return (TuneKind.Profile, name);
        if (valid && name == ManualMarker && offset != 0) return (TuneKind.Manual, "");
        return offset != 0 ? (TuneKind.Unknown, "") : (TuneKind.Default, "");
    }

    /// <summary>GPU tuning is volatile: anything set before this moment is gone.</summary>
    internal static DateTimeOffset LastBoot => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

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

    /// <summary>Registers the sign-in task again when a profile is chosen but the task is missing (see Autostart.Restore).</summary>
    public static void RestoreSignInTask(string exePath)
    {
        if (SignInProfile is { Length: > 0 } name && LogonTask.Command(TaskName) is null && AdminOnly.IsProtectedProgram(exePath))
            SetSignInProfile(name, exePath);
    }
}
