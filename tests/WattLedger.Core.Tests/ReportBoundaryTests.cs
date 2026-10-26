using Microsoft.Data.Sqlite;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;
using WattLedger.Core.Storage;
using WattLedger.Core.Time;

namespace WattLedger.Core.Tests;

public sealed class ReportBoundaryTests
{
    [Fact]
    public void Cross_midnight_session_is_clipped_and_prorated_on_both_days()
    {
        using var fixture = new DatabaseFixture();
        var day1 = new DateOnly(2026, 10, 20);
        var day2 = day1.AddDays(1);
        var (day1Start, _) = LocalDay.Range(day1);
        var (day2Start, _) = LocalDay.Range(day2);
        var sessionStart = day1Start.AddHours(23);
        var sessionEnd = day2Start.AddHours(1);
        fixture.Database.UpsertSession(new SessionRecord
        {
            Start = sessionStart,
            End = sessionEnd,
            Type = "Battery",
            StartPercent = 80,
            EndPercent = 60,
            StartRemainingMwh = 48_000,
            EndRemainingMwh = 36_000,
            EnergyWh = 10,
            AverageWatts = 5,
            PeakWatts = 8
        });

        var first = ReportBuilder.BuildDay(fixture.Database, day1);
        var firstSession = Assert.Single(first.Sessions);
        Assert.Equal(sessionStart, firstSession.Start);
        Assert.Equal(day2Start, firstSession.End);
        Assert.Equal(3600, firstSession.DurationSeconds);
        Assert.Equal(5, firstSession.EnergyWh, 6);
        Assert.Equal(80, firstSession.StartPercent);
        Assert.Equal(70, firstSession.EndPercent);
        Assert.Equal(5, first.DischargeWh, 6);
        Assert.Equal(3600, first.ActiveSeconds, 6);

        var second = ReportBuilder.BuildDay(fixture.Database, day2);
        var secondSession = Assert.Single(second.Sessions);
        Assert.Equal(day2Start, secondSession.Start);
        Assert.Equal(sessionEnd, secondSession.End);
        Assert.Equal(3600, secondSession.DurationSeconds);
        Assert.Equal(5, secondSession.EnergyWh, 6);
        Assert.Equal(70, secondSession.StartPercent);
        Assert.Equal(60, secondSession.EndPercent);
        Assert.Equal(5, second.DischargeWh, 6);
        Assert.Equal(3600, second.ActiveSeconds, 6);
        Assert.Equal(10, first.DischargeWh + second.DischargeWh, 6);
    }

    [Fact]
    public void Daily_summaries_clip_to_the_requested_day_and_never_report_another_date()
    {
        using var fixture = new DatabaseFixture();
        var day1 = new DateOnly(2026, 10, 20);
        var day2 = day1.AddDays(1);
        var (day1Start, day1End) = LocalDay.Range(day1);
        var (day2Start, day2End) = LocalDay.Range(day2);
        var sessionStart = day1Start.AddHours(23);
        fixture.Database.UpsertSession(new SessionRecord
        {
            Start = sessionStart,
            End = day2Start.AddHours(1),
            Type = "Battery",
            EnergyWh = 10,
            AverageWatts = 5,
            PeakWatts = 8
        });
        fixture.Database.UpsertSession(new SessionRecord
        {
            Start = day2Start,
            End = day2Start.AddHours(1),
            Type = "Battery",
            EnergyWh = 2,
            AverageWatts = 2,
            PeakWatts = 3
        });

        var firstDay = Assert.Single(fixture.Database.GetDailySummaries(day1Start, day1End));
        Assert.Equal(day1, firstDay.Date);
        Assert.Equal(5, firstDay.ActiveWh, 6);
        Assert.Equal(3600, firstDay.ActiveSeconds, 6);

        var secondDay = Assert.Single(fixture.Database.GetDailySummaries(day2Start, day2End));
        Assert.Equal(day2, secondDay.Date);
        Assert.Equal(7, secondDay.ActiveWh, 6);
        Assert.Equal(7200, secondDay.ActiveSeconds, 6);
    }

    [Fact]
    public void Session_starting_at_next_midnight_is_not_included_in_previous_day()
    {
        using var fixture = new DatabaseFixture();
        var day1 = new DateOnly(2026, 10, 20);
        var day2 = day1.AddDays(1);
        var (_, day1End) = LocalDay.Range(day1);
        var (day2Start, _) = LocalDay.Range(day2);
        fixture.Database.UpsertSession(new SessionRecord
        {
            Start = day2Start,
            End = day2Start.AddHours(1),
            Type = "Battery",
            EnergyWh = 3,
            AverageWatts = 3,
            PeakWatts = 4
        });

        Assert.Empty(ReportBuilder.BuildDay(fixture.Database, day1).Sessions);
        Assert.Single(ReportBuilder.BuildDay(fixture.Database, day2).Sessions);
        Assert.Equal(day1End, day2Start);
    }

    [Fact]
    public void Local_day_range_keeps_dst_days_at_23_and_25_hours()
    {
        var zone = DstTimeZone();
        var spring = LocalDay.Range(new DateOnly(2026, 3, 8), zone);
        var fall = LocalDay.Range(new DateOnly(2026, 11, 1), zone);

        Assert.Equal(TimeSpan.FromHours(23), spring.End - spring.Start);
        Assert.Equal(TimeSpan.FromHours(25), fall.End - fall.Start);
    }

    [Fact]
    public void Decimation_never_returns_more_than_the_requested_number_of_points()
    {
        using var fixture = new DatabaseFixture();
        var start = LocalDay.Start(new DateOnly(2026, 10, 20));
        for (var i = 0; i < 20; i++)
        {
            fixture.Database.InsertSample(new SampleRecord
            {
                Timestamp = start.AddMinutes(i),
                PowerState = "Battery",
                IsDischarging = true,
                DischargeMw = 5_000
            });
        }

        var samples = fixture.Database.GetSamples(start, start.AddMinutes(30), maxPoints: 5);

        Assert.Equal(5, samples.Count);
        Assert.Equal(start, samples[0].Timestamp);
        Assert.Equal(start.AddMinutes(19), samples[^1].Timestamp);
    }

    private static TimeZoneInfo DstTimeZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), month: 3, week: 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), month: 11, week: 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/DST", TimeSpan.FromHours(-5), "Test/DST", "Standard", "Daylight", [rule]);
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), $"wattledger-report-{Guid.NewGuid():N}");

        public DatabaseFixture()
        {
            Directory.CreateDirectory(_root);
            Database = new LedgerDatabase(Path.Combine(_root, "ledger.sqlite"));
            Database.Initialize();
        }

        public LedgerDatabase Database { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }
}
