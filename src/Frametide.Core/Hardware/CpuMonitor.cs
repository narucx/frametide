using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Frametide.Core.Hardware;

public sealed record CpuSample(int Load, int AvgMHz, int MaxMHz, int BaseMHz, int Cores);

public sealed record CpuSensors(double? TempC, double? PowerW, double? Vcore);

/// <summary>
/// CPU load and effective clock per core from the "Processor Information" performance counters (PDH, no package).
/// Read-only. The first sample only primes the counters and returns null.
/// </summary>
public sealed partial class CpuMonitor : IDisposable
{
    private IntPtr _query, _util, _freq, _perf;
    private bool _primed;

    public CpuSample? Sample()
    {
        if (_query == IntPtr.Zero)
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return null;
            if (PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _util) != 0 ||
                PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _freq) != 0 ||
                PdhAddEnglishCounterW(_query, @"\Processor Information(*)\% Processor Performance", IntPtr.Zero, out _perf) != 0)
            {
                Dispose();
                return null;
            }
        }
        if (PdhCollectQueryData(_query) != 0) return null;
        if (!_primed) { _primed = true; return null; }

        if (PdhGetFormattedCounterValue(_util, FmtDouble, out _, out var util) != 0) return null;
        if (PdhGetFormattedCounterValue(_freq, FmtDouble, out _, out var freq) != 0) return null;
        var baseMHz = (int)freq.Double;

        double sum = 0, max = 0;
        var n = 0;
        foreach (var (name, perf) in ReadArray(_perf))
        {
            // Per-core instances look like "0,5"; skip the totals ("_Total", "0,_Total").
            if (!name.Contains(',') || name.EndsWith("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            var mhz = baseMHz * perf / 100.0;
            sum += mhz; n++;
            if (mhz > max) max = mhz;
        }
        return new CpuSample((int)Math.Round(Math.Min(100, util.Double)), n > 0 ? (int)(sum / n) : 0, (int)max, baseMHz, n);
    }

    private static List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, ref count, IntPtr.Zero) != PdhMoreData) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, ref count, buf) != 0) return list;
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buf + i * itemSize);
                if (item.Value.Status == 0) list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Double));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private const uint FmtDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [LibraryImport("pdh.dll")] private static partial int PdhCollectQueryData(IntPtr query);
    [LibraryImport("pdh.dll")] private static partial int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [LibraryImport("pdh.dll")] private static partial int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr items);
    [LibraryImport("pdh.dll")] private static partial int PdhCloseQuery(IntPtr query);
}

/// <summary>
/// CPU temperature, package power and Vcore from HWiNFO's shared memory (only when HWiNFO runs with
/// "Shared Memory Support"). Windows has no driver-free source for these values.
/// </summary>
public static class Hwinfo
{
    private sealed record Reading(int Type, string Label, double Value);

    public static CpuSensors? ReadCpu()
    {
        var r = Read();
        if (r.Count == 0) return null;
        double? Pick(int type, params string[] patterns)
        {
            foreach (var p in patterns)
                if (r.FirstOrDefault(x => x.Type == type && System.Text.RegularExpressions.Regex.IsMatch(x.Label, p)) is { } hit) return hit.Value;
            return null;
        }
        return new CpuSensors(
            Pick(1, "^CPU Package$", "^Core Max$", @"^CPU \(Tctl/Tdie\)$", "^CPU Die"),
            Pick(5, "^CPU Package Power$", @"^CPU Package Power \(SMU\)$"),
            Pick(2, "^Vcore$", "^CPU Core Voltage", @"^Core VID \(avg\)$", @"^Core VIDs \(avg\)$", "^VID$"));
    }

    // HWiNFO_SENSORS_SHARED_MEM2, packed structs: reading label at 140 (user) / 12 (original), value (double) at 284.
    private static List<Reading> Read()
    {
        var list = new List<Reading>();
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != 0x53695748) return list;      // 'HWiS'
            uint offset = v.ReadUInt32(32), size = v.ReadUInt32(36), count = v.ReadUInt32(40);
            var buf = new byte[128];
            for (uint i = 0; i < count; i++)
            {
                long o = offset + (long)i * size;
                v.ReadArray(o + 140, buf, 0, 128);
                var label = Encoding.ASCII.GetString(buf).TrimEnd('\0');
                if (label.Length == 0) { v.ReadArray(o + 12, buf, 0, 128); label = Encoding.ASCII.GetString(buf).TrimEnd('\0'); }
                list.Add(new Reading(v.ReadInt32(o), label, v.ReadDouble(o + 284)));
            }
        }
        catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException or IOException) { }
        return list;
    }
}
