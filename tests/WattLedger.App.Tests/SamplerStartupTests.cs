using WattLedger.App;
using WattLedger.Core.Engine;
using Microsoft.Data.Sqlite;

namespace WattLedger.App.Tests;

public sealed class SamplerStartupTests
{
    [Fact]
    public async Task TrayStartupReportsRealLeaseConflictWithoutLoadingAWindow()
    {
        var directory = Directory.CreateTempSubdirectory("wattledger-startup-");
        try
        {
            var databasePath = Path.Combine(directory.FullName, "ledger.db");
            using var service = new WattLedgerService(databasePath);
            service.Initialize();
            // A running CLI watch or another desktop holds this exclusive file lease.
            using var existingSampler = new FileStream(databasePath + ".sampling.lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var notifications = new List<Exception>();
            var startup = new SamplerStartup(() => service.StartAsync(), notifications.Add);

            Assert.False(await startup.StartAsync());
            Assert.False(await startup.StartAsync());

            var error = Assert.Single(notifications);
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("Another WattLedger sampler owns", error.Message);
            Assert.Contains(databasePath, error.Message);
            Assert.Null(service.Latest);
            Assert.Empty(service.GetSamples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartsOnceEvenWhenWindowOpensWhileStartupIsPending()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var notifications = new List<Exception>();
        var startup = new SamplerStartup(() =>
        {
            starts++;
            return pending.Task;
        }, notifications.Add);

        var trayStartup = startup.StartAsync();
        var windowStartup = startup.StartAsync();
        Assert.Same(trayStartup, windowStartup);
        Assert.Equal(1, starts);
        Assert.False(trayStartup.IsCompleted);

        pending.SetResult();
        Assert.True(await trayStartup);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task ReportsFailureAfterAsynchronousStartupCompletes()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<Exception>();
        var startup = new SamplerStartup(() => pending.Task, notifications.Add);
        var task = startup.StartAsync();
        var error = new IOException("Database cannot be opened.");

        pending.SetException(error);

        Assert.False(await task);
        Assert.Same(error, Assert.Single(notifications));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReportsSynchronousStartupFailure()
    {
        var error = new IOException("Database cannot be opened.");
        var notifications = new List<Exception>();
        var startup = new SamplerStartup(() => throw error, notifications.Add);

        Assert.False(await startup.StartAsync());
        Assert.Same(error, Assert.Single(notifications));
    }
}
