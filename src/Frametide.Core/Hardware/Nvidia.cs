using System.Runtime.InteropServices;
using System.Text;

namespace Frametide.Core.Hardware;

public sealed record GpuInfo(string Name, string Driver, int MaxClockMHz, int PowerMinW, int PowerMaxW, int PowerDefaultW,
    bool OffsetSupported, int OffsetMinMHz, int OffsetMaxMHz);

public sealed record GpuSnapshot(int TempC, double PowerW, int PowerLimitW, int ClockMHz, int MemClockMHz, int Util, int FanPct,
    string PState, int OffsetMHz, ulong ThrottleReasons, double VoltageV);

/// <summary>Resizable BAR (BAR1 size) and the PCIe link the slot allows.</summary>
public sealed record GpuBus(int? Bar1MB, int? PcieGen, int? PcieWidth, int? GpuMaxGen);

/// <summary>NVIDIA GPU via NVML (nvml.dll comes with the driver). All methods return null without an NVIDIA GPU.</summary>
public static partial class Nvidia
{
    private const string Dll = "nvml.dll";
    private const int RetryMs = 30_000;
    private static IntPtr _device;
    private static long _failedAt = -1;     // Environment.TickCount64 of the last failed connect, -1 = none
    private static readonly object Gate = new();

    /// <summary>
    /// The device handle, or zero without an NVIDIA GPU. Callers keep the returned handle in a local: another thread
    /// may reconnect at any time. A failed connect is tried again after a while (NVML may not be ready right after
    /// sign-in or a driver update).
    /// </summary>
    private static IntPtr Device()
    {
        lock (Gate)
        {
            if (_device != IntPtr.Zero) return _device;
            if (_failedAt >= 0 && Environment.TickCount64 - _failedAt < RetryMs) return IntPtr.Zero;
            try
            {
                if (nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out var device) == 0)
                {
                    _device = device;
                    _failedAt = -1;
                    return device;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
            _failedAt = Environment.TickCount64;
            return IntPtr.Zero;
        }
    }

    public static GpuInfo? GetInfo()
    {
        var d = Device();
        if (d == IntPtr.Zero) return null;
        var name = new StringBuilder(96); nvmlDeviceGetName(d, name, 96);
        var driver = new StringBuilder(80); nvmlSystemGetDriverVersion(driver, 80);
        nvmlDeviceGetMaxClockInfo(d, 0, out var maxClk);
        nvmlDeviceGetPowerManagementLimitConstraints(d, out var plMin, out var plMax);
        nvmlDeviceGetPowerManagementDefaultLimit(d, out var plDef);
        var off = NewOffset();
        var offRc = nvmlDeviceGetClockOffsets(d, ref off);
        return new GpuInfo(name.ToString(), driver.ToString(), (int)maxClk, (int)(plMin / 1000), (int)(plMax / 1000), (int)(plDef / 1000),
            offRc == 0, off.MinOffsetMHz, off.MaxOffsetMHz);
    }

    public static GpuSnapshot? GetSnapshot()
    {
        var d = Device();
        if (d == IntPtr.Zero) return null;
        if (nvmlDeviceGetTemperature(d, 0, out var t) != 0) return null;
        nvmlDeviceGetPowerUsage(d, out var p);
        nvmlDeviceGetPowerManagementLimit(d, out var pl);
        nvmlDeviceGetClockInfo(d, 0, out var c);
        nvmlDeviceGetClockInfo(d, 2, out var m);
        nvmlDeviceGetUtilizationRates(d, out var u);
        nvmlDeviceGetFanSpeed(d, out var f);
        nvmlDeviceGetPerformanceState(d, out var ps);
        nvmlDeviceGetCurrentClocksEventReasons(d, out var reasons);
        var off = NewOffset();
        nvmlDeviceGetClockOffsets(d, ref off);
        return new GpuSnapshot((int)t, Math.Round(p / 1000.0, 1), (int)(pl / 1000), (int)c, (int)m, (int)u.Gpu, (int)f, $"P{ps}",
            off.OffsetMHz, reasons, NvApiVoltage.GetCoreVoltage());
    }

    /// <summary>The power limit in effect right now, or null when it cannot be read.</summary>
    public static int? GetPowerLimitW()
    {
        var d = Device();
        if (d == IntPtr.Zero) return null;
        return nvmlDeviceGetPowerManagementLimit(d, out var mw) == 0 && mw > 0 ? (int)(mw / 1000) : null;
    }

    public static GpuBus? GetBus()
    {
        var d = Device();
        if (d == IntPtr.Zero) return null;
        var rcBar = nvmlDeviceGetBAR1MemoryInfo(d, out var bar);
        var rcGen = nvmlDeviceGetMaxPcieLinkGeneration(d, out var gen);
        var rcWidth = nvmlDeviceGetMaxPcieLinkWidth(d, out var width);
        var rcGpuGen = nvmlDeviceGetGpuMaxPcieLinkGeneration(d, out var gpuGen);
        return new GpuBus(rcBar == 0 ? (int)(bar.Total / (1024 * 1024)) : null, rcGen == 0 ? (int)gen : null,
            rcWidth == 0 ? (int)width : null, rcGpuGen == 0 && gpuGen > 0 ? (int)gpuGen : null);
    }

    /// <summary>Forgets the device handle; the next call connects again (needed after a driver reset).</summary>
    public static void Reconnect()
    {
        lock (Gate) { _device = IntPtr.Zero; _failedAt = -1; }
        NvApiVoltage.Reset();
    }

    // Changes below are volatile: after a reboot or driver reset the GPU runs at defaults again. They need admin rights.

    /// <summary>Shifts the voltage/frequency curve of the graphics clock (P0).</summary>
    public static void SetClockOffset(int mhz)
    {
        var o = NewOffset();
        o.OffsetMHz = mhz;
        Check(Call(d => nvmlDeviceSetClockOffsets(d, ref o)), "Setting the clock offset");
    }

    public static void LockClocks(int minMHz, int maxMHz) => Check(Call(d => nvmlDeviceSetGpuLockedClocks(d, (uint)minMHz, (uint)maxMHz)), "Locking the clock");

    public static void ResetLockedClocks() => Check(Call(nvmlDeviceResetGpuLockedClocks), "Resetting the clock lock");

    public static void SetPowerLimit(int watts) => Check(Call(d => nvmlDeviceSetPowerManagementLimit(d, (uint)watts * 1000)), "Setting the power limit");

    private static int Call(Func<IntPtr, int> f)
    {
        var d = Device();
        if (d == IntPtr.Zero) throw new InvalidOperationException("No NVIDIA GPU (NVML) found.");
        var rc = f(d);
        if (rc == 0) return 0;
        // A driver reset invalidates the handle: connect again once.
        Reconnect();
        d = Device();
        return d != IntPtr.Zero ? f(d) : rc;
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi(nvmlErrorString(rc))} (code {rc}).");
    }

    // nvmlClockOffset_v1: version = sizeof | (1 << 24)
    private static ClockOffset NewOffset() => new() { Version = (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24) };

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Bar1 { public ulong Total; public ulong Free; public ulong Used; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ClockOffset { public uint Version; public int Type; public int PState; public int OffsetMHz; public int MinOffsetMHz; public int MaxOffsetMHz; }

    [LibraryImport(Dll)] private static partial int nvmlInit_v2();
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
    [LibraryImport(Dll)] internal static partial int nvmlDeviceGetClockOffsets(IntPtr device, ref ClockOffset info);
    [LibraryImport(Dll)] private static partial int nvmlDeviceSetClockOffsets(IntPtr device, ref ClockOffset info);
    [LibraryImport(Dll)] private static partial int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minMHz, uint maxMHz);
    [LibraryImport(Dll)] private static partial int nvmlDeviceResetGpuLockedClocks(IntPtr device);
    [LibraryImport(Dll)] private static partial int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint milliwatts);
    [LibraryImport(Dll)] private static partial IntPtr nvmlErrorString(int result);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetBAR1MemoryInfo(IntPtr device, out Bar1 bar1);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint gen);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);
    [LibraryImport(Dll)] private static partial int nvmlDeviceGetGpuMaxPcieLinkGeneration(IntPtr device, out uint gen);
}

/// <summary>Core voltage via NvAPI (NvAPI_GPU_ClientVoltRailsGetStatus, not in NVML). Read-only.</summary>
internal static class NvApiVoltage
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] private static extern IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnEnum([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnVolt(IntPtr gpu, IntPtr status);

    private const int RetryMs = 30_000;
    private static readonly object Gate = new();   // the UI timer and the GPU test thread read the voltage at the same time
    private static IntPtr _gpu;
    private static FnVolt? _volt;
    private static long _failedAt = -1;            // Environment.TickCount64 of the last failed setup, -1 = none

    /// <summary>Call with <see cref="Gate"/> held.</summary>
    private static bool Ensure()
    {
        if (_volt is not null) return true;
        if (_failedAt >= 0 && Environment.TickCount64 - _failedAt < RetryMs) return false;
        try
        {
            var p = QueryInterface(0x0150E828);     // NvAPI_Initialize
            if (p == IntPtr.Zero) return Fail();
            Marshal.GetDelegateForFunctionPointer<FnInit>(p)();
            var handles = new IntPtr[64];
            p = QueryInterface(0xE5AC921F);         // NvAPI_EnumPhysicalGPUs
            if (p == IntPtr.Zero || Marshal.GetDelegateForFunctionPointer<FnEnum>(p)(handles, out var n) != 0 || n < 1) return Fail();
            _gpu = handles[0];
            p = QueryInterface(0x465F9BCF);         // NvAPI_GPU_ClientVoltRailsGetStatus
            if (p == IntPtr.Zero) return Fail();
            _volt = Marshal.GetDelegateForFunctionPointer<FnVolt>(p);
            _failedAt = -1;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return Fail(); }
    }

    private static bool Fail()
    {
        _failedAt = Environment.TickCount64;
        return false;
    }

    public static void Reset()
    {
        lock (Gate) { _volt = null; _failedAt = -1; }
    }

    /// <summary>Core voltage in volts, or -1 when not available.</summary>
    public static double GetCoreVoltage()
    {
        lock (Gate)
        {
            if (!Ensure() || _volt is not { } volt) return -1;
            var buf = Marshal.AllocHGlobal(0x4C);
            try
            {
                for (var i = 0; i < 0x4C; i += 4) Marshal.WriteInt32(buf, i, 0);
                Marshal.WriteInt32(buf, 0, 0x4C | (1 << 16));
                if (volt(_gpu, buf) != 0) { _volt = null; return -1; }   // the handle can be stale after a driver reset
                return Marshal.ReadInt32(buf, 40) / 1_000_000.0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
