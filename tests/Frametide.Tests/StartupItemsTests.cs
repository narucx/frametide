using Frametide.Core.Maintenance;

namespace Frametide.Tests;

public sealed class StartupItemsTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\tool.exe /background", @"C:\Tools\tool.exe")]
    [InlineData(@"C:\Program Files\Some App\app.exe -silent", @"C:\Program Files\Some App\app.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe")]
    public void Program_path_is_taken_from_the_command(string command, string expected) =>
        Assert.Equal(expected, StartupItems.ExePath(command));

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, "explorer.exe"), StartupItems.ExePath(@"%SystemRoot%\explorer.exe /n"), ignoreCase: true);
    }
}
