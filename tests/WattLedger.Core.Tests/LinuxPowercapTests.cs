using WattLedger.Core.Native;

namespace WattLedger.Core.Tests;

public sealed class LinuxPowercapTests
{
    [Fact]
    public void Reads_domains_with_names_ranges_and_missing_values_as_null()
    {
        using var fixture = new PowercapFixture();
        fixture.Domain("intel-rapl-0", "package-0", ("energy_uj", "50000000"), ("max_energy_range_uj", "262143328850"));
        fixture.Domain("intel-rapl-0-0", "core", ("energy_uj", "1000000"), ("max_energy_range_uj", "98441216000"));
        // Present but unreadable on this machine: energy_uj is null, not zero.
        fixture.Domain("intel-rapl-1", "package-1", ("max_energy_range_uj", "262143328850"));
        // Control-type directory without energy_uj must be skipped entirely.
        fixture.AddDirectory("intel-rapl");

        var domains = new LinuxPowercapReader(fixture.Root).Read();

        Assert.Equal(3, domains.Count);
        Assert.Equal("package-0", domains[0].Name);
        Assert.Equal(50_000_000, domains[0].EnergyUj);
        Assert.Equal(262_143_328_850, domains[0].MaxRangeUj);
        Assert.Equal("core", domains[1].Name);
        Assert.Null(domains[2].EnergyUj);
        Assert.Equal("package-1", domains[2].Name);
    }

    [Fact]
    public void Missing_name_falls_back_to_the_directory_name()
    {
        using var fixture = new PowercapFixture();
        fixture.Domain("intel-rapl-0", null, ("energy_uj", "5"));

        var domain = Assert.Single(new LinuxPowercapReader(fixture.Root).Read());

        Assert.Equal("intel-rapl-0", domain.Name);
    }

    [Fact]
    public void Missing_or_empty_powercap_root_reports_no_domains()
    {
        using var fixture = new PowercapFixture();
        Assert.Empty(new LinuxPowercapReader(Path.Combine(fixture.Root, "absent")).Read());
        Assert.Empty(new LinuxPowercapReader(fixture.Root).Read());
    }

    [Fact]
    public void Tracker_first_sight_is_a_zero_baseline_then_reports_deltas()
    {
        var tracker = new PowercapEnergyTracker();
        var at = DateTimeOffset.Now;
        var path = "/sys/class/powercap/intel-rapl-0";

        var baseline = tracker.Collect([Snapshot("package-0", path, 1_000_000, 10_000_000, at)]);
        Assert.Equal(0, Assert.Single(baseline).EnergyWh, 9);

        var delta = Assert.Single(tracker.Collect([Snapshot("package-0", path, 3_601_000_000, 10_000_000, at.AddSeconds(5))]));

        // 3.6e9 µJ == 1 Wh.
        Assert.Equal(1.0, delta.EnergyWh, 9);
    }

    [Fact]
    public void Tracker_reconstructs_wraparound_only_when_the_range_is_known()
    {
        var tracker = new PowercapEnergyTracker();
        var at = DateTimeOffset.Now;
        var path = "/sys/class/powercap/intel-rapl-0";
        tracker.Collect([Snapshot("package-0", path, 9_900_000, 10_000_000, at)]);

        var wrapped = Assert.Single(tracker.Collect([Snapshot("package-0", path, 100_000, 10_000_000, at.AddSeconds(5))]));

        Assert.Equal((10_000_000 - 9_900_000 + 100_000) / 3_600_000_000.0, wrapped.EnergyWh, 9);

        var unknownTracker = new PowercapEnergyTracker();
        unknownTracker.Collect([Snapshot("package-0", path, 9_900_000, null, at)]);
        Assert.Equal(0, Assert.Single(unknownTracker.Collect([Snapshot("package-0", path, 100_000, null, at.AddSeconds(5))])).EnergyWh);
    }

    [Fact]
    public void Tracker_forggets_the_baseline_after_an_unreadable_reading()
    {
        var tracker = new PowercapEnergyTracker();
        var at = DateTimeOffset.Now;
        var path = "/sys/class/powercap/intel-rapl-0";
        tracker.Collect([Snapshot("package-0", path, 1_000_000, 10_000_000, at)]);

        // Permission dropped mid-run: the unreadable round must clear the baseline so the
        // next readable sample never diffs across the unknown gap.
        Assert.Empty(tracker.Collect([Snapshot("package-0", path, null, 10_000_000, at.AddSeconds(5))]));

        Assert.Equal(0, Assert.Single(tracker.Collect([Snapshot("package-0", path, 9_000_000, 10_000_000, at.AddSeconds(10))])).EnergyWh);
    }

    [Fact]
    public void Tracker_keeps_domains_independent_and_drops_disappeared_ones()
    {
        var tracker = new PowercapEnergyTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect(
        [
            Snapshot("package-0", "/sys/class/powercap/intel-rapl-0", 1_000_000, 10_000_000, at),
            Snapshot("core", "/sys/class/powercap/intel-rapl-0-0", 2_000_000, 10_000_000, at)
        ]);

        var deltas = tracker.Collect(
        [
            Snapshot("package-0", "/sys/class/powercap/intel-rapl-0", 2_800_000, 10_000_000, at.AddSeconds(5))
        ]);

        var delta = Assert.Single(deltas);
        Assert.Equal("package-0", delta.Name);
        Assert.Equal(1_800_000 / 3_600_000_000.0, delta.EnergyWh, 9);
    }

    private static EnergyDomainSnapshot Snapshot(string name, string path, long? energyUj, long? maxRangeUj, DateTimeOffset at) =>
        new(name, path, energyUj, maxRangeUj, at);

    private sealed class PowercapFixture : IDisposable
    {
        public PowercapFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"wattledger-powercap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void AddDirectory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name));

        public void Domain(string name, string? domainName, params (string File, string Value)[] files)
        {
            var path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(path);
            if (domainName is not null)
            {
                File.WriteAllText(Path.Combine(path, "name"), domainName + "\n");
            }

            foreach (var (file, value) in files)
            {
                File.WriteAllText(Path.Combine(path, file), value + "\n");
            }
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
