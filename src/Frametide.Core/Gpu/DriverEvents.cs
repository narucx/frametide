using System.Xml.Linq;
using Frametide.Core.Windows;

namespace Frametide.Core.Gpu;

/// <summary>Display driver crashes in the System event log (TDR "Display 4101", nvlddmkm errors).</summary>
public static class DriverEvents
{
    /// <summary>Description of the first driver crash since <paramref name="since"/>, or null.</summary>
    public static string? CrashSince(DateTime since)
    {
        var ms = Math.Max(1000, (long)(DateTime.Now - since).TotalMilliseconds);
        var query = "*[System[((Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2)))"
                    + $" and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        var r = NativeProcess.Run("wevtutil.exe", ["qe", "System", $"/q:{query}", "/c:1", "/rd:true", "/f:RenderedXml"], TimeSpan.FromSeconds(20));
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Output)) return null;
        try
        {
            var ev = XElement.Parse(r.Output.Trim());
            XNamespace ns = ev.Name.Namespace;
            var provider = ev.Descendants(ns + "Provider").FirstOrDefault()?.Attribute("Name")?.Value ?? "?";
            var id = ev.Descendants(ns + "EventID").FirstOrDefault()?.Value ?? "?";
            var message = ev.Descendants(ns + "Message").FirstOrDefault()?.Value.Split('\n')[0].Trim() ?? "";
            return $"{provider} {id}: {message}";
        }
        catch (System.Xml.XmlException) { return "Display driver error event"; }
    }
}
