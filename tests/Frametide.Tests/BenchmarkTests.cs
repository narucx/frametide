using System.Globalization;
using System.Text;
using Frametide.Core.Bench;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Tests;

// Shares the static data folder setting with other tests: not in parallel with them.
[Collection("DataDir")]
public sealed class BenchmarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ft-bench-").FullName;

    public void Dispose() => TestData.Release(_dir);

    /// <summary>Writes a PresentMon v2 style CSV; frame start times are on the QPC ms clock, starting at 10 000.</summary>
    private string Csv(IEnumerable<double> frameTimes, string process = "game.exe", string swapChain = "0x1", IEnumerable<double>? overlay = null)
    {
        var sb = new StringBuilder("Application,ProcessID,SwapChainAddress,CPUStartQPCTime,FrameTime\n");
        void Add(IEnumerable<double> times, string app, string sc)
        {
            double t = 10_000;
            foreach (var ft in times)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{app},100,{sc},{t:F4},{ft:F4}\n");
                t += ft;
            }
        }
        Add(frameTimes, process, swapChain);
        if (overlay is not null) Add(overlay, "overlay.exe", "0x2");
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void Steady_frames_give_exact_fps_and_no_stutters()
    {
        var r = FrameAnalysis.Analyze(Csv(Enumerable.Repeat(5.0, 24_000)));   // 200 FPS for 2 minutes
        Assert.Null(r.Error);
        Assert.Equal(24_000, r.Frames);
        Assert.Equal(120, r.DurationSec, 1);
        Assert.Equal(200, r.AvgFps, 1);
        Assert.Equal(200, r.Low1, 1);
        Assert.Equal(0, r.Stutters);
        Assert.Equal(200, FrameAnalysis.DetectCap(r.Series));
    }

    [Fact]
    public void Single_spikes_count_as_stutters_and_slow_phases_do_not()
    {
        var frames = new List<double>();
        for (var i = 0; i < 6000; i++) frames.Add(i % 1000 == 500 ? 40 : 5);   // 6 spikes
        frames.AddRange(Enumerable.Repeat(50.0, 200));                          // loading screen: slow but even
        frames.AddRange(Enumerable.Repeat(5.0, 1000));
        frames.Add(150);                                                        // freeze
        frames.AddRange(Enumerable.Repeat(5.0, 100));
        var r = FrameAnalysis.Analyze(Csv(frames));
        Assert.Equal(7, r.Stutters);
        Assert.Equal(1, r.Hitches);
    }

    [Fact]
    public void Uses_the_swap_chain_with_most_frames()
    {
        var r = FrameAnalysis.Analyze(Csv(Enumerable.Repeat(4.0, 5000), overlay: Enumerable.Repeat(16.0, 300)));
        Assert.Equal(5000, r.Frames);
        Assert.Equal(250, r.AvgFps, 1);
    }

    [Fact]
    public void Frames_while_the_game_was_not_in_front_are_left_out()
    {
        // 10 s at 100 FPS starting at t = 10 000 ms; alt-tab from 13 000 to 15 000 (+0.5 s before, +1.5 s after).
        var r = FrameAnalysis.Analyze(Csv(Enumerable.Repeat(10.0, 1000)), [(13_000, 15_000)]);
        Assert.Equal(4.0, r.ExcludedSec, 1);
        Assert.Equal(599, r.Frames);
    }

    [Fact]
    public void Older_sessions_are_read_and_compared_by_duration()
    {
        AppPaths.UseDataDir(_dir);
        var sessions = Path.Combine(AppPaths.BenchDir, "sessions");
        Directory.CreateDirectory(sessions);
        // Format of the first version: UTF-8 BOM, no Analysis/Cap/Hitches fields.
        File.WriteAllText(Path.Combine(sessions, "20260101-200000.json"),
            (char)0xFEFF + "{ \"Id\": \"20260101-200000\", \"Game\": \"G\", \"Exe\": \"g.exe\", \"Start\": \"2026-01-01T20:00:00\", \"DurationSec\": 600, \"AvgFps\": 100, \"Low1\": 50, \"Low01\": 30, \"StuttersPerMin\": 9, \"Boost\": false, \"GpuW\": 200, \"Series\": [100, 100] }",
            new UTF8Encoding(false));
        BenchStore.Save(new BenchSession { Id = "20260102-200000", Game = "G", Exe = "g.exe", Start = new DateTime(2026, 1, 2, 20, 0, 0), DurationSec = 300, AvgFps = 130, Low1 = 70, StuttersPerMin = 1, Boost = true, Analysis = FrameAnalysis.Version, GpuW = 180, Series = [130] });
        BenchStore.Save(new BenchSession { Id = "20260103-200000", Game = "G", Exe = "g.exe", Start = new DateTime(2026, 1, 3, 20, 0, 0), DurationSec = 900, AvgFps = 110, Low1 = 60, StuttersPerMin = 2, Boost = true, Analysis = FrameAnalysis.Version, Series = [110] });

        var all = BenchStore.Sessions();
        Assert.Equal(["20260103-200000", "20260102-200000", "20260101-200000"], all.Select(s => s.Id));
        Assert.Equal(1, all[2].AnalysisVersion);

        var c = Assert.Single(BenchStore.Compare(all));
        Assert.Equal(100, c.Without!["AvgFps"]);
        Assert.Equal(115, c.With!["AvgFps"]);              // (130 * 300 + 110 * 900) / 1200
        Assert.Equal(180, c.With["GpuW"]);                  // only one session has a value
        Assert.Null(c.Without["StuttersPerMin"]);           // analyzed with the old rule: not compared
        Assert.Equal(20, c.With.Minutes);

        BenchStore.Delete(@"..\..\config");                 // not a session ID: ignored
        BenchStore.Delete("20260101-200000");
        Assert.Equal(2, BenchStore.Sessions().Count);
    }

    [Fact]
    public void Signatures_are_checked()
    {
        var signed = typeof(object).Assembly.Location;     // System.Private.CoreLib, signed by Microsoft
        Assert.True(Authenticode.IsSignedBy(signed, "Microsoft Corporation"));
        Assert.False(Authenticode.IsSignedBy(signed, "Intel Corporation"));
        var unsigned = Path.Combine(_dir, "unsigned.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0]);
        Assert.False(Authenticode.IsSignedBy(unsigned, "Microsoft Corporation"));
    }
}
