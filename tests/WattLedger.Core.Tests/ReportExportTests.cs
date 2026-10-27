using WattLedger.Core.Models;
using WattLedger.Core.Reporting;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Tests;

public sealed class ReportExportTests
{
    [Fact]
    public void ExportCsv_quotes_delimiters_and_line_breaks_inside_text_fields()
    {
        var path = TempPath("csv");
        try
        {
            var timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
            ReportBuilder.ExportCsv(
            [
                new SampleRecord
                {
                    Timestamp = timestamp,
                    PowerState = "Battery",
                    DischargeMw = 6_000,
                    IsDischarging = true,
                    ForegroundProcess = "calc,.exe",
                    DeviceName = "line1\nline2"
                }
            ], path);

            using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(path)
            {
                HasFieldsEnclosedInQuotes = true,
                Delimiters = [","]
            };
            var header = parser.ReadFields();
            Assert.Equal(20, header!.Length);
            Assert.Equal("foreground_process", header[17]);
            Assert.Equal("device_name", header[19]);
            var row = parser.ReadFields();
            Assert.NotNull(row);
            Assert.Null(parser.ReadFields()); // The embedded newline stayed inside its quoted field.
            Assert.Equal("calc,.exe", row![17]);
            Assert.Equal("line1\nline2", row[19]);
            Assert.Equal("6000", row[7]);
            Assert.Equal("1", row[11]);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void ExportCsv_neutralizes_formula_prefixes_in_text_fields_only()
    {
        var path = TempPath("csv");
        try
        {
            var timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
            ReportBuilder.ExportCsv(
            [
                new SampleRecord
                {
                    Timestamp = timestamp,
                    PowerState = "Battery",
                    DischargeMw = 6_000,
                    IsDischarging = true,
                    ForegroundProcess = "=HYPERLINK(\"http://example.invalid\",\"click\")",
                    DeviceName = "+SUM(A1)"
                },
                new SampleRecord
                {
                    Timestamp = timestamp.AddSeconds(5),
                    PowerState = "Battery",
                    DischargeMw = 6_000,
                    IsDischarging = true,
                    ForegroundProcess = "explorer",
                    DeviceName = "@cmd"
                }
            ], path);

            var text = File.ReadAllText(path);
            Assert.Contains("'=HYPERLINK", text);
            Assert.Contains(",'+SUM(A1)", text);
            Assert.Contains("explorer", text);
            Assert.Contains("'@cmd", text);
            Assert.DoesNotContain("'6000", text);
            Assert.DoesNotContain("'Battery", text);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void ExportCsv_uses_invariant_numbering()
    {
        var path = TempPath("csv");
        try
        {
            var timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
            ReportBuilder.ExportCsv(
            [
                new SampleRecord
                {
                    Timestamp = timestamp,
                    PowerState = "Battery",
                    DischargeMw = 6_000,
                    IsDischarging = true,
                    RaplPackageWatts = 12.3456
                }
            ], path);

            var line = File.ReadLines(path).Last();
            Assert.Contains(",12.346,", line);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void ExportMarkdown_escapes_pipes_in_notes_and_lists_the_sessions()
    {
        var path = TempPath("md");
        try
        {
            using var fixture = new LedgerDatabaseTests.DatabaseFixture();
            var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
            fixture.Database.UpsertSession(new SessionRecord
            {
                Start = start,
                End = start.AddMinutes(10),
                Type = "Battery",
                StartPercent = 80,
                EndPercent = 70,
                EnergyWh = 2,
                AverageWatts = 12,
                PeakWatts = 18,
                Note = "rate | unreliable"
            });

            ReportBuilder.ExportMarkdown(ReportBuilder.BuildDay(fixture.Database, DateOnly.FromDateTime(start.LocalDateTime)), path);

            var markdown = File.ReadAllText(path);
            Assert.Contains("rate \\| unreliable", markdown);
            Assert.Contains("2.00 Wh", markdown);
            Assert.Contains("12.0 W", markdown);
            Assert.Contains("80% → 70%", markdown);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void ExportJson_writes_a_parseable_day_report()
    {
        var path = TempPath("json");
        try
        {
            using var fixture = new LedgerDatabaseTests.DatabaseFixture();
            var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
            fixture.Database.UpsertSession(new SessionRecord
            {
                Start = start,
                End = start.AddMinutes(10),
                Type = "Battery",
                EnergyWh = 2,
                AverageWatts = 12,
                PeakWatts = 18
            });

            var report = ReportBuilder.BuildDay(fixture.Database, DateOnly.FromDateTime(start.LocalDateTime));
            ReportBuilder.ExportJson(report, path);

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, document.RootElement.GetProperty("DischargeWh").GetDouble(), 6);
            Assert.Equal("2026-10-20", document.RootElement.GetProperty("Date").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("Sessions").GetArrayLength());
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void FormatDuration_switches_units_at_one_hour()
    {
        Assert.Equal("5m 30s", ReportBuilder.FormatDuration(330));
        Assert.Equal("2h 5m", ReportBuilder.FormatDuration(2 * 3600 + 5 * 60));
        Assert.Equal("0m 0s", ReportBuilder.FormatDuration(-5));
    }

    private static string TempPath(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wattledger-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"report.{extension}");
    }
}
