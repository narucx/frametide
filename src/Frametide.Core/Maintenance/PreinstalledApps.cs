using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;
using Windows.Management.Deployment;

namespace Frametide.Core.Maintenance;

public sealed record PreinstalledApp(string Name, bool Recommended, IReadOnlyList<string> PackageFullNames, IReadOnlyList<string> FamilyNames);

/// <summary>
/// Store apps that come with Windows and that many people never use. Removal is for all users, and the provisioned
/// copy is removed too, so new user accounts do not get the app again. Everything here can be reinstalled from the
/// Microsoft Store. Deliberately not in the list: Store, Xbox identity/services, Calculator, Photos, Notepad, Terminal,
/// Paint, Snipping Tool.
/// </summary>
public static class PreinstalledApps
{
    // "Recommended" = preselected by "Select recommended".
    private static readonly (string Pattern, string Name, bool Recommended)[] Known =
    [
        ("Clipchamp.Clipchamp", "Clipchamp (video editor)", true),
        ("Microsoft.BingNews", "Microsoft News", true),
        ("Microsoft.BingWeather", "Weather", false),
        ("Microsoft.BingSearch", "Bing Search", true),
        ("Microsoft.GetHelp", "Get Help", true),
        ("Microsoft.Getstarted", "Tips", true),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office ads)", true),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", true),
        ("Microsoft.People", "People", true),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", true),
        ("Microsoft.ZuneVideo", "Movies & TV", true),
        ("Microsoft.Todos", "Microsoft To Do", false),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", true),
        ("MicrosoftTeams", "Microsoft Teams (personal, old chat app)", true),
        ("MSTeams", "Microsoft Teams (new, also work/school)", false),
        ("Microsoft.549981C3F5F10", "Cortana", true),
        ("Microsoft.OutlookForWindows", "Outlook (new)", false),
        ("Microsoft.Windows.DevHome", "Dev Home", true),
        ("Microsoft.Copilot", "Copilot app", true),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", false),
        ("Microsoft.WindowsMaps", "Maps", true),
        ("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family", true),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", true),
        ("Microsoft.YourPhone", "Phone Link", false),
        ("Microsoft.WindowsSoundRecorder", "Sound Recorder", false),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar (Win+G)", false),
        ("Disney.37853FC22B2CE", "Disney+", true),
        ("SpotifyAB.SpotifyMusic", "Spotify (Store version)", false),
        ("king.com.CandyCrush*", "Candy Crush", true),
        ("7EE7776C.LinkedInforWindows", "LinkedIn", true),
    ];

    /// <summary>Package name pattern: exact name, or "*" as wildcard.</summary>
    internal static bool Matches(string pattern, string packageName) => pattern.Contains('*')
        ? Regex.IsMatch(packageName, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase)
        : packageName.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>The known apps that are installed for any user or provisioned for new users (needs admin rights).</summary>
    public static IReadOnlyList<PreinstalledApp> Find()
    {
        var pm = new PackageManager();
        var installed = pm.FindPackages()
            .Where(p => !p.IsFramework && p.SignatureKind != global::Windows.ApplicationModel.PackageSignatureKind.System)
            .Select(p => (p.Id.Name, p.Id.FullName, p.Id.FamilyName)).ToList();
        var provisioned = pm.FindProvisionedPackages().Select(p => (p.Id.Name, p.Id.FullName, p.Id.FamilyName)).ToList();
        var result = new List<PreinstalledApp>();
        foreach (var (pattern, name, recommended) in Known)
        {
            var packages = installed.Where(p => Matches(pattern, p.Name)).Select(p => p.FullName).Distinct().ToList();
            var families = installed.Concat(provisioned).Where(p => Matches(pattern, p.Name)).Select(p => p.FamilyName).Distinct().ToList();
            if (packages.Count > 0 || families.Count > 0) result.Add(new PreinstalledApp(name, recommended, packages, families));
        }
        return result;
    }

    public static void Remove(IEnumerable<PreinstalledApp> apps)
    {
        var pm = new PackageManager();
        foreach (var app in apps)
        {
            // Every package on its own: one failure must not leave the rest of the app (or the other apps) untouched.
            // Provisioned copy first, otherwise Windows installs the app again for the next new account.
            var failed = 0;
            foreach (var family in app.FamilyNames)
                if (!Try(app.Name, family, () => Wait(pm.DeprovisionPackageForAllUsersAsync(family).AsTask()))) failed++;
            foreach (var full in app.PackageFullNames)
                if (!Try(app.Name, full, () => Wait(pm.RemovePackageAsync(full, RemovalOptions.RemoveForAllUsers).AsTask()))) failed++;
            if (failed == 0) Log.Ok($"Removed: {app.Name}.");
        }
    }

    private static bool Try(string app, string package, Action action)
    {
        try { action(); return true; }
        catch (Exception e) { Log.Warn($"{app} ({package}): {e.Message}"); return false; }
    }

    private static void Wait(Task<DeploymentResult> operation)
    {
        var result = operation.GetAwaiter().GetResult();
        if (result.ExtendedErrorCode is { } error) throw new InvalidOperationException($"{result.ErrorText} (0x{error.HResult:X8})");
    }
}
