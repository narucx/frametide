using System.Diagnostics;
using System.Text;

namespace Frametide.Core.Windows;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Runs a console program without a window and returns exit code and output.</summary>
public static class NativeProcess
{
    public static ProcessResult Run(string file, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(SystemProgram(file))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{file} did not finish in time");
        }
        return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// Full path for a bare Windows program name ("sc.exe" -> C:\Windows\System32\sc.exe). Without a path, Windows
    /// looks in the current folder first, which may be one a standard user can write to.
    /// </summary>
    public static string SystemProgram(string file) =>
        Path.GetFileName(file) == file ? Path.Combine(Environment.SystemDirectory, file) : file;

    /// <summary>Quotes one argument so CommandLineToArgvW (and .NET's args) give back exactly the same string.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            // Backslashes before a quote are escaped, and so is the quote; elsewhere they are literal.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();   // before the closing quote
    }
}
