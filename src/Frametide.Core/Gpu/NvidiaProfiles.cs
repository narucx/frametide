using System.Runtime.InteropServices;
using System.Text;
using Frametide.Core.Bench;
using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Gpu;

/// <summary>NvAPI driver settings database (DRS), the store behind "Manage 3D settings".</summary>
public sealed class Drs : IDisposable
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] private static extern IntPtr Query(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int F0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FOut(out IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FH(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FHOut(IntPtr s, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FHP(IntPtr s, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FHPP(IntPtr s, IntPtr p, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FHPId(IntPtr s, IntPtr p, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FHPIdData(IntPtr s, IntPtr p, uint id, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FFindApp(IntPtr s, IntPtr name, out IntPtr p, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FFindProf(IntPtr s, IntPtr name, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FCreateProf(IntPtr s, IntPtr info, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FErr(int status, IntPtr text);

    private const int AppSize = 16396, ProfSize = 4116, SetSize = 12320;   // NVDRS_APPLICATION_V3, NVDRS_PROFILE_V1, NVDRS_SETTING_V1
    private const int SettingNotFound = -160, ProfileNotFound = -163, ExecutableNotFound = -166;

    private IntPtr _session;

    public Drs()
    {
        Check(Fn<F0>(0x0150E828)(), "NvAPI_Initialize");
        Check(Fn<FOut>(0x0694D52E)(out _session), "DRS_CreateSession");
        Check(Fn<FH>(0x375DBD6B)(_session), "DRS_LoadSettings");
    }

    private static T Fn<T>(uint id) where T : Delegate
    {
        IntPtr p;
        try { p = Query(id); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { throw new InvalidOperationException(L.T("No NVIDIA driver found.")); }
        if (p == IntPtr.Zero) throw new InvalidOperationException($"NvAPI function 0x{id:X8} is not available in this driver.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static IntPtr Buffer(int size, uint version)
    {
        var b = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i += 4) Marshal.WriteInt32(b, i, 0);
        Marshal.WriteInt32(b, 0, (int)version);
        return b;
    }

    private static void WriteString(IntPtr b, int offset, string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s);
        Marshal.Copy(bytes, 0, b + offset, Math.Min(bytes.Length, 4094));
    }

    private static IntPtr String(string s)
    {
        var b = Buffer(4096, 0);
        WriteString(b, 0, s);
        return b;
    }

    private static string Error(int status)
    {
        var t = Buffer(64, 0);
        try { return Fn<FErr>(0x6C2D048C)(status, t) == 0 ? Marshal.PtrToStringAnsi(t) ?? "" : $"NvAPI status {status}"; }
        catch (InvalidOperationException) { return $"NvAPI status {status}"; }
        finally { Marshal.FreeHGlobal(t); }
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what}: {Error(rc)} ({rc})");
    }

    public void Dispose()
    {
        if (_session == IntPtr.Zero) return;
        Fn<FH>(0xDAD9CFF8)(_session);
        _session = IntPtr.Zero;
    }

    public void Save() => Check(Fn<FH>(0xFCBC7E14)(_session), "DRS_SaveSettings");

    public IntPtr GlobalProfile()
    {
        Check(Fn<FHOut>(0x617BFF9F)(_session, out var p), "DRS_GetCurrentGlobalProfile");
        return p;
    }

    /// <summary>The profile the driver uses for this executable, or zero when there is none.</summary>
    public IntPtr FindApp(string exe)
    {
        IntPtr name = String(exe), app = Buffer(AppSize, AppSize | (3u << 16));
        try
        {
            var rc = Fn<FFindApp>(0xEEE566B2)(_session, name, out var p, app);
            if (rc == ExecutableNotFound) return IntPtr.Zero;
            Check(rc, "DRS_FindApplicationByName");
            return p;
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }
    }

    public IntPtr FindProfile(string profileName)
    {
        var name = String(profileName);
        try
        {
            var rc = Fn<FFindProf>(0x7E4A9A0B)(_session, name, out var p);
            if (rc == ProfileNotFound) return IntPtr.Zero;
            Check(rc, "DRS_FindProfileByName");
            return p;
        }
        finally { Marshal.FreeHGlobal(name); }
    }

    public string ProfileName(IntPtr profile)
    {
        var info = Buffer(ProfSize, ProfSize | (1u << 16));
        try
        {
            Check(Fn<FHPP>(0x61CD6FD6)(_session, profile, info), "DRS_GetProfileInfo");
            return Marshal.PtrToStringUni(info + 4) ?? "";
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>A DWORD setting. Own = set in this profile (not inherited from the global profile or the default).</summary>
    public (uint? Value, bool Own, bool Predefined) GetDword(IntPtr profile, uint id)
    {
        var st = Buffer(SetSize, SetSize | (1u << 16));
        try
        {
            var rc = Fn<FHPIdData>(0x73BF8338)(_session, profile, id, st);
            if (rc == SettingNotFound) return (null, false, false);
            Check(rc, "DRS_GetSetting");
            return ((uint)Marshal.ReadInt32(st, 8220), Marshal.ReadInt32(st, 4108) == 0, Marshal.ReadInt32(st, 4112) != 0);
        }
        finally { Marshal.FreeHGlobal(st); }
    }

    public void SetDword(IntPtr profile, uint id, uint value)
    {
        var st = Buffer(SetSize, SetSize | (1u << 16));
        try
        {
            Marshal.WriteInt32(st, 4100, (int)id);       // settingId
            Marshal.WriteInt32(st, 4104, 0);             // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(st, 8220, (int)value);    // u32CurrentValue
            Check(Fn<FHPP>(0x577DD202)(_session, profile, st), "DRS_SetSetting");
        }
        finally { Marshal.FreeHGlobal(st); }
    }

    /// <summary>Removes the value set in this profile; it then inherits again (or uses NVIDIA's predefined value).</summary>
    public void ResetSetting(IntPtr profile, uint id)
    {
        var rc = Fn<FHPId>(0x53F0381E)(_session, profile, id);                // DRS_RestoreProfileDefaultSetting
        if (rc != 0) rc = Fn<FHPId>(0xE4A26362)(_session, profile, id);       // DRS_DeleteProfileSetting
        if (rc != SettingNotFound) Check(rc, "DRS_RestoreProfileDefaultSetting");
    }

    public IntPtr CreateProfile(string profileName, string exe, string friendlyName)
    {
        IntPtr info = Buffer(ProfSize, ProfSize | (1u << 16)), app = Buffer(AppSize, AppSize | (3u << 16));
        try
        {
            WriteString(info, 4, profileName);
            Check(Fn<FCreateProf>(0xCC176068)(_session, info, out var p), "DRS_CreateProfile");
            WriteString(app, 8, exe);
            WriteString(app, 8 + 4096, friendlyName);
            Check(Fn<FHPP>(0x4347A9DE)(_session, p, app), "DRS_CreateApplication");
            return p;
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(app); }
    }

    public void DeleteProfile(IntPtr profile) => Check(Fn<FHP>(0x17093206)(_session, profile), "DRS_DeleteProfile");
}

/// <summary>Original value of a driver setting before Frametide changed it (nv-profiles.json).</summary>
public sealed class NvUndo
{
    public string Profile { get; set; } = "";
    public bool Created { get; set; }
    public uint Id { get; set; }
    public bool Own { get; set; }
    public bool Predefined { get; set; }
    public uint? Prev { get; set; }
}

public sealed record NvGameStatus(string Game, string Exe, string? Profile, uint? PState, uint? Prerender, bool Optimized);

public sealed record NvStatus(uint? GlobalPState, uint? GlobalShaderCache, uint? GlobalVsync, IReadOnlyList<NvGameStatus> Games, bool HasUndo);

/// <summary>
/// Optimizes the NVIDIA driver profiles of the games in the Game Boost list: "prefer maximum performance" and the
/// low latency mode, plus an unlimited shader cache. ShadowPlay, the overlay and the NVIDIA App are not touched.
/// Every changed value is recorded first; Undo restores exactly that.
/// </summary>
public static class NvidiaProfiles
{
    public const uint PowerMode = 0x1057EB71;       // 1 = prefer maximum performance
    public const uint Prerender = 0x007BA09E;       // maximum pre-rendered frames: 1 = "Low latency mode: On"
    public const uint ShaderCache = 0x00AC8497;     // shader disk cache size: 0xFFFFFFFF = unlimited
    public const uint Vsync = 0x00A879CF;
    public const uint FrameRateLimit = 0x10835002;
    public const uint VsyncForcedOn = 0x47814940;
    public const uint VsyncForcedOff = 0x08416747;

    private static readonly (uint Id, uint Value)[] GameSettings = [(PowerMode, 1), (Prerender, 1)];

    /// <summary>Driver FPS limits for the benchmark (limiter and V-Sync forced on).</summary>
    public static IEnumerable<FpsLimit> FpsLimitsFor(string exe)
    {
        using var drs = new Drs();
        var p = drs.FindApp(exe);
        if (p == IntPtr.Zero) p = drs.GlobalProfile();
        var result = new List<FpsLimit>();
        if (drs.GetDword(p, FrameRateLimit).Value is { } fps and > 0) result.Add(new FpsLimit("NVIDIA driver", (int)fps));
        if (drs.GetDword(p, Vsync).Value == VsyncForcedOn) result.Add(new FpsLimit("NVIDIA V-Sync forced on", null));
        return result;
    }

    private static string OwnProfileName(GameEntry g) => $"Frametide - {g.Name}";

    public static NvStatus Status()
    {
        using var drs = new Drs();
        var glob = drs.GlobalProfile();
        var games = new List<NvGameStatus>();
        foreach (var g in BoostConfig.Load().Games)
        {
            var p = drs.FindApp(g.Exe);
            if (p == IntPtr.Zero) p = drs.FindProfile(OwnProfileName(g));
            if (p == IntPtr.Zero) { games.Add(new NvGameStatus(g.Name, g.Exe, null, null, null, false)); continue; }
            var ps = drs.GetDword(p, PowerMode);
            var pr = drs.GetDword(p, Prerender);
            games.Add(new NvGameStatus(g.Name, g.Exe, drs.ProfileName(p), ps.Value, pr.Value, ps is { Own: true, Value: 1 } && pr is { Own: true, Value: 1 }));
        }
        return new NvStatus(drs.GetDword(glob, PowerMode).Value, drs.GetDword(glob, ShaderCache).Value, drs.GetDword(glob, Vsync).Value, games, File.Exists(AppPaths.NvProfilesUndo));
    }

    /// <summary>The undo list; only the first original value per profile and setting is kept, so repeated runs never overwrite it.</summary>
    private sealed class UndoLog
    {
        private readonly HashSet<(string, uint, bool)> _known;

        public List<NvUndo> Entries { get; } = JsonFile.Read<List<NvUndo>>(AppPaths.NvProfilesUndo) ?? [];

        public UndoLog() => _known = Entries.Select(u => (u.Profile, u.Id, u.Created)).ToHashSet();

        public void Add(NvUndo u) { if (_known.Add((u.Profile, u.Id, u.Created))) Entries.Add(u); }

        public void Save() => JsonFile.Write(AppPaths.NvProfilesUndo, Entries);
    }

    /// <summary>Sets the values in the game's driver profile (created when the driver has none for the game).</summary>
    private static string ApplyTo(Drs drs, GameEntry g, (uint Id, uint Value)[] settings, UndoLog undo)
    {
        var p = drs.FindApp(g.Exe);
        if (p == IntPtr.Zero)
        {
            var own = OwnProfileName(g);
            p = drs.FindProfile(own);
            if (p == IntPtr.Zero)
            {
                p = drs.CreateProfile(own, g.Exe, g.Name);
                undo.Add(new NvUndo { Profile = own, Created = true });
                Log.Ok($"NVIDIA: created profile '{own}' for {g.Exe}.");
            }
        }
        var name = drs.ProfileName(p);
        foreach (var (id, value) in settings)
        {
            var cur = drs.GetDword(p, id);
            if (cur.Own && cur.Value == value) continue;
            undo.Add(new NvUndo { Profile = name, Id = id, Own = cur.Own, Predefined = cur.Predefined, Prev = cur.Value });
            drs.SetDword(p, id, value);
        }
        return name;
    }

    private static readonly GameEntry Cs2Game = new() { Name = "Counter-Strike 2", Exe = "cs2.exe" };
    private static readonly (uint Id, uint Value)[] Cs2Settings = [(PowerMode, 1), (Vsync, VsyncForcedOff)];

    /// <summary>CS2 profile: prefer maximum performance and V-Sync forced off (Reflex is set in game).</summary>
    public static bool Cs2Optimized()
    {
        using var drs = new Drs();
        var p = drs.FindApp(Cs2Game.Exe);
        return p != IntPtr.Zero && Cs2Settings.All(s => drs.GetDword(p, s.Id) is { Own: true } v && v.Value == s.Value);
    }

    public static void OptimizeCs2()
    {
        var undo = new UndoLog();
        using var drs = new Drs();
        var name = ApplyTo(drs, Cs2Game, Cs2Settings, undo);
        undo.Save();   // undo data first, then save to the driver
        drs.Save();
        Log.Ok($"NVIDIA profile '{name}' (cs2.exe): prefer maximum performance, V-Sync forced off.");
    }

    public static void Optimize(bool globalPowerNormal)
    {
        var undo = new UndoLog();
        using var drs = new Drs();
        foreach (var g in BoostConfig.Load().Games)
        {
            var name = ApplyTo(drs, g, GameSettings, undo);
            Log.Ok($"NVIDIA profile '{name}' ({g.Exe}): prefer maximum performance, low latency mode on.");
        }

        var glob = drs.GlobalProfile();
        var globalName = drs.ProfileName(glob);
        var sc = drs.GetDword(glob, ShaderCache);
        if (sc.Value != uint.MaxValue)
        {
            undo.Add(new NvUndo { Profile = globalName, Id = ShaderCache, Own = sc.Own, Predefined = sc.Predefined, Prev = sc.Value });
            drs.SetDword(glob, ShaderCache, uint.MaxValue);
            Log.Ok("NVIDIA global: shader cache size unlimited.");
        }
        if (globalPowerNormal && drs.GetDword(glob, PowerMode) is { Own: true } gp)
        {
            undo.Add(new NvUndo { Profile = globalName, Id = PowerMode, Own = gp.Own, Predefined = gp.Predefined, Prev = gp.Value });
            drs.ResetSetting(glob, PowerMode);
            Log.Ok("NVIDIA global: power management back to the driver default (Normal).");
        }
        undo.Save();   // undo data first, then save to the driver
        drs.Save();
    }

    public static void Undo()
    {
        var undo = JsonFile.Read<List<NvUndo>>(AppPaths.NvProfilesUndo);
        if (undo is not { Count: > 0 }) return;
        using (var drs = new Drs())
        {
            foreach (var u in Enumerable.Reverse(undo))
            {
                var p = drs.FindProfile(u.Profile);
                if (p == IntPtr.Zero) continue;
                try
                {
                    if (u.Created) drs.DeleteProfile(p);
                    else if (!u.Own || u.Prev is null || u.Predefined) drs.ResetSetting(p, u.Id);
                    else drs.SetDword(p, u.Id, u.Prev.Value);
                }
                catch (InvalidOperationException e) { Log.Warn($"NVIDIA undo '{u.Profile}': {e.Message}"); }
            }
            drs.Save();
        }
        JsonFile.Delete(AppPaths.NvProfilesUndo);
        Log.Ok("NVIDIA profiles restored to the previous values.");
    }
}
