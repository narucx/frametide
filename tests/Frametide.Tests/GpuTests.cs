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
    public void Median_takes_the_lower_middle()
    {
        Assert.Equal(2, Frametide.Core.Gpu.GpuTests.Median([3, 1, 2]));
        Assert.Equal(2, Frametide.Core.Gpu.GpuTests.Median([4, 1, 3, 2]));
        Assert.Equal(0, Frametide.Core.Gpu.GpuTests.Median([]));
    }
}
