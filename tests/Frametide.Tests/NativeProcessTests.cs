using System.Runtime.InteropServices;
using Frametide.Core.Windows;

namespace Frametide.Tests;

public sealed class NativeProcessTests
{
    [Theory]
    [InlineData("--tray")]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData(@"C:\some folder\")]
    [InlineData(@"C:\x\\")]
    [InlineData("a\"b")]
    [InlineData(@"a\""b")]
    [InlineData(@"a\\""b c")]
    [InlineData("tab\there")]
    public void Quoted_arguments_come_back_unchanged(string arg)
    {
        var line = "app.exe " + NativeProcess.Quote(arg) + " next";
        Assert.Equal(["app.exe", arg, "next"], Split(line));
    }

    [Fact]
    public void Bare_program_names_resolve_to_system32()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "sc.exe"), NativeProcess.SystemProgram("sc.exe"));
        Assert.Equal(@"C:\Tools\x.exe", NativeProcess.SystemProgram(@"C:\Tools\x.exe"));
    }

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
