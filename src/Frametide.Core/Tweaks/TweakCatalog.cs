using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Core.Tweaks;

/// <summary>All tweaks and repairs. IDs are keys in the journal and in profiles: never rename them.</summary>
public static class TweakCatalog
{
    private const string Adv = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string MemMgmt = @"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Kernel = @"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string Ifeo = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string DxPrefs = @"HKCU:\Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ClassicMenu = @"HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private static readonly Regex OffValue = new("^(Disabled|Off|Aus|Deaktiviert|Disable)$", RegexOptions.IgnoreCase);

    private static RegSetting Dword(string path, string name, long value) => new(path, name, RegistryValueKind.DWord, value);
    private static RegSetting Text(string path, string name, string value) => new(path, name, RegistryValueKind.String, value);

    public static readonly IReadOnlyList<Tweak> Tweaks =
    [
        // ------------------------------------------------------------ Gaming
        new RegistryTweak
        {
            Id = "gaming.gamedvr", Category = "Gaming", Name = "Game DVR / background recording off", Recommended = true,
            Description = "Xbox background recording costs performance. Sets GameDVR_Enabled, AppCaptureEnabled and HistoricalCaptureEnabled to 0.",
            Registry =
            [
                Dword(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled", 0),
                Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
                Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0),
            ],
        },
        new RegistryTweak
        {
            Id = "gaming.gamemode", Category = "Gaming", Name = "Windows Game Mode on", Recommended = true,
            Description = "Game Mode prioritizes the game and pauses Windows Update installs while you play. Default on Windows 11.",
            Registry = [Dword(@"HKCU:\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1), Dword(@"HKCU:\Software\Microsoft\GameBar", "AllowAutoGameMode", 1)],
        },
        new RegistryTweak
        {
            Id = "gaming.gamebar_tips", Category = "Gaming", Name = "Game Bar startup tip off", Recommended = true,
            Description = "Hides the \"Press Win+G\" popup when a game starts.",
            Registry = [Dword(@"HKCU:\Software\Microsoft\GameBar", "ShowStartupPanel", 0)],
        },
        new RegistryTweak
        {
            Id = "gaming.hags", Category = "Gaming", Name = "Hardware-accelerated GPU scheduling (HAGS) on", Recommended = true, Restart = RestartNeed.Reboot,
            Description = "Required for DLSS Frame Generation, recommended for RTX 40. HwSchMode=2. Needs a reboot.",
            Registry = [Dword(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)],
        },
        new CustomTweak
        {
            Id = "gaming.windowed_opt", Category = "Gaming", Name = "Optimizations for windowed games", Recommended = true,
            Description = "Modern flip-model presentation for DX10/11 games in (borderless) windows: lower latency, VRR/G-Sync works.",
            Status = () => (Reg.Get(DxPrefs, "DirectXUserGlobalSettings").Value as string ?? "").Contains("SwapEffectUpgradeEnable=1;")
                ? TweakStatus.Applied : TweakStatus.NotApplied,
            ApplyAction = j =>
            {
                var cur = Reg.Get(DxPrefs, "DirectXUserGlobalSettings");
                j.SaveOriginal("gaming.windowed_opt", "value", new JsonObject { ["Exists"] = cur.Exists, ["Value"] = cur.Value as string ?? "" });
                var v = Regex.Replace(cur.Value as string ?? "", @"SwapEffectUpgradeEnable=\d;", "");
                Reg.Set(DxPrefs, "DirectXUserGlobalSettings", RegistryValueKind.String, v + "SwapEffectUpgradeEnable=1;");
            },
            RevertAction = j =>
            {
                var o = j.GetOriginal("gaming.windowed_opt", "value");
                if (o?["Exists"]?.GetValue<bool>() == true) Reg.Set(DxPrefs, "DirectXUserGlobalSettings", RegistryValueKind.String, (string?)o["Value"] ?? "");
                else Reg.Remove(DxPrefs, "DirectXUserGlobalSettings");
            },
        },
        new RegistryTweak
        {
            Id = "gaming.mouse_accel", Category = "Gaming", Name = "Mouse acceleration off (\"Enhance pointer precision\")", Recommended = true, Restart = RestartNeed.SignOut,
            Description = "Consistent aim, 1:1 to your mouse movement. Raw input in games is not affected, the desktop is.",
            Registry = [Text(@"HKCU:\Control Panel\Mouse", "MouseSpeed", "0"), Text(@"HKCU:\Control Panel\Mouse", "MouseThreshold1", "0"), Text(@"HKCU:\Control Panel\Mouse", "MouseThreshold2", "0")],
        },
        new RegistryTweak
        {
            Id = "gaming.sticky_keys", Category = "Gaming", Name = "Sticky Keys shortcut (5x Shift) off", Recommended = true, Restart = RestartNeed.SignOut,
            Description = "Prevents the popup when you press Shift a lot in game. Sticky Keys itself stays available.",
            Registry = [Text(@"HKCU:\Control Panel\Accessibility\StickyKeys", "Flags", "506")],
        },

        // ------------------------------------------------------------ Power
        new CustomTweak
        {
            Id = "power.usb_suspend", Category = "Power", Name = "USB selective suspend off (all power plans)", Recommended = true,
            Description = "Stops Windows from putting USB ports (mouse, keyboard, headset) to sleep. Prevents dropouts and polling issues.",
            Status = () => PowerCfg.GetSetting("SCHEME_CURRENT", PowerCfg.UsbSubgroup, PowerCfg.UsbSelectiveSuspend) switch
            {
                null => TweakStatus.NotAvailable,
                { Ac: 0 } => TweakStatus.Applied,
                _ => TweakStatus.NotApplied,
            },
            ApplyAction = j =>
            {
                foreach (var scheme in PowerCfg.GetSchemes())
                {
                    if (PowerCfg.GetSetting(scheme.Guid, PowerCfg.UsbSubgroup, PowerCfg.UsbSelectiveSuspend) is not { } s) continue;
                    j.SaveOriginal("power.usb_suspend", scheme.Guid, new JsonObject { ["AC"] = s.Ac, ["DC"] = s.Dc });
                    PowerCfg.SetSetting(scheme.Guid, PowerCfg.UsbSubgroup, PowerCfg.UsbSelectiveSuspend, 0, 0);
                }
                PowerCfg.Refresh();
            },
            RevertAction = j =>
            {
                if (j.Get("power.usb_suspend") is not { } entry) return;
                foreach (var (guid, v) in entry)
                    PowerCfg.SetSetting(guid, PowerCfg.UsbSubgroup, PowerCfg.UsbSelectiveSuspend, v?["AC"]?.GetValue<int>() ?? 1, v?["DC"]?.GetValue<int>() ?? 1);
                PowerCfg.Refresh();
            },
        },
        new RegistryTweak
        {
            Id = "power.fast_startup", Category = "Power", Name = "Fast Startup off", Recommended = true,
            Description = "Fast Startup is a half-hibernation: drivers and kernel are never fully reloaded. Without it, \"Shut down\" is a real fresh start.",
            Registry = [Dword(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0)],
        },

        // ------------------------------------------------------------ Network
        // "Idle power down restriction" must not be matched: setting it to Disabled ALLOWS powering down.
        NicPropertyTweak("net.eee", "Network adapter: power saving off", recommended: true, Risk.Safe,
            "Energy Efficient Ethernet, Green Ethernet, Gigabit Lite and power saving off on the active adapter. Against short latency spikes. The adapter restarts briefly.",
            new Regex("Energy.?Efficient|Green.?Ethernet|Energieeffizient|Advanced EEE|Gigabit Lite|Power Saving|Energiespar|Ultra Low Power|Idle Power|Power Down", RegexOptions.IgnoreCase),
            exclude: new Regex("restriction|Einschr", RegexOptions.IgnoreCase)),
        new CustomTweak
        {
            Id = "net.nic_power", Category = "Network", Name = "Network adapter may not be turned off", Recommended = true,
            Description = "Removes \"Allow the computer to turn off this device to save power\" for the active adapter (like unchecking it in Device Manager, which also turns off Wake-on-LAN). The adapter restarts briefly.",
            // PnPCapabilities in the driver key, 0x10 = no power management, 0x08 = no wake (what Device Manager writes).
            Status = () =>
            {
                var adapters = NetworkAdapters.GetConnectedPhysical();
                if (adapters.Count == 0) return TweakStatus.NotAvailable;
                return adapters.All(a => (Convert.ToInt32(Reg.Get(a.RegPath, "PnPCapabilities").Value ?? 0) & 0x10) != 0) ? TweakStatus.Applied : TweakStatus.NotApplied;
            },
            ApplyAction = j =>
            {
                foreach (var a in NetworkAdapters.GetConnectedPhysical())
                {
                    var cur = Reg.Get(a.RegPath, "PnPCapabilities");
                    var value = Convert.ToInt32(cur.Value ?? 0);
                    if ((value & 0x10) != 0) continue;
                    j.SaveOriginal("net.nic_power", $"pnp|{a.RegPath}", new JsonObject { ["Exists"] = cur.Exists, ["Value"] = cur.Exists ? value : null });
                    Reg.Set(a.RegPath, "PnPCapabilities", RegistryValueKind.DWord, value | 0x18);
                    NetworkAdapters.Restart(a);
                }
            },
            RevertAction = j =>
            {
                if (j.Get("net.nic_power") is not { } entry) return;
                foreach (var (key, v) in entry)
                {
                    if (!key.StartsWith("pnp|", StringComparison.Ordinal)) continue;     // only driver key entries
                    var path = key[4..];
                    if (v?["Exists"]?.GetValue<bool>() == true) Reg.Set(path, "PnPCapabilities", RegistryValueKind.DWord, v["Value"]!.GetValue<long>());
                    else Reg.Remove(path, "PnPCapabilities");
                }
                foreach (var a in NetworkAdapters.GetConnectedPhysical()) NetworkAdapters.Restart(a);
            },
        },
        NicPropertyTweak("net.interrupt_moderation", "Network adapter: interrupt moderation off", recommended: false, Risk.Moderate,
            "Every packet triggers an interrupt immediately. Marginally lower latency, more CPU/DPC load. Measurable effect is usually in the microsecond range.",
            new Regex("Interrupt Moderation$|Interruptmoderation$", RegexOptions.IgnoreCase)),
        NicPropertyTweak("net.flow_control", "Network adapter: flow control off", recommended: false, Risk.Safe,
            "Pause frames off. Neutral to slightly positive for gaming.",
            new Regex("Flow ?Control|Flusssteuerung", RegexOptions.IgnoreCase)),

        // ------------------------------------------------------------ Privacy
        new RegistryTweak
        {
            Id = "privacy.telemetry", Category = "Privacy", Name = "Minimize telemetry", Recommended = true,
            Description = "Diagnostic data set to \"Required\" (policy AllowTelemetry=1) and the \"Connected User Experiences and Telemetry\" service (DiagTrack) disabled.",
            Registry = [Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1)],
            Services = [new ServiceSetting("DiagTrack", StartMode.Disabled)],
        },
        new RegistryTweak
        {
            Id = "privacy.adid", Category = "Privacy", Name = "Advertising ID and personalized ads off", Recommended = true,
            Description = "No advertising ID for apps, no \"tailored experiences\" based on diagnostic data.",
            Registry =
            [
                Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
                Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            ],
        },
        new RegistryTweak
        {
            Id = "privacy.activity", Category = "Privacy", Name = "Activity history off", Recommended = true,
            Description = "Windows no longer stores and syncs a list of the apps and files you opened.",
            Registry =
            [
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            ],
        },
        new RegistryTweak
        {
            Id = "privacy.feedback", Category = "Privacy", Name = "Feedback surveys off", Recommended = true,
            Description = "No \"How likely are you to recommend Windows\" popups.",
            Registry = [Dword(@"HKCU:\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)],
        },
        new RegistryTweak
        {
            Id = "privacy.websearch", Category = "Privacy", Name = "Bing web search in Start menu off", Recommended = true,
            Description = "Start menu search only searches your PC. Faster and without Bing results.",
            Registry =
            [
                Dword(@"HKCU:\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
            ],
        },
        new RegistryTweak
        {
            Id = "privacy.background_apps", Category = "Privacy", Name = "Background apps (Store apps) off globally", Risk = Risk.Moderate,
            Description = "Store apps no longer run in the background. Can block notifications from e.g. Phone Link or the Mail app.",
            Registry = [Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)],
        },

        // ------------------------------------------------------------ Ads & AI
        new RegistryTweak
        {
            Id = "ads.suggestions", Category = "Ads & AI", Name = "Suggestions, tips and app ads off", Recommended = true,
            Description = "ContentDeliveryManager: no silently installed apps, no Start menu suggestions, no tips, no ads in Settings or on the lock screen.",
            Registry =
            [
                .. new[]
                {
                    "SilentInstalledAppsEnabled", "SystemPaneSuggestionsEnabled", "SoftLandingEnabled", "RotatingLockScreenOverlayEnabled",
                    "SubscribedContent-310093Enabled", "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled",
                    "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled",
                }.Select(n => Dword(Cdm, n, 0)),
            ],
        },
        new RegistryTweak
        {
            Id = "ads.start_reco", Category = "Ads & AI", Name = "Start menu recommendations and Explorer ads off", Recommended = true,
            Description = "No app recommendations in Start, no OneDrive/Microsoft 365 notices in Explorer.",
            Registry = [Dword(Adv, "Start_IrisRecommendations", 0), Dword(Adv, "ShowSyncProviderNotifications", 0)],
        },
        new RegistryTweak
        {
            Id = "ai.copilot", Category = "Ads & AI", Name = "Windows Copilot off", Recommended = true,
            Description = "Copilot policy off and taskbar button hidden.",
            Registry = [Dword(@"HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1), Dword(Adv, "ShowCopilotButton", 0)],
        },
        new RegistryTweak
        {
            Id = "ai.recall", Category = "Ads & AI", Name = "Recall and AI screen analysis off", Recommended = true,
            Description = "Recall may not save screen snapshots, Click to Do off.",
            Registry =
            [
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0),
                Dword(@"HKCU:\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                Dword(@"HKCU:\Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
            ],
        },
        new RegistryTweak
        {
            Id = "ads.widgets", Category = "Ads & AI", Name = "Widgets (news feed) off", Recommended = true,
            Description = "Removes the Widgets board including the news feed from the taskbar.",
            Registry = [Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
            // Without the "Windows Web Experience Pack" there are no widgets at all, nothing to turn off.
            StatusOverride = () => Reg.Equals(@"HKLM:\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegistryValueKind.DWord, 0) ? null
                : Appx.IsInstalledForCurrentUser("MicrosoftWindows.Client.WebExperience") ? null : TweakStatus.NotAvailable,
            // Current Windows 11 builds deny write access to this policy key, even as administrator.
            BlockedHint = "Windows blocks this setting for scripts. Set it yourself: gpedit.msc > Computer Configuration > Administrative Templates > Windows Components > Widgets > \"Allow Widgets\" = Disabled.",
        },
        new RegistryTweak
        {
            Id = "ads.edge", Category = "Ads & AI", Name = "Edge in background and Edge ads off", Recommended = true,
            Description = "Edge does not keep running after closing (Startup Boost, background mode). No sidebar, no shopping assistant, no recommendations.",
            Registry =
            [
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled", 0),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience", 1),
                Dword(@"HKLM:\SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0),
            ],
        },

        // ------------------------------------------------------------ Explorer & UI
        new RegistryTweak
        {
            Id = "ui.file_ext", Category = "Interface", Name = "Show file extensions", Recommended = true,
            Description = "Shows .exe, .txt etc. Also helps against disguised malware (e.g. \"picture.jpg.exe\").",
            Registry = [Dword(Adv, "HideFileExt", 0)],
        },
        new RegistryTweak
        {
            Id = "ui.end_task", Category = "Interface", Name = "Taskbar: \"End task\" on right-click", Recommended = true,
            Description = "Kill frozen games straight from the taskbar, no Task Manager needed.",
            Registry = [Dword(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1)],
        },
        new CustomTweak
        {
            Id = "ui.classic_context", Category = "Interface", Name = "Classic right-click menu (Windows 10 style)", Restart = RestartNeed.SignOut,
            Description = "Full context menu without \"Show more options\". Matter of taste. Takes effect after restarting Explorer.",
            Status = () => Reg.KeyExists(ClassicMenu + @"\InprocServer32") ? TweakStatus.Applied : TweakStatus.NotApplied,
            ApplyAction = _ => Reg.Set(ClassicMenu + @"\InprocServer32", "", RegistryValueKind.String, ""),
            RevertAction = _ => Reg.DeleteKeyTree(ClassicMenu),
        },
        new RegistryTweak
        {
            Id = "ui.hidden_files", Category = "Interface", Name = "Show hidden files",
            Description = "Shows e.g. AppData in Explorer.",
            Registry = [Dword(Adv, "Hidden", 1)],
        },
        new RegistryTweak
        {
            Id = "ui.this_pc", Category = "Interface", Name = "Explorer opens \"This PC\"",
            Description = "Instead of Home/Quick access.",
            Registry = [Dword(Adv, "LaunchTo", 1)],
        },
        new RegistryTweak
        {
            Id = "ui.transparency", Category = "Interface", Name = "Transparency effects off",
            Description = "Purely visual, slightly less DWM load.",
            Registry = [Dword(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
        },
    ];

    /// <summary>
    /// Advanced driver properties of the active adapters set to their "off" option. Journal key "(adapter)|(keyword)"
    /// with the previous value.
    /// </summary>
    private static CustomTweak NicPropertyTweak(string id, string name, bool recommended, Risk risk, string description, Regex property, Regex? exclude = null) => new()
    {
        Id = id, Category = "Network", Name = name, Recommended = recommended, Risk = risk, Description = description,
        Status = () =>
        {
            var targets = NetworkAdapters.FindTargets(property, OffValue, exclude).ToList();
            if (targets.Count == 0) return TweakStatus.NotAvailable;
            return targets.All(t => t.Property.Value == t.TargetValue) ? TweakStatus.Applied : TweakStatus.NotApplied;
        },
        ApplyAction = j =>
        {
            var restart = new Dictionary<string, NetworkAdapter>();
            foreach (var (adapter, prop, target) in NetworkAdapters.FindTargets(property, OffValue, exclude))
            {
                if (prop.Value == target) continue;
                j.SaveOriginal(id, $"{adapter.Name}|{prop.Keyword}", prop.Value ?? "");
                NetworkAdapters.SetProperty(adapter, prop.Keyword, target);
                Log.Info($"{adapter.Name}: '{prop.DisplayName}' -> {prop.Options[target]}");
                restart[adapter.ClassKey] = adapter;
            }
            foreach (var a in restart.Values) NetworkAdapters.Restart(a);
        },
        RevertAction = j =>
        {
            if (j.Get(id) is not { } entry) return;
            var adapters = NetworkAdapters.GetConnectedPhysical();
            var restart = new Dictionary<string, NetworkAdapter>();
            foreach (var (key, v) in entry)
            {
                var parts = key.Split('|', 2);
                if (parts.Length != 2 || adapters.FirstOrDefault(a => a.Name == parts[0]) is not { } adapter) continue;
                NetworkAdapters.SetProperty(adapter, parts[1], (string?)v ?? "");
                restart[adapter.ClassKey] = adapter;
            }
            foreach (var a in restart.Values) NetworkAdapters.Restart(a);
        },
    };

    // ================================================================ Repairs (harmful tweaks from other tools)

    private static readonly string[] BcdTimers = ["useplatformclock", "disabledynamictick", "useplatformtick", "tscsyncpolicy"];
    private static readonly string[] TimerServices = ["SetTimerResolutionService", "STR"];

    public static readonly IReadOnlyList<Repair> Repairs =
    [
        new Repair
        {
            Id = "repair.bcd", Name = "BCD timer entries",
            Description = "useplatformclock, disabledynamictick, useplatformtick and tscsyncpolicy usually make frametimes worse on modern CPUs.",
            Detect = () =>
            {
                var output = NativeProcess.Run("bcdedit.exe", ["/enum", "{current}"]).Output;
                var hits = BcdTimers.Where(v => Regex.IsMatch(output, $@"(?im)^{v}\s")).ToList();
                return hits.Count > 0 ? $"Set: {string.Join(", ", hits)}" : null;
            },
            Fix = () => { foreach (var v in BcdTimers) NativeProcess.Run("bcdedit.exe", ["/deletevalue", "{current}", v]); },
        },
        new Repair
        {
            Id = "repair.hpet", Name = "HPET disabled in Device Manager",
            Description = "The High Precision Event Timer should stay enabled. Windows decides on its own which timer to use.",
            Detect = () => Devices.GetInstanceIds(@"ACPI\PNP0103").Any(d => Devices.GetProblem(d) == Devices.ProblemDisabled) ? "HPET is disabled" : null,
            Fix = () =>
            {
                foreach (var d in Devices.GetInstanceIds(@"ACPI\PNP0103"))
                    if (Devices.GetProblem(d) == Devices.ProblemDisabled) Devices.Enable(d);
            },
        },
        new Repair
        {
            Id = "repair.spectre", Name = "CPU vulnerability mitigations (Spectre/Meltdown) disabled",
            Description = "FeatureSettingsOverride is set. Hardly any FPS gain on current CPUs, but a real security risk.",
            Detect = () => Reg.Get(MemMgmt, "FeatureSettingsOverride").Exists ? "FeatureSettingsOverride is set" : null,
            Fix = () => { Reg.Remove(MemMgmt, "FeatureSettingsOverride"); Reg.Remove(MemMgmt, "FeatureSettingsOverrideMask"); },
        },
        new Repair
        {
            Id = "repair.threaded_dpc", Name = "Threaded DPCs disabled",
            Description = "ThreadDpcEnable=0 is an old tweak with no proven benefit. Can disturb driver timing.",
            Detect = () => Reg.Get(Kernel, "ThreadDpcEnable") is { Exists: true, Value: 0 } ? "ThreadDpcEnable = 0" : null,
            Fix = () => Reg.Remove(Kernel, "ThreadDpcEnable"),
        },
        new Repair
        {
            Id = "repair.timer_service", Name = "Timer resolution tools in autostart",
            Description = "SetTimerResolutionService/ISLC permanently force a 0.5 ms timer resolution. Mostly ineffective since Windows 11, but costs power.",
            Detect = () =>
            {
                var found = TimerServices.Where(s => Services.GetStartMode(s) is not null).Concat(TimerProcesses().Select(p => p.ProcessName)).Distinct().ToList();
                return found.Count > 0 ? string.Join(", ", found) : null;
            },
            Fix = () =>
            {
                foreach (var p in TimerProcesses()) { try { p.Kill(); } catch (InvalidOperationException) { } }
                foreach (var s in TimerServices.Where(s => Services.GetStartMode(s) is not null))
                {
                    NativeProcess.Run("sc.exe", ["stop", s]);
                    NativeProcess.Run("sc.exe", ["delete", s]);
                }
            },
        },
        new Repair
        {
            Id = "repair.sysmain", Name = "SysMain (Superfetch) disabled",
            Description = "SysMain speeds up app and game launches from SSDs. Turning it off gains nothing.",
            Detect = () => Services.GetStartMode("SysMain") == StartMode.Disabled ? "SysMain is disabled" : null,
            Fix = () => { Services.SetStartMode("SysMain", StartMode.Automatic); NativeProcess.Run("sc.exe", ["start", "SysMain"]); },
        },
        new Repair
        {
            Id = "repair.prefetch", Name = "Prefetcher turned off",
            Description = "EnablePrefetcher should be 3 (Windows default).",
            Detect = () => Reg.Get(MemMgmt + @"\PrefetchParameters", "EnablePrefetcher") is { Exists: true } v && Convert.ToInt64(v.Value) != 3 ? $"EnablePrefetcher = {v.Value}" : null,
            Fix = () => Reg.Set(MemMgmt + @"\PrefetchParameters", "EnablePrefetcher", RegistryValueKind.DWord, 3),
        },
        new Repair
        {
            Id = "repair.tcp_autotuning", Name = "TCP receive window auto-tuning changed",
            Description = "If auto-tuning is not \"normal\", downloads get slower (e.g. Steam). It has no effect on in-game ping (UDP).",
            Detect = () =>
            {
                // "Receive Window Auto-Tuning Level : normal" (the label is localized, the value is not).
                var output = NativeProcess.Run("netsh.exe", ["interface", "tcp", "show", "global"]).Output;
                var m = Regex.Match(output, @"(?im)^.*(auto.?tuning|abstimmung).*:\s*(\w+)\s*$");
                return m.Success && !m.Groups[2].Value.Equals("normal", StringComparison.OrdinalIgnoreCase) ? $"AutoTuningLevel = {m.Groups[2].Value}" : null;
            },
            Fix = () => NativeProcess.Run("netsh.exe", ["interface", "tcp", "set", "global", "autotuninglevel=normal"]),
        },
        new Repair
        {
            Id = "repair.mpo", Name = "Multi-Plane Overlay (MPO) disabled",
            Description = "OverlayTestMode=5 disables MPO. That only helps against flickering on old drivers and otherwise increases latency in windowed mode. Only remove it if you did not set it on purpose.",
            Detect = () => Reg.Get(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode") is { Exists: true } v ? $"OverlayTestMode = {v.Value}" : null,
            Fix = () => Reg.Remove(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode"),
        },
        new Repair
        {
            Id = "repair.input_queue", Name = "Mouse/keyboard buffer size changed",
            Description = "A too small MouseDataQueueSize/KeyboardDataQueueSize can swallow inputs. Windows default is 100.",
            Detect = () =>
            {
                var issues = new List<string>();
                if (Reg.Get(@"HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize") is { Exists: true } m && Convert.ToInt64(m.Value) != 100) issues.Add($"Mouse = {m.Value}");
                if (Reg.Get(@"HKLM:\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize") is { Exists: true } k && Convert.ToInt64(k.Value) != 100) issues.Add($"Keyboard = {k.Value}");
                return issues.Count > 0 ? string.Join(", ", issues) : null;
            },
            Fix = () =>
            {
                Reg.Set(@"HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", RegistryValueKind.DWord, 100);
                Reg.Set(@"HKLM:\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", RegistryValueKind.DWord, 100);
            },
        },
        new Repair
        {
            Id = "repair.csrss", Name = "csrss.exe priority overridden",
            Description = "IFEO PerfOptions for csrss.exe is a placebo tweak and can make the system unstable.",
            Detect = () => Reg.KeyExists(Ifeo + @"\csrss.exe\PerfOptions") ? "PerfOptions set for csrss.exe" : null,
            Fix = () => Reg.DeleteKeyTree(Ifeo + @"\csrss.exe\PerfOptions"),
        },
        new Repair
        {
            Id = "repair.wer", Name = "Windows Error Reporting disabled",
            Description = "Without WER there are no crash reports. You need those when games or drivers crash (e.g. while undervolting).",
            Detect = () => Services.GetStartMode("WerSvc") == StartMode.Disabled ? "WerSvc is disabled" : null,
            Fix = () => Services.SetStartMode("WerSvc", StartMode.Manual),
        },
    ];


    private static IEnumerable<Process> TimerProcesses() => Process.GetProcesses().Where(p =>
        p.ProcessName.Equals("ISLC", StringComparison.OrdinalIgnoreCase) ||
        p.ProcessName.Equals("TimerResolution", StringComparison.OrdinalIgnoreCase) ||
        p.ProcessName.StartsWith("Intelligent standby list cleaner", StringComparison.OrdinalIgnoreCase));
}
