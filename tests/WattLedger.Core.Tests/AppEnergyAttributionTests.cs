using Microsoft.Data.Sqlite;
using WattLedger.Core.Processes;
using WattLedger.Core.Storage;
using WattLedger.Core.Native;

namespace WattLedger.Core.Tests;

public sealed class AppEnergyAttributionTests
{
    [Fact]
    public void Attributes_the_core_pool_proportionally_and_keeps_the_remainder_unattributed()
    {
        var apps = new[]
        {
            new AppCpuUsage("worker", 4, 1),
            new AppCpuUsage("browser", 1, 1),
            new AppCpuUsage("brand new", 0, 1)
        };
        var domains = new[]
        {
            new EnergyDomainDelta("core", "/sys/class/powercap/intel-rapl-0-0", 2.0),
            new EnergyDomainDelta("package-0", "/sys/class/powercap/intel-rapl-0", 3.0),
            new EnergyDomainDelta("uncore", "/sys/class/powercap/intel-rapl-0-1", 0.2)
        };

        var result = AppEnergyAttribution.Attribute(apps, domains);

        Assert.NotNull(result);
        Assert.Equal(AppEnergyAttribution.ModelVersion, result!.ModelVersion);
        Assert.Equal("core (powercap)", result.EnergySource);
        Assert.False(result.PackageLevelOnly);
        Assert.Equal(2.0, result.PoolWh, 9);
        // The package (which contains the core) is excluded as an ancestor; only genuinely
        // non-pool energy (uncore) is reported as remainder.
        Assert.Equal(0.2, result.RemainderWh, 9);
        Assert.Equal(2, result.Estimates.Count);
        Assert.Equal("worker", result.Estimates[0].AppName);
        Assert.Equal(2.0 * 4 / 5, result.Estimates[0].EstimatedWh, 9);
        Assert.Equal("browser", result.Estimates[1].AppName);
        Assert.Equal(2.0 * 1 / 5, result.Estimates[1].EstimatedWh, 9);
    }

    [Fact]
    public void Falls_back_to_package_domains_flagged_as_package_level()
    {
        var apps = new[] { new AppCpuUsage("worker", 3, 1) };
        var domains = new[] { new EnergyDomainDelta("package-0", "/sys/class/powercap/intel-rapl-0", 3.0) };

        var result = AppEnergyAttribution.Attribute(apps, domains);

        Assert.NotNull(result);
        Assert.True(result!.PackageLevelOnly);
        Assert.Equal("package (powercap)", result.EnergySource);
        Assert.Equal(3.0, result.PoolWh, 9);
        Assert.Equal(3.0, Assert.Single(result.Estimates).EstimatedWh, 9);
    }

    [Fact]
    public void Sums_core_domains_across_sockets_without_double_counting()
    {
        var apps = new[] { new AppCpuUsage("worker", 2, 1) };
        var domains = new[]
        {
            new EnergyDomainDelta("core", "/sys/class/powercap/intel-rapl-0-0", 1.0),
            new EnergyDomainDelta("core", "/sys/class/powercap/intel-rapl-1-0", 0.5)
        };

        var result = AppEnergyAttribution.Attribute(apps, domains);

        Assert.NotNull(result);
        Assert.False(result!.PackageLevelOnly);
        Assert.Equal(1.5, result.PoolWh, 9);
        Assert.Equal(0, result.RemainderWh, 9);
    }

    [Fact]
    public void Returns_null_without_a_usable_energy_pool()
    {
        var apps = new[] { new AppCpuUsage("worker", 3, 1) };
        Assert.Null(AppEnergyAttribution.Attribute(apps, Array.Empty<EnergyDomainDelta>()));
        // uncore alone is neither a core nor a package domain: no attribution.
        Assert.Null(AppEnergyAttribution.Attribute(apps, [new EnergyDomainDelta("uncore", "/sys/class/powercap/intel-rapl-0-1", 1.0)]));
    }

    [Fact]
    public void Returns_null_when_the_window_has_no_cpu_activity()
    {
        var apps = new[] { new AppCpuUsage("brand new", 0, 1) };
        var domains = new[] { new EnergyDomainDelta("core", "/sys/class/powercap/intel-rapl-0-0", 2.0) };

        // Idle energy stays unattributed instead of being spread anywhere.
        Assert.Null(AppEnergyAttribution.Attribute(apps, domains));
    }
}

public sealed class AppEnergyStorageTests
{
    [Fact]
    public void Migration_v2_upgrades_a_version_1_database_and_preserves_rows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wattledger-migration2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "ledger.sqlite");
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE samples (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL);
                    CREATE TABLE app_cpu_samples (id INTEGER PRIMARY KEY AUTOINCREMENT, minute INTEGER NOT NULL, app_name TEXT NOT NULL, cpu_seconds REAL NOT NULL, process_count INTEGER NOT NULL, windows INTEGER NOT NULL);
                    INSERT INTO samples (ts) VALUES (7);
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            var database = new LedgerDatabase(path);
            database.Initialize();

            Assert.Equal(2, ReadUserVersion(path));
            Assert.Equal(1, database.GetSampleCount());
            database.InsertAppCpuSamples([new AppCpuSample(Minute(0), "app", 1, 1, 1)]);
            database.InsertAppEnergySamples([Energy(0, "app", 0.5)]);
            Assert.Single(database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
            Assert.Single(database.GetAppEnergyTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Energy_estimates_roundtrip_aggregate_and_prune()
    {
        using var fixture = new LedgerDatabaseTests.DatabaseFixture();
        var minute = Minute(0);
        fixture.Database.InsertAppEnergySamples(
        [
            new AppEnergySample(minute, "app", 1.0, 100, "core (powercap)", AppEnergyAttribution.ModelVersion, 2),
            new AppEnergySample(minute, "app", 0.5, 50, "package (powercap)", AppEnergyAttribution.ModelVersion, 1),
            new AppEnergySample(minute.AddMinutes(3), "later", 9.0, 10, "core (powercap)", AppEnergyAttribution.ModelVersion, 1)
        ]);

        var totals = fixture.Database.GetAppEnergyTotals(minute, minute.AddMinutes(1));

        Assert.Equal(2, totals.Count);
        // Grouping is per application AND source: a day can mix core-level windows with
        // package-level fallback windows, and callers see both lines.
        Assert.Equal("app", totals[0].Name);
        Assert.Equal(1.0, totals[0].EstimatedWh, 9);
        Assert.Equal(100, totals[0].CpuSeconds, 9);
        Assert.Equal(2, totals[0].Windows);
        Assert.Equal("core (powercap)", totals[0].EnergySource);
        Assert.Equal("package (powercap)", totals[1].EnergySource);
        Assert.Equal(0.5, totals[1].EstimatedWh, 9);

        // Retention prunes only rows older than the cutoff, across both aggregate tables.
        fixture.Database.InsertAppCpuSamples([new AppCpuSample(minute, "app", 1, 1, 1)]);
        fixture.Database.PruneAppEnergyEstimates(minute.AddMinutes(2));
        fixture.Database.PruneAppCpuSamples(minute.AddMinutes(2));
        Assert.Empty(fixture.Database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal("later", Assert.Single(fixture.Database.GetAppEnergyTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)).Name);
    }

    [Fact]
    public void Recorder_persists_attribution_rows_aligned_with_cpu_minutes()
    {
        using var fixture = new LedgerDatabaseTests.DatabaseFixture();
        var provider = new FakeProcessProvider();
        var domains = new Queue<EnergyDomainSnapshot[]>();
        var recorder = new ProcessResourceRecorder(
            fixture.Database, provider,
            () => Minute(0).AddSeconds(30),
            readEnergyDomains: () => domains.Dequeue());
        var energyMinutes = new List<DateTimeOffset>();
        recorder.EnergyRecorded += (minute, _) => energyMinutes.Add(minute);

        provider.Enqueue(Snapshot(100, 1, "app", 10.0), Snapshot(200, 2, "browser", 10.0)); // CPU baseline
        domains.Enqueue([Domain("core", 1_000_000_000), Domain("uncore", 500_000_000)]); // energy baseline
        recorder.Collect();
        provider.Enqueue(Snapshot(100, 1, "app", 14.0), Snapshot(200, 2, "browser", 12.0));
        domains.Enqueue(
        [
            Domain("core", 2_800_000_000),   // +1.8e9 µJ == 0.5 Wh over the window
            Domain("uncore", 600_000_000)
        ]);
        recorder.Collect();

        recorder.Flush();

        // CPU: app 4s, browser 2s over the window; core pool 0.5 Wh split 2:1.
        var estimates = fixture.Database.GetAppEnergyTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(2, estimates.Count);
        Assert.Equal("app", estimates[0].Name);
        Assert.Equal(0.5 * 4 / 6, estimates[0].EstimatedWh, 9);
        Assert.Equal("browser", estimates[1].Name);
        Assert.Equal(0.5 * 2 / 6, estimates[1].EstimatedWh, 9);
        Assert.Equal(Minute(0), energyMinutes.Single());

        // The second window has no readable domains: attribution pauses instead of inventing.
        provider.Enqueue(Snapshot(100, 1, "app", 15.0));
        domains.Enqueue([]);
        recorder.Collect();
        recorder.Flush();
        Assert.Equal(2, fixture.Database.GetAppEnergyTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Count);
    }

    private static AppEnergySample Energy(int minuteOffset, string name, double wh) =>
        new(Minute(minuteOffset), name, wh, 10, "core (powercap)", AppEnergyAttribution.ModelVersion, 1);

    private static DateTimeOffset Minute(int minuteOffset) =>
        new(2026, 11, 1, 10, minuteOffset, 0, TimeSpan.FromHours(8));

    private static ProcessCpuSnapshot Snapshot(int pid, long identity, string name, double cpu) =>
        new(pid, identity, name, cpu, Minute(0).AddSeconds(30));

    private static EnergyDomainSnapshot Domain(string name, long energyUj) =>
        new(name, $"/sys/class/powercap/{name}", energyUj, null, Minute(0).AddSeconds(30));

    private static long ReadUserVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeProcessProvider : IProcessResourceProvider
    {
        private readonly Queue<IReadOnlyList<ProcessCpuSnapshot>> _batches = new();

        public void Enqueue(params ProcessCpuSnapshot[] batch) => _batches.Enqueue(batch);

        public IReadOnlyList<ProcessCpuSnapshot> ReadCpuSnapshots() =>
            _batches.Count > 0 ? _batches.Dequeue() : Array.Empty<ProcessCpuSnapshot>();
    }
}
