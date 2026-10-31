using Microsoft.Data.Sqlite;
using WattLedger.Core.Processes;
using WattLedger.Core.Storage;
using WattLedger.Core.Time;

namespace WattLedger.Core.Tests;

public sealed class ProcessResourceStorageTests
{
    [Fact]
    public void Initialize_upgrades_a_preexisting_database_without_touching_its_data()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wattledger-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "ledger.sqlite");
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        power_state TEXT NOT NULL,
                        battery_percent INTEGER NULL,
                        remaining_mwh INTEGER NULL,
                        full_charge_mwh INTEGER NULL,
                        design_mwh INTEGER NULL,
                        voltage_mv INTEGER NULL,
                        discharge_mw INTEGER NULL,
                        charge_mw INTEGER NULL,
                        is_ac INTEGER NOT NULL,
                        is_charging INTEGER NOT NULL,
                        is_discharging INTEGER NOT NULL,
                        rapl_pkg_w REAL NULL,
                        rapl_cpu_w REAL NULL,
                        rapl_gpu_w REAL NULL,
                        rapl_dram_w REAL NULL,
                        brightness_pct INTEGER NULL,
                        foreground_process TEXT NULL,
                        cycle_count INTEGER NULL,
                        device_name TEXT NULL
                    );
                    INSERT INTO samples (ts, power_state, is_ac, is_charging, is_discharging)
                    VALUES (123, 'Battery', 0, 0, 0);
                    """;
                command.ExecuteNonQuery();
            }

            var database = new LedgerDatabase(path);
            database.Initialize();

            Assert.Equal(1, ReadUserVersion(path));
            Assert.Equal(1, database.GetSampleCount()); // The old ledger row survived the migration.
            database.InsertAppCpuSamples([Sample(0, "app", 1)]);
            Assert.Single(database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void App_cpu_samples_roundtrip_and_aggregate_over_a_range()
    {
        using var fixture = new LedgerDatabaseTests.DatabaseFixture();
        var minute = Minute(2026, 11, 1, 10, 0);
        fixture.Database.InsertAppCpuSamples(
        [
            new AppCpuSample(minute, "app", 2.0, 1, 2),
            new AppCpuSample(minute, "browser", 0.5, 3, 1),
            new AppCpuSample(minute.AddMinutes(1), "app", 1.0, 2, 1),
            new AppCpuSample(minute.AddMinutes(5), "later", 9.0, 1, 1)
        ]);

        var totals = fixture.Database.GetAppCpuTotals(minute, minute.AddMinutes(2));

        Assert.Equal(2, totals.Count);
        Assert.Equal("app", totals[0].Name);
        Assert.Equal(3.0, totals[0].CpuSeconds, 9);
        Assert.Equal(3, totals[0].Windows);
        Assert.Equal(2, totals[0].MaxProcessCount);
        Assert.Equal("browser", totals[1].Name);
        Assert.Equal(0.5, totals[1].CpuSeconds, 9);
        Assert.Equal(3, totals[1].MaxProcessCount);
        // Rows outside the range stay queryable.
        Assert.Equal("later", Assert.Single(fixture.Database.GetAppCpuTotals(minute.AddMinutes(5), minute.AddMinutes(5))).Name);
    }

    [Fact]
    public void Recorder_buffers_within_a_minute_and_flushes_on_rollover()
    {
        using var fixture = new LedgerDatabaseTests.DatabaseFixture();
        var provider = new FakeProcessProvider();
        var clockMinute = 0;
        var clock = new Func<DateTimeOffset>(() => Minute(2026, 11, 1, 10, clockMinute).AddSeconds(30));
        var recorder = new ProcessResourceRecorder(fixture.Database, provider, clock);
        var flushedMinutes = new List<DateTimeOffset>();
        recorder.MinuteRecorded += (minute, _) => flushedMinutes.Add(minute);

        provider.Enqueue(Snapshot(100, 1, "app", 10.0), Snapshot(200, 2, "browser", 0.0), Snapshot(300, 3, "new", 1.0)); // baseline
        recorder.Collect();
        provider.Enqueue(Snapshot(100, 1, "app", 12.0), Snapshot(200, 2, "browser", 0.5), Snapshot(300, 3, "new", 1.0));
        recorder.Collect();                                                     // still inside minute 10:00

        Assert.Empty(fixture.Database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));

        clockMinute = 1;                                                        // roll into 10:01
        provider.Enqueue(Snapshot(100, 1, "app", 12.5));
        recorder.Collect();

        var flushed = fixture.Database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(2, flushed.Count);
        Assert.Equal("app", flushed[0].Name);
        Assert.Equal(2.0, flushed[0].CpuSeconds, 9);
        Assert.Equal(1, flushed[0].Windows);
        Assert.Equal("browser", flushed[1].Name);
        Assert.Equal(0.5, flushed[1].CpuSeconds, 9);
        Assert.Single(flushedMinutes, minute => minute == Minute(2026, 11, 1, 10, 0));

        recorder.Flush();                                                       // minute 10:01 buffer lands

        var all = fixture.Database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(2, all.Count);
        Assert.Equal(2.5, all[0].CpuSeconds, 9);
        Assert.Equal(2, all[0].Windows);
    }

    [Fact]
    public void Recorder_does_not_store_zero_usage_instances_or_empty_batches()
    {
        using var fixture = new LedgerDatabaseTests.DatabaseFixture();
        var provider = new FakeProcessProvider();
        var recorder = new ProcessResourceRecorder(fixture.Database, provider, () => Minute(2026, 11, 1, 10, 0).AddSeconds(30));

        provider.Enqueue(Snapshot(100, 1, "app", 10.0));
        recorder.Collect();                    // baseline: zero usage, nothing stored
        provider.Enqueue(Snapshot(100, 1, "app", 10.0)); // idle interval: zero usage
        recorder.Collect();
        provider.Enqueue(Array.Empty<ProcessCpuSnapshot>()); // everything exited
        recorder.Collect();
        recorder.Flush();

        Assert.Empty(fixture.Database.GetAppCpuTotals(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    private static AppCpuSample Sample(int minuteOffset, string name, double cpu) =>
        new(Minute(2026, 11, 1, 10, minuteOffset), name, cpu, 1, 1);

    private static DateTimeOffset Minute(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    private static long ReadUserVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ProcessCpuSnapshot Snapshot(int pid, long identity, string name, double cpu) =>
        new(pid, identity, name, cpu, Minute(2026, 11, 1, 10, 0).AddSeconds(30));

    private sealed class FakeProcessProvider : IProcessResourceProvider
    {
        private readonly Queue<IReadOnlyList<ProcessCpuSnapshot>> _batches = new();

        public void Enqueue(params ProcessCpuSnapshot[] batch) => _batches.Enqueue(batch);

        public IReadOnlyList<ProcessCpuSnapshot> ReadCpuSnapshots() =>
            _batches.Count > 0 ? _batches.Dequeue() : Array.Empty<ProcessCpuSnapshot>();
    }
}
