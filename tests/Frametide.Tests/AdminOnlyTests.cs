using System.Security.AccessControl;
using Frametide.Core.Windows;

namespace Frametide.Tests;

public sealed class AdminOnlyTests
{
    [Theory]
    // Like Program Files: admins, SYSTEM and TrustedInstaller change it, users read; CREATOR OWNER only for new entries.
    [InlineData("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;GA;;;CO)", true, true)]
    [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)", true, true)]
    // Owned by a user: the owner can always change the permissions.
    [InlineData("O:S-1-5-21-1-2-3-1001D:(A;;FA;;;BA)", true, false)]
    // Users may write.
    [InlineData("O:BAD:(A;;FA;;;BA)(A;;0x1301bf;;;BU)", true, false)]
    [InlineData("O:BAD:(A;;FA;;;BA)(A;;FW;;;AU)", true, false)]
    // No access list at all = everyone has full access.
    [InlineData("O:BAD:NO_ACCESS_CONTROL", true, false)]
    // Like C:\: authenticated users may add folders, which is harmless above the program folder ...
    [InlineData("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)(A;;0x4;;;AU)(A;OICIIO;SDGXGWGR;;;AU)", false, true)]
    // ... but not in the program folder itself.
    [InlineData("O:BAD:(A;;FA;;;BA)(A;;0x4;;;AU)", true, false)]
    // Above the program folder, deleting or renaming entries is not harmless.
    [InlineData("O:BAD:(A;;FA;;;BA)(A;;0x40;;;BU)", false, false)]
    public void Access_lists_are_judged(string sddl, bool contents, bool protectedExpected) =>
        Assert.Equal(protectedExpected, AdminOnly.Check("x", new RawSecurityDescriptor(sddl), contents) is null);

    [Fact]
    public void System_folder_is_protected()
    {
        var etc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc");
        Assert.Null(AdminOnly.Problem(etc));
    }

    [Fact]
    public void Folder_created_by_the_user_is_not_protected()
    {
        var dir = Directory.CreateTempSubdirectory("frametide-acl-");
        try { Assert.NotNull(AdminOnly.Problem(dir.FullName)); }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Missing_folder_is_not_protected() =>
        Assert.Contains("does not exist", AdminOnly.Problem(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
}
