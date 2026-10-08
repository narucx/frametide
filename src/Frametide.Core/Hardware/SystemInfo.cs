using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Frametide.Core.Windows;

namespace Frametide.Core.Hardware;

public sealed record MemoryModule(int SizeMB, int RatedMTs, int ConfiguredMTs, int SmbiosType, string Manufacturer, string PartNumber);

public sealed record SystemInfo(
    string CpuName, int Cores, int Threads, int PCores, int ECores, bool Hybrid, uint? Microcode,
    string? GpuName, string? GpuDriver, IReadOnlyList<MemoryModule> Memory,
    string OsName, string OsVersion, string OsBuild, string Board, string Bios)
{
    public int RamGB => (int)Math.Round(Memory.Sum(m => (long)m.SizeMB) / 1024.0);
    public int RamSpeed => Memory.FirstOrDefault()?.ConfiguredMTs ?? 0;
}

public enum CheckLevel { Ok, Info, Warn, Bad }

public sealed record SystemCheck(string Name, CheckLevel Level, string Value, string Text, params object[] TextArgs);

/// <summary>System information without WMI: registry, SMBIOS firmware table, NVML, CPU topology.</summary>
public static partial class SystemInfoReader
{
    public static SystemInfo Read()
    {
        const string cpuKey = @"HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        const string cv = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var cpu = Regex.Replace((Reg.Get(cpuKey, "ProcessorNameString").Value as string ?? "").Trim(), @"\s+", " ");
        uint? microcode = Reg.Get(cpuKey, "Update Revision").Value is byte[] mc
            ? mc.Length >= 8 ? BitConverter.ToUInt32(mc, 4) : BitConverter.ToUInt32(mc, 0)
            : null;
        var topo = CpuTopology.Read();
        var gpu = Nvidia.GetInfo();
        var smbios = Smbios.Read();

        var build = Reg.Get(cv, "CurrentBuild").Value as string ?? "";
        var product = Reg.Get(cv, "ProductName").Value as string ?? "Windows";
        // ProductName still says "Windows 10" on Windows 11.
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return new SystemInfo(cpu, topo.Count, Environment.ProcessorCount,
            topo.Count(c => c.Efficiency == topo.Max(x => x.Efficiency)), topo.Count(c => c.Efficiency != topo.Max(x => x.Efficiency)),
            topo.Select(c => c.Efficiency).Distinct().Count() > 1, microcode,
            gpu?.Name ?? DisplayAdapterName(), gpu?.Driver, smbios.Memory,
            product, Reg.Get(cv, "DisplayVersion").Value as string ?? "", $"{build}.{Reg.Get(cv, "UBR").Value}",
            $"{smbios.BoardMaker} {smbios.BoardProduct}".Trim(), smbios.BiosVersion);
    }

    private static string? DisplayAdapterName()
    {
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        return root?.GetSubKeyNames().Select(n => root.OpenSubKey(n)?.GetValue("DriverDesc") as string).FirstOrDefault(d => d is not null);
    }

    /// <summary>The checks shown on the overview: BIOS/firmware and Windows settings that matter for gaming. Read-only.</summary>
    public static IReadOnlyList<SystemCheck> Checks(SystemInfo info)
    {
        var list = new List<SystemCheck>();

        // RAM speed: XMP/EXPO. The rated speed comes from the module (SPD) or the kit's part number (e.g. ...6000C30).
        var mod = info.Memory.FirstOrDefault();
        if (mod is not null && mod.ConfiguredMTs > 0)
        {
            var rated = mod.RatedMTs;
            if (PartSpeed().Match(mod.PartNumber) is { Success: true } m) rated = Math.Max(rated, int.Parse(m.Groups[1].Value));
            var jedec = mod.SmbiosType == 34 ? 4800 : 2666;      // 34 = DDR5
            var cfg = mod.ConfiguredMTs;
            if (rated > cfg + 100)
                list.Add(new SystemCheck("RAM speed (XMP/EXPO)", CheckLevel.Warn, $"{cfg} MT/s", "Your RAM is rated for {0} MT/s but runs at {1}. Enable XMP (Intel) or EXPO (AMD) in the BIOS.", rated, cfg));
            else if (cfg > jedec)
                list.Add(new SystemCheck("RAM speed (XMP/EXPO)", CheckLevel.Ok, $"{cfg} MT/s", "XMP/EXPO is active."));
            else
                list.Add(new SystemCheck("RAM speed (XMP/EXPO)", CheckLevel.Info, $"{cfg} MT/s", "Standard speed. If your RAM is rated higher, enable XMP/EXPO in the BIOS."));
        }

        if (info.Memory.Count == 1)
        {
            var part = info.Memory[0].PartNumber.Trim();
            list.Add(Regex.IsMatch(part, @"M2B|X2\b|K2|2X")
                ? new SystemCheck("Memory channels", CheckLevel.Info, "Single channel", "Only 1 of 2 RAM sticks from the kit ({0}) is installed. Single channel mainly costs 1% lows, less average FPS.", part)
                : new SystemCheck("Memory channels", CheckLevel.Info, "Single channel", "Only one RAM stick. Single channel mainly costs 1% lows, less average FPS."));
        }
        else if (info.Memory.Count >= 2)
            list.Add(new SystemCheck("Memory channels", CheckLevel.Ok, $"{info.Memory.Count} sticks", "Dual channel possible. Use the slots the board manual recommends (usually A2 + B2)."));

        if (Regex.IsMatch(info.CpuName, @"i[579]-1[34]\d{3}") && info.Microcode is { } mcode)
        {
            list.Add(mcode < 0x12B
                ? new SystemCheck("CPU microcode", CheckLevel.Bad, $"0x{mcode:X}", "Intel 13th/14th gen: BIOS update strongly recommended (Vmin shift fix from 0x12B).")
                : new SystemCheck("CPU microcode", CheckLevel.Ok, $"0x{mcode:X}", "Vmin shift fix included. Keep the \"Intel Default Settings\" profile in the BIOS, no \"unlimited\" power limits."));
        }

        if (Nvidia.GetBus() is { } bus)
        {
            if (bus.Bar1MB is { } bar)
                list.Add(bar > 256
                    ? new SystemCheck("Resizable BAR", CheckLevel.Ok, "On", "The CPU can access the whole video memory.")
                    : new SystemCheck("Resizable BAR", CheckLevel.Warn, "Off", "Enable \"Above 4G Decoding\" and \"Re-Size BAR Support\" in the BIOS (CSM must be off). Gives a few % in many games."));
            if (bus.PcieWidth is { } width)
            {
                var link = $"PCIe {bus.PcieGen}.0 x{width}";
                if (width < 16)
                    list.Add(new SystemCheck("GPU slot", CheckLevel.Warn, link, "The graphics card only gets {0} lanes. Use the top x16 slot; on some boards M.2 drives share lanes with it.", width));
                else if (bus.GpuMaxGen is { } gpuMax && bus.PcieGen < gpuMax)
                    list.Add(new SystemCheck("GPU slot", CheckLevel.Info, link, "The card supports PCIe {0}.0, the slot runs at {1}.0. Check the PCIe setting of the slot in the BIOS (Auto).", gpuMax, bus.PcieGen ?? 0));
                else
                    list.Add(new SystemCheck("GPU slot", CheckLevel.Ok, link, "Full speed."));
            }
        }

        if (MemoryIntegrityRunning() is { } hvci)
            list.Add(hvci
                ? new SystemCheck("Memory integrity (HVCI)", CheckLevel.Info, "On", "Security feature that costs a few % FPS in some games. To turn it off: Windows Security > Device security > Core isolation > Memory integrity (restart). Only do this if you accept less protection against malicious drivers.")
                : new SystemCheck("Memory integrity (HVCI)", CheckLevel.Ok, "Off", "No performance cost."));
        return list;
    }

    /// <summary>Whether hypervisor-enforced code integrity is running now (not just configured).</summary>
    public static bool? MemoryIntegrityRunning()
    {
        var ci = new CodeIntegrityInformation { Length = (uint)Marshal.SizeOf<CodeIntegrityInformation>() };
        if (NtQuerySystemInformation(103, ref ci, ci.Length, out _) != 0) return null;    // SystemCodeIntegrityInformation
        return (ci.Options & 0x400) != 0;                                                // CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED
    }

    [GeneratedRegex(@"(?<!\d)([4-9]\d{3})(?!\d)")]
    private static partial Regex PartSpeed();

    [StructLayout(LayoutKind.Sequential)]
    private struct CodeIntegrityInformation { public uint Length; public uint Options; }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int infoClass, ref CodeIntegrityInformation info, uint length, out uint returned);
}

/// <summary>SMBIOS (DMI) table via GetSystemFirmwareTable('RSMB'): memory devices (type 17), board (2), BIOS (0).</summary>
internal static partial class Smbios
{
    public sealed record Data(IReadOnlyList<MemoryModule> Memory, string BoardMaker, string BoardProduct, string BiosVersion);

    public static Data Read()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0) return new Data([], "", "", "");
        var raw = new byte[size];
        GetSystemFirmwareTable(Rsmb, 0, raw, size);
        var mem = new List<MemoryModule>();
        string maker = "", product = "", bios = "";
        var len = BitConverter.ToInt32(raw, 4);
        var pos = 8;                                     // RawSMBIOSData header
        var end = Math.Min(raw.Length, 8 + len);
        while (pos + 4 <= end)
        {
            int type = raw[pos], hdrLen = raw[pos + 1];
            if (hdrLen < 4) break;
            var strings = ReadStrings(raw, pos + hdrLen, end, out var next);
            string Str(int offset) => offset < hdrLen && raw[pos + offset] is var idx and > 0 && idx <= strings.Count ? strings[idx - 1].Trim() : "";
            int Word(int offset) => offset + 1 < hdrLen ? BitConverter.ToUInt16(raw, pos + offset) : 0;
            switch (type)
            {
                case 0: bios = Str(0x05); break;
                case 2: maker = Str(0x04); product = Str(0x05); break;
                case 17:
                    var sizeMb = Word(0x0C);
                    if (sizeMb is 0 or 0xFFFF) break;                                   // empty slot / unknown
                    if (sizeMb == 0x7FFF && hdrLen > 0x1F) sizeMb = BitConverter.ToInt32(raw, pos + 0x1C);   // extended size
                    else if ((sizeMb & 0x8000) != 0) sizeMb = (sizeMb & 0x7FFF) / 1024;     // KB granularity
                    var rated = Word(0x15);
                    var configured = Word(0x20);
                    if (rated == 0xFFFF && hdrLen > 0x57) rated = BitConverter.ToInt32(raw, pos + 0x54);
                    if (configured == 0xFFFF && hdrLen > 0x5B) configured = BitConverter.ToInt32(raw, pos + 0x58);
                    mem.Add(new MemoryModule(sizeMb, rated, configured, hdrLen > 0x12 ? raw[pos + 0x12] : 0, Str(0x17), Str(0x1A)));
                    break;
            }
            if (type == 127) break;                      // end of table
            pos = next;
        }
        return new Data(mem, maker, product, bios);
    }

    private static List<string> ReadStrings(byte[] raw, int start, int end, out int next)
    {
        var list = new List<string>();
        var p = start;
        if (p + 1 < end && raw[p] == 0 && raw[p + 1] == 0) { next = p + 2; return list; }   // no strings
        while (p < end && raw[p] != 0)
        {
            var s = p;
            while (p < end && raw[p] != 0) p++;
            list.Add(Encoding.ASCII.GetString(raw, s, p - s));
            p++;
        }
        next = p + 1;
        return list;
    }

    private const uint Rsmb = 0x52534D42;     // 'RSMB'

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);
}

/// <summary>Physical cores with efficiency class (P-/E-cores) via GetLogicalProcessorInformationEx.</summary>
public static partial class CpuTopology
{
    public sealed record Core(int Index, byte Efficiency, bool Smt, ulong Mask);

    public static IReadOnlyList<Core> Read()
    {
        var list = new List<Core>();
        uint len = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return list;
            int offset = 0, index = 0;
            while (offset < len)
            {
                var p = buf + offset;
                var size = Marshal.ReadInt32(p, 4);
                var flags = Marshal.ReadByte(p, 8);
                var eff = Marshal.ReadByte(p, 9);
                var mask = (ulong)Marshal.ReadInt64(p, 32);
                var group = (ushort)Marshal.ReadInt16(p, 40);
                if (group == 0) list.Add(new Core(index, eff, (flags & 1) == 1, mask));
                index++;
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
