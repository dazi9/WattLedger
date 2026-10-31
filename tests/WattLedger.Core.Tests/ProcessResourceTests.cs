using WattLedger.Core.Processes;

namespace WattLedger.Core.Tests;

public sealed class ProcessCpuTrackerTests
{
    [Fact]
    public void First_sample_is_a_baseline_and_reports_zero_usage()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;

        var usages = tracker.Collect([Snapshot(100, 1, "app", 12.5, at)]);

        var usage = Assert.Single(usages);
        Assert.Equal(0, usage.CpuSeconds);
        Assert.Equal(new ProcessKey(100, 1), new ProcessKey(usage.Pid, usage.StartIdentity));
    }

    [Fact]
    public void Second_sample_reports_the_interval_delta()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "app", 12.5, at)]);

        var usages = tracker.Collect([Snapshot(100, 1, "app", 13.0, at.AddSeconds(5))]);

        Assert.Equal(0.5, Assert.Single(usages).CpuSeconds, 9);
    }

    [Fact]
    public void Exited_instances_are_dropped_and_not_kept_as_baseline()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "app", 5, at), Snapshot(200, 2, "other", 5, at)]);

        var usages = tracker.Collect([Snapshot(100, 1, "app", 6, at.AddSeconds(5))]);

        var usage = Assert.Single(usages);
        Assert.Equal(100, usage.Pid);
        // The exited process must also be forgotten: a later PID-200 instance is brand new.
        Assert.DoesNotContain(tracker.Collect([Snapshot(200, 2, "other", 7, at.AddSeconds(10))]), u => u.CpuSeconds > 0);
    }

    [Fact]
    public void Pid_reuse_is_a_new_instance_and_never_diffed_against_the_old_one()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "old", 500, at)]);

        var usages = tracker.Collect([Snapshot(100, 2, "new", 0.25, at.AddSeconds(5))]);

        // Diffing against the old instance would report a huge negative (or clamped) value;
        // a new identity starts from zero instead.
        Assert.Equal(0, Assert.Single(usages).CpuSeconds);
    }

    [Fact]
    public void Backwards_counters_are_clamped_to_zero()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "app", 10, at)]);

        var usages = tracker.Collect([Snapshot(100, 1, "app", 9, at.AddSeconds(5))]);

        Assert.Equal(0, Assert.Single(usages).CpuSeconds);
    }

    [Fact]
    public void Duplicate_keys_in_one_batch_keep_the_last_sample()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "app", 5, at)]);

        tracker.Collect([Snapshot(100, 1, "app", 6, at.AddSeconds(5)), Snapshot(100, 1, "app", 7, at.AddSeconds(5))]);

        var usages = tracker.Collect([Snapshot(100, 1, "app", 8, at.AddSeconds(10))]);
        Assert.Equal(1, Assert.Single(usages).CpuSeconds, 9);
    }

    [Fact]
    public void Empty_batch_resets_the_baseline()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "app", 5, at)]);
        tracker.Collect([]);

        // Everything exited; the next appearance is a fresh baseline with zero usage.
        Assert.Equal(0, Assert.Single(tracker.Collect([Snapshot(100, 1, "app", 9, at.AddSeconds(5))])).CpuSeconds);
    }

    [Fact]
    public void Samples_are_deduplicated_across_multi_threaded_snapshots()
    {
        var tracker = new ProcessCpuTracker();
        var at = DateTimeOffset.Now;
        tracker.Collect([Snapshot(100, 1, "app", 5, at), Snapshot(100, 1, "app", 5, at)]);

        var usages = tracker.Collect([Snapshot(100, 1, "app", 6, at.AddSeconds(5))]);

        var usage = Assert.Single(usages);
        Assert.Equal(1, usage.CpuSeconds, 9);
    }

    private static ProcessCpuSnapshot Snapshot(int pid, long identity, string name, double cpu, DateTimeOffset at) =>
        new(pid, identity, name, cpu, at);
}

public sealed class ProcessAppGrouperTests
{
    [Fact]
    public void Groups_by_executable_name_case_insensitively_and_counts_instances()
    {
        var at = DateTimeOffset.Now;
        var usages = new[]
        {
            new ProcessCpuUsage(100, 1, "chrome", 4),
            new ProcessCpuUsage(101, 2, "Chrome", 2),
            new ProcessCpuUsage(102, 3, "chrome", 1),
            new ProcessCpuUsage(200, 4, "code", 8)
        };

        var apps = ProcessAppGrouper.Group(usages);

        Assert.Equal(2, apps.Count);
        Assert.Equal("code", apps[0].Name);
        Assert.Equal(8, apps[0].CpuSeconds, 9);
        Assert.Equal(1, apps[0].ProcessCount);
        Assert.Equal("chrome", apps[1].Name);
        Assert.Equal(7, apps[1].CpuSeconds, 9);
        Assert.Equal(3, apps[1].ProcessCount);
    }

    [Fact]
    public void Instance_count_deduplicates_by_process_key()
    {
        var at = DateTimeOffset.Now;
        var usages = new[]
        {
            new ProcessCpuUsage(100, 1, "app", 4),
            new ProcessCpuUsage(100, 1, "app", 2)
        };

        var app = Assert.Single(ProcessAppGrouper.Group(usages));

        Assert.Equal(6, app.CpuSeconds, 9);
        Assert.Equal(1, app.ProcessCount);
    }

    [Fact]
    public void Blank_names_stay_visible_as_unknown()
    {
        var usages = new[] { new ProcessCpuUsage(100, 1, "", 4) };

        var app = Assert.Single(ProcessAppGrouper.Group(usages));

        Assert.Equal("unknown", app.Name);
        Assert.Equal(4, app.CpuSeconds, 9);
    }

    [Fact]
    public void Zero_cpu_new_instances_are_kept_for_coverage_but_sort_last()
    {
        var at = DateTimeOffset.Now;
        var usages = new[]
        {
            new ProcessCpuUsage(200, 2, "brand new", 0),
            new ProcessCpuUsage(100, 1, "worker", 3)
        };

        var apps = ProcessAppGrouper.Group(usages);

        Assert.Equal("worker", apps[0].Name);
        Assert.Equal("brand new", apps[1].Name);
        Assert.Equal(0, apps[1].CpuSeconds);
    }
}

public sealed class LinuxProcessCpuReaderTests
{
    [Fact]
    public void Parses_utime_stime_and_starttime_from_proc_stat()
    {
        using var fixture = new ProcFixture();
        fixture.Process(42, "my app (v1.2)",
            "42 (my app (v1.2)) S 1 1 0 0 -1 4194560 100 0 0 0 250 750 0 0 20 0 1 0 98765 218916864 2048 18446744073709551615");

        var snapshots = new LinuxProcessCpuReader(fixture.Root, clockTicksPerSecond: 100).ReadCpuSnapshots();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(42, snapshot.Pid);
        Assert.Equal("my app (v1.2)", snapshot.Name);
        Assert.Equal((250 + 750) / 100.0, snapshot.TotalCpuSeconds, 9);
        Assert.Equal(98765, snapshot.StartIdentity);
    }

    [Fact]
    public void Non_numeric_directories_and_missing_stat_files_are_skipped()
    {
        using var fixture = new ProcFixture();
        fixture.Process(7, "worker",
            "7 (worker) S 1 1 0 0 -1 0 0 0 0 0 10 5 0 0 20 0 1 0 111 1 1 0");
        fixture.AddDirectory("acpi");
        fixture.AddDirectory("self");
        fixture.AddDirectory("999"); // PID directory without a stat file.

        var snapshots = new LinuxProcessCpuReader(fixture.Root, clockTicksPerSecond: 100).ReadCpuSnapshots();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(7, snapshot.Pid);
        Assert.Equal(15 / 100.0, snapshot.TotalCpuSeconds, 9);
    }

    [Fact]
    public void Malformed_stat_lines_are_skipped_without_failing_the_batch()
    {
        using var fixture = new ProcFixture();
        fixture.Process(7, "worker",
            "7 (worker) S 1 1 0 0 -1 0 0 0 0 0 10 5 0 0 20 0 1 0 111 1 1 0");
        fixture.Process(8, "broken", "8 (broken) S not-a-number");

        var snapshots = new LinuxProcessCpuReader(fixture.Root, clockTicksPerSecond: 100).ReadCpuSnapshots();

        Assert.Equal(7, Assert.Single(snapshots).Pid);
    }

    [Fact]
    public void Reader_and_tracker_compute_interval_usage_from_proc_data()
    {
        using var fixture = new ProcFixture();
        var reader = new LinuxProcessCpuReader(fixture.Root, clockTicksPerSecond: 100);
        fixture.Process(7, "worker", "7 (worker) S 1 1 0 0 -1 0 0 0 0 0 100 100 0 0 20 0 1 0 55 1 1 0");
        var tracker = new ProcessCpuTracker();
        tracker.Collect(reader.ReadCpuSnapshots());

        fixture.Process(7, "worker", "7 (worker) S 1 1 0 0 -1 0 0 0 0 0 150 100 0 0 20 0 1 0 55 1 1 0");

        var usage = Assert.Single(tracker.Collect(reader.ReadCpuSnapshots()));
        Assert.Equal(50 / 100.0, usage.CpuSeconds, 9);
    }

    private sealed class ProcFixture : IDisposable
    {
        public ProcFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"wattledger-proc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void AddDirectory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name));

        public void Process(int pid, string name, string statLine)
        {
            var directory = Path.Combine(Root, pid.ToString());
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "stat"), statLine + "\n");
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}

public sealed class ProcessResourceProviderTests
{
    [Fact]
    public void Windows_reader_reads_live_instances_or_reports_empty_off_windows()
    {
        var snapshots = new WindowsProcessCpuReader().ReadCpuSnapshots();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(snapshots);
            return;
        }

        Assert.NotEmpty(snapshots);
        // The start identity must be positive for every instance: it is the PID-reuse guard.
        Assert.All(snapshots, snapshot => Assert.True(snapshot.StartIdentity > 0));
        Assert.All(snapshots, snapshot => Assert.True(snapshot.TotalCpuSeconds >= 0));
    }

    [Fact]
    public void Factory_selects_the_platform_collector()
    {
        var provider = ProcessResourceProvider.Create();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsProcessCpuReader>(provider);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxProcessCpuReader>(provider);
        }
        else
        {
            Assert.Empty(provider.ReadCpuSnapshots());
        }
    }
}
