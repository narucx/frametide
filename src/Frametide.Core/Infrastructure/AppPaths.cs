using System.Security.AccessControl;
using System.Security.Principal;

namespace Frametide.Core.Infrastructure;

/// <summary>
/// Where Frametide keeps its data. Default: C:\ProgramData\Frametide, writable for administrators and SYSTEM only.
/// The elevated app writes registry values from the journal on "Revert"; if standard users could edit the journal,
/// that would be a privilege escalation.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Frametide");

    public static string Journal => Path.Combine(DataDir, "journal.json");
    public static string Config => Path.Combine(DataDir, "config.json");
    public static string GpuProfiles => Path.Combine(DataDir, "gpu-profiles.json");
    public static string BoostState => Path.Combine(DataDir, "boost-state.json");
    public static string IfeoState => Path.Combine(DataDir, "ifeo-state.json");
    public static string NvProfilesUndo => Path.Combine(DataDir, "nv-profiles.json");
    public static string LogFile => Path.Combine(DataDir, "frametide.log");
    public static string BenchDir => Path.Combine(DataDir, "bench");

    /// <summary>Use another data folder (tests). Does not change permissions.</summary>
    public static void UseDataDir(string dir)
    {
        DataDir = dir;
        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Creates the data folder with restricted permissions if it does not exist yet. Any user can create folders in
    /// ProgramData: a folder that someone else created before Frametide is moved aside, its content is not trusted.
    /// Checked again after creating, because a user can create the folder in between; throws if it stays untrusted.
    /// </summary>
    public static void EnsureDataDir()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(DataDir)) Create();
            if (Windows.AdminOnly.Problem(DataDir, recursive: false) is not { } problem) return;
            if (attempt == 3) throw new UnauthorizedAccessException($"The data folder {DataDir} can be changed by standard users ({problem}).");
            var aside = $"{DataDir}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            Directory.Move(DataDir, aside);
            Log.Warn($"The data folder could be changed by standard users ({problem}). Moved it to {aside} and started a new one.");
        }
    }

    private static void Create()
    {
        var security = new DirectorySecurity();
        // Explicit owner: with "Default owner = object creator" (hardened PCs) it would be the user, who could then
        // change the permissions without elevation.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DataDir).Create(security);
    }
}
