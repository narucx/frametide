using System.Diagnostics;

namespace Frametide.Core.Windows;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Runs a console program without a window and returns exit code and output.</summary>
public static class NativeProcess
{
    public static ProcessResult Run(string file, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(file)
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
}
