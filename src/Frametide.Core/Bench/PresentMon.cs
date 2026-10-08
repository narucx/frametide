using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Bench;

/// <summary>
/// Intel PresentMon (console version, MIT license): records frame times from Windows' graphics events (ETW), the game
/// process is not touched. Downloaded from Intel's GitHub releases into the administrator-only data folder; the
/// Intel signature is checked after the download and before every start.
/// </summary>
public static partial class PresentMon
{
    private const string Publisher = "Intel Corporation";
    private const string SessionName = "Frametide";

    private static string Dir => Path.Combine(AppPaths.DataDir, @"tools\PresentMon");

    /// <summary>The installed PresentMon executable, or null.</summary>
    public static string? ExePath =>
        Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "PresentMon*.exe").Order(StringComparer.OrdinalIgnoreCase).LastOrDefault() : null;

    public static async Task<string> InstallLatestAsync(CancellationToken cancel = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Frametide", "1.0"));
        using var release = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases/latest", cancel));
        var tag = release.RootElement.GetProperty("tag_name").GetString();
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => ConsoleAsset().IsMatch(a.GetProperty("name").GetString() ?? ""));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"No PresentMon console download found in release {tag}.");

        Directory.CreateDirectory(Dir);
        var name = Path.GetFileName(asset.GetProperty("name").GetString()!);
        var target = Path.Combine(Dir, name);
        var tmp = target + ".download";
        await using (var src = await http.GetStreamAsync(asset.GetProperty("browser_download_url").GetString(), cancel))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, cancel);
        if (!Authenticode.IsSignedBy(tmp, Publisher))
        {
            File.Delete(tmp);
            throw new InvalidOperationException("The downloaded file is not signed by Intel. It was deleted.");
        }
        File.Move(tmp, target, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(Dir, "PresentMon*.exe").Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
            File.Delete(old);
        Log.Ok($"PresentMon {tag} installed (signed by Intel).");
        return target;
    }

    /// <summary>Starts a recording of one game into a CSV file. PresentMon ends by itself when the game closes.</summary>
    public static Process Start(string exe, string csvPath)
    {
        var pm = ExePath ?? throw new InvalidOperationException("PresentMon is not installed.");
        if (!Authenticode.IsSignedBy(pm, Publisher)) throw new InvalidOperationException("PresentMon is not signed by Intel, it is not started.");
        var psi = new ProcessStartInfo(pm) { UseShellExecute = false, CreateNoWindow = true };
        // --qpc_time_ms: frame start times on the QPC clock, so frames can be matched with the foreground tracking.
        foreach (var a in new[] { "--process_name", exe, "--output_file", csvPath, "--terminate_on_proc_exit", "--no_console_stats",
                     "--no_track_input", "--qpc_time_ms", "--stop_existing_session", "--session_name", SessionName })
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("PresentMon could not be started.");
    }

    [GeneratedRegex(@"^PresentMon-[\d.]+-x64\.exe$")]
    private static partial Regex ConsoleAsset();
}
