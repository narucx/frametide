using System.Security.AccessControl;
using System.Security.Principal;

namespace Frametide.Core.Windows;

/// <summary>
/// Checks that only administrators can change a folder. The elevated app must not start programs from, or trust data
/// in, places a standard user (or malware running as the user) can change: that would be a silent way to admin rights.
/// </summary>
public static class AdminOnly
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),   // NT SERVICE\TrustedInstaller
        new("S-1-3-4"),                                                           // OWNER RIGHTS (the owner is checked itself)
    ];

    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    /// <summary>Rights that change an entry or add to a folder.</summary>
    private const int Change = (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership) | GenericAll | GenericWrite;

    /// <summary>Above the folder adding entries is harmless; renaming, deleting or re-permissioning one is not.</summary>
    private const int Replace = (int)(FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | GenericAll | GenericWrite;

    /// <summary>
    /// Null when only administrators can change the folder (with <paramref name="recursive"/>: also everything in it)
    /// and the folders above it cannot be swapped; otherwise what is wrong, for the log.
    /// </summary>
    public static string? Problem(string folder, bool recursive = true)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(folder));
        if (!dir.Exists) return $"{dir.FullName} does not exist";
        try
        {
            var entries = recursive
                ? dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }).Prepend(dir)
                : [dir];
            foreach (var entry in entries)
                if (Check(entry, Change) is { } problem) return problem;
            for (var parent = dir.Parent; parent is not null; parent = parent.Parent)
                if (Check(parent, Replace) is { } problem) return problem;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return $"{dir.FullName}: {e.Message}"; }
        return null;
    }

    /// <summary>Whether the folder of a program and everything in it can only be changed by administrators.</summary>
    public static bool IsProtectedProgram(string exePath) => ProgramProblem(exePath) is null;

    public static string? ProgramProblem(string exePath) =>
        Path.GetDirectoryName(Path.GetFullPath(exePath)) is { } dir ? Problem(dir) : $"{exePath} has no folder";

    private static string? Check(FileSystemInfo entry, int rights)
    {
        // Links can point anywhere, and the target is what gets started.
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{entry.FullName} is a link";
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
        FileSystemSecurity security = entry is DirectoryInfo d ? d.GetAccessControl(sections) : ((FileInfo)entry).GetAccessControl(sections);
        return Check(entry.FullName, new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0), contents: rights == Change);
    }

    /// <param name="contents">The entry itself or what is in it must not change (otherwise: only its place).</param>
    internal static string? Check(string name, RawSecurityDescriptor sd, bool contents)
    {
        // The owner can always change the permissions.
        if (sd.Owner is null || !IsTrusted(sd.Owner)) return $"{name} is owned by {Name(sd.Owner)}";
        if (sd.DiscretionaryAcl is null) return $"{name} has no access list (everyone has full access)";
        var rights = contents ? Change : Replace;
        foreach (var ace in sd.DiscretionaryAcl.OfType<QualifiedAce>())
        {
            if (ace.AceQualifier != AceQualifier.AccessAllowed || ace.AceFlags.HasFlag(AceFlags.InheritOnly)) continue;
            if ((ace.AccessMask & rights) != 0 && !IsTrusted(ace.SecurityIdentifier))
                return $"{Name(ace.SecurityIdentifier)} can change {name}";
        }
        return null;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => Trusted.Contains(sid);

    private static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "nobody";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
    }
}
