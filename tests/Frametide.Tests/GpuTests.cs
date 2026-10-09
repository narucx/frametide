using System.Text;
using Frametide.Core.Gpu;
using Frametide.Core.Infrastructure;

namespace Frametide.Tests;

[Collection("DataDir")]
public sealed class GpuProfileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ft-gpu-").FullName;

    public GpuProfileTests() => AppPaths.UseDataDir(_dir);

    public void Dispose() => TestData.Release(_dir);

    [Fact]
    public void Reads_profiles_of_the_first_version()
    {
        // As written by the first version: UTF-8 BOM, wide indentation, a single profile.
        File.WriteAllText(AppPaths.GpuProfiles, (char)0xFEFF + """
            {
                "Profiles":  [
                                 {
                                     "Driver":  "600.10",
                                     "StockVoltage":  1.05,
                                     "MaxTemp":  70,
                                     "MaxClock":  2600,
                                     "Created":  "2026-01-02T12:30:45",
                                     "OffsetMHz":  240,
                                     "Voltage":  0.95,
                                     "PowerLimitW":  0,
                                     "Name":  "UV 2600 MHz @ 0,950 V (2026-01-02 12:30)",
                                     "AvgPowerW":  300.5
                                 }
                             ]
            }
            """, new UTF8Encoding(false));
        var p = Assert.Single(GpuProfiles.All());
        Assert.Equal(2600, p.MaxClock);
        Assert.Equal(240, p.OffsetMHz);
        Assert.Equal(0.95, p.Voltage);
        Assert.Equal(new DateTime(2026, 1, 2, 12, 30, 45), p.Created);
    }

    [Fact]
    public void Single_profile_object_and_replace_by_name()
    {
        File.WriteAllText(AppPaths.GpuProfiles, """{ "Profiles": { "Name": "A", "MaxClock": 2500, "OffsetMHz": 150 } }""");
        Assert.Equal("A", Assert.Single(GpuProfiles.All()).Name);

        GpuProfiles.Save(new GpuProfile { Name = "A", MaxClock = 2600, OffsetMHz = 180 });
        GpuProfiles.Save(new GpuProfile { Name = "B \"quoted\"", MaxClock = 0, OffsetMHz = 0 });
        var all = GpuProfiles.All();
        Assert.Equal(["A", "B quoted"], all.Select(p => p.Name));
        Assert.Equal(2600, all[0].MaxClock);

        GpuProfiles.Delete("A");
        Assert.Equal("B quoted", Assert.Single(GpuProfiles.All()).Name);
    }

    [Fact]
    public void Broken_file_gives_no_profiles()
    {
        File.WriteAllText(AppPaths.GpuProfiles, "[1, 2");
        Assert.Empty(GpuProfiles.All());
    }

    [Fact]
    public void Clean_name_drops_trailing_backslashes()
    {
        // A backslash right before the closing quote of the sign-in task argument would escape it.
        Assert.Equal("My profile", GpuProfiles.CleanName("My profile\\"));
        Assert.Equal("My profile", GpuProfiles.CleanName(" My profile \\ \\\\ "));
        Assert.Equal("a\\b", GpuProfiles.CleanName("a\\b"));
        Assert.Equal("", GpuProfiles.CleanName("\\\\"));
        Assert.Equal("B quoted", GpuProfiles.CleanName("B \"quoted\"\t"));
    }

    [Fact]
    public void Profiles_from_another_gpu_or_driver_are_recognized()
    {
        var info = new Frametide.Core.Hardware.GpuInfo("NVIDIA GeForce RTX 4080", "600.10", 3000, 100, 400, 320, true, -1000, 1000);
        var same = new GpuProfile { Name = "A", Gpu = info.Name, Driver = info.Driver };
        var newDriver = new GpuProfile { Name = "B", Gpu = info.Name, Driver = "590.01" };
        var otherGpu = new GpuProfile { Name = "C", Gpu = "NVIDIA GeForce RTX 3070", Driver = info.Driver };
        var manual = new GpuProfile { Name = "D" };

        Assert.False(GpuTuning.OtherGpu(same, info) || GpuTuning.DriverChanged(same, info));
        Assert.True(GpuTuning.DriverChanged(newDriver, info));
        Assert.False(GpuTuning.OtherGpu(newDriver, info));
        Assert.True(GpuTuning.OtherGpu(otherGpu, info));
        Assert.False(GpuTuning.OtherGpu(manual, info) || GpuTuning.DriverChanged(manual, info));
        Assert.Throws<InvalidOperationException>(() => GpuTuning.EnsureSameGpu(otherGpu, info));
        GpuTuning.EnsureSameGpu(newDriver, info);
    }

    [Fact]
    public void Test_marker_decides_between_wait_discard_and_recover()
    {
        var boot = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
        var thisBoot = new GpuTestMarker(320, boot.AddHours(1), 1234);
        var earlierBoot = new GpuTestMarker(320, boot.AddHours(-1), 1234);

        Assert.Equal(MarkerAction.Recover, Frametide.Core.Gpu.GpuTests.Evaluate(thisBoot, boot, ownerAlive: false));
        Assert.Equal(MarkerAction.Discard, Frametide.Core.Gpu.GpuTests.Evaluate(earlierBoot, boot, ownerAlive: false));
        Assert.Equal(MarkerAction.Wait, Frametide.Core.Gpu.GpuTests.Evaluate(thisBoot, boot, ownerAlive: true));
    }

    [Fact]
    public void Marker_from_an_earlier_boot_is_removed_without_touching_the_gpu()
    {
        var path = Frametide.Core.Gpu.GpuTests.MarkerPath;
        var started = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromHours(1);
        JsonFile.Write(path, new GpuTestMarker(285, started, Environment.ProcessId));
        var read = JsonFile.Read<GpuTestMarker>(path);
        Assert.Equal(285, read?.OriginalPowerLimitW);
        Assert.Equal(started, read?.Started);

        Assert.False(Frametide.Core.Gpu.GpuTests.RecoverAfterCrash());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Broken_marker_is_removed()
    {
        File.WriteAllText(Frametide.Core.Gpu.GpuTests.MarkerPath, "{ broken");
        Assert.False(Frametide.Core.Gpu.GpuTests.RecoverAfterCrash());
        Assert.False(File.Exists(Frametide.Core.Gpu.GpuTests.MarkerPath));
    }

    [Fact]
    public void Median_takes_the_lower_middle()
    {
        Assert.Equal(2, Frametide.Core.Gpu.GpuTests.Median([3, 1, 2]));
        Assert.Equal(2, Frametide.Core.Gpu.GpuTests.Median([4, 1, 3, 2]));
        Assert.Equal(0, Frametide.Core.Gpu.GpuTests.Median([]));
    }
}
