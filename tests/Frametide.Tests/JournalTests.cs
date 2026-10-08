using System.Text;
using System.Text.Json.Nodes;
using Frametide.Core.Infrastructure;

namespace Frametide.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ft-journal-").FullName;
    private string JournalPath => Path.Combine(_dir, "journal.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Keeps_the_first_original_only()
    {
        var j = new Journal(JournalPath);
        Assert.True(j.SaveOriginal("t1", "reg|HKCU:\\x|a", JsonValue.Create(1)));
        Assert.False(j.SaveOriginal("t1", "reg|HKCU:\\x|a", JsonValue.Create(2)));
        Assert.Equal(1, j.GetOriginal("t1", "reg|HKCU:\\x|a")!.GetValue<int>());
    }

    [Fact]
    public void Remove_deletes_only_that_tweak()
    {
        var j = new Journal(JournalPath);
        j.SaveOriginal("t1", "k", JsonValue.Create("a"));
        j.SaveOriginal("t2", "k", JsonValue.Create("b"));
        j.Remove("t1");
        Assert.Equal(["t2"], j.TweakIds);
    }

    [Fact]
    public void Reads_files_with_a_bom()
    {
        const string ps = """
            {
                "privacy.telemetry":  {
                    "svc|DiagTrack":  "Automatic",
                    "reg|HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection|AllowTelemetry":  { "Value":  null, "Type":  null, "Exists":  false }
                }
            }
            """;
        File.WriteAllText(JournalPath, ps, new UTF8Encoding(true));
        var entry = new Journal(JournalPath).Get("privacy.telemetry")!;
        Assert.Equal("Automatic", (string?)entry["svc|DiagTrack"]);
        Assert.False(entry[@"reg|HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection|AllowTelemetry"]!["Exists"]!.GetValue<bool>());
    }

    [Fact]
    public void Parallel_writes_lose_nothing()
    {
        var j = new Journal(JournalPath);
        Parallel.For(0, 40, i => j.SaveOriginal($"t{i % 4}", $"k{i}", JsonValue.Create(i)));
        Assert.Equal(40, Enumerable.Range(0, 4).Sum(t => j.Get($"t{t}")!.Count));
    }
}
