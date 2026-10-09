using System.Diagnostics;
using Frametide.Core.Windows;

namespace Frametide.Tests;

public sealed class SafeDeleteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ft-clean-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("ft-outside-").FullName;

    public void Dispose()
    {
        var link = Path.Combine(_root, "sub");
        if (Directory.Exists(link)) Directory.Delete(link);   // only the junction, not the target's content
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void Deletes_files_inside_the_folder()
    {
        var file = Path.Combine(_root, "a.tmp");
        File.WriteAllText(file, "12345");
        Assert.Equal(5, SafeDelete.Delete(file, SafeDelete.RealRoot(_root)!));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Never_deletes_through_a_junction()
    {
        var victim = Path.Combine(_outside, "important.dll");
        File.WriteAllText(victim, "x");
        Junction(Path.Combine(_root, "sub"), _outside);

        Assert.Equal(-1, SafeDelete.Delete(Path.Combine(_root, "sub", "important.dll"), SafeDelete.RealRoot(_root)!));
        Assert.True(File.Exists(victim));
        Assert.Null(SafeDelete.RealRoot(Path.Combine(_root, "sub")));   // the folder to clean itself is a link
    }
}
