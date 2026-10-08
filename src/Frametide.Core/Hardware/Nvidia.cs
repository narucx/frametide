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
    private static IntPtr _device;
    private static bool _failed;
    private static readonly object Gate = new();

    private static bool Init()
    {
        lock (Gate)
        {
            if (_device != IntPtr.Zero) return true;
            if (_failed) return false;
            try
            {
                if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0) { _failed = true; return false; }
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _failed = true; return false; }
        }
    }

    public static GpuInfo? GetInfo()
    {
        if (!Init()) return null;
        var name = new StringBuilder(96); nvmlDeviceGetName(_device, name, 96);
        var driver = new StringBuilder(80); nvmlSystemGetDriverVersion(driver, 80);
        nvmlDeviceGetMaxClockInfo(_device, 0, out var maxClk);
        nvmlDeviceGetPowerManagementLimitConstraints(_device, out var plMin, out var plMax);
        nvmlDeviceGetPowerManagementDefaultLimit(_device, out var plDef);
        var off = NewOffset();
        var offRc = nvmlDeviceGetClockOffsets(_device, ref off);
        return new GpuInfo(name.ToString(), driver.ToString(), (int)maxClk, (int)(plMin / 1000), (int)(plMax / 1000), (int)(plDef / 1000),
            offRc == 0, off.MinOffsetMHz, off.MaxOffsetMHz);
    }

    public static GpuSnapshot? GetSnapshot()
    {
        if (!Init()) return null;
        if (nvmlDeviceGetTemperature(_device, 0, out var t) != 0) return null;
        nvmlDeviceGetPowerUsage(_device, out var p);
        nvmlDeviceGetPowerManagementLimit(_device, out var pl);
        nvmlDeviceGetClockInfo(_device, 0, out var c);
        nvmlDeviceGetClockInfo(_device, 2, out var m);
        nvmlDeviceGetUtilizationRates(_device, out var u);
        nvmlDeviceGetFanSpeed(_device, out var f);
        nvmlDeviceGetPerformanceState(_device, out var ps);
        nvmlDeviceGetCurrentClocksEventReasons(_device, out var reasons);
        var off = NewOffset();
        nvmlDeviceGetClockOffsets(_device, ref off);
        return new GpuSnapshot((int)t, Math.Round(p / 1000.0, 1), (int)(pl / 1000), (int)c, (int)m, (int)u.Gpu, (int)f, $"P{ps}",
            off.OffsetMHz, reasons, NvApiVoltage.GetCoreVoltage());
    }

    public static GpuBus? GetBus()
    {
        if (!Init()) return null;
        var rcBar = nvmlDeviceGetBAR1MemoryInfo(_device, out var bar);
        var rcGen = nvmlDeviceGetMaxPcieLinkGeneration(_device, out var gen);
        var rcWidth = nvmlDeviceGetMaxPcieLinkWidth(_device, out var width);
        var rcGpuGen = nvmlDeviceGetGpuMaxPcieLinkGeneration(_device, out var gpuGen);
        return new GpuBus(rcBar == 0 ? (int)(bar.Total / (1024 * 1024)) : null, rcGen == 0 ? (int)gen : null,
            rcWidth == 0 ? (int)width : null, rcGpuGen == 0 && gpuGen > 0 ? (int)gpuGen : null);
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

    private static IntPtr _gpu;
    private static FnVolt? _volt;
    private static bool _failed;

    private static bool Ensure()
    {
        if (_volt is not null) return true;
        if (_failed) return false;
        try
        {
            var p = QueryInterface(0x0150E828);     // NvAPI_Initialize
            if (p == IntPtr.Zero) { _failed = true; return false; }
            Marshal.GetDelegateForFunctionPointer<FnInit>(p)();
            var handles = new IntPtr[64];
            p = QueryInterface(0xE5AC921F);         // NvAPI_EnumPhysicalGPUs
            if (p == IntPtr.Zero || Marshal.GetDelegateForFunctionPointer<FnEnum>(p)(handles, out var n) != 0 || n < 1) { _failed = true; return false; }
            _gpu = handles[0];
            p = QueryInterface(0x465F9BCF);         // NvAPI_GPU_ClientVoltRailsGetStatus
            if (p == IntPtr.Zero) { _failed = true; return false; }
            _volt = Marshal.GetDelegateForFunctionPointer<FnVolt>(p);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _failed = true; return false; }
    }

    /// <summary>Core voltage in volts, or -1 when not available.</summary>
    public static double GetCoreVoltage()
    {
        if (!Ensure()) return -1;
        var buf = Marshal.AllocHGlobal(0x4C);
        try
        {
            for (var i = 0; i < 0x4C; i += 4) Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt32(buf, 0, 0x4C | (1 << 16));
            if (_volt!(_gpu, buf) != 0) { _volt = null; return -1; }   // the handle can be stale after a driver reset
            return Marshal.ReadInt32(buf, 40) / 1_000_000.0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
