using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WattLedger.Core.Engine;
using WattLedger.Desktop;

namespace WattLedger.Linux.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void LanguagePreferencePreservesThemeAndUnknownSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wattledger-language-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            File.WriteAllText(path, "{\"theme\":\"Dark\",\"custom\":42,\"language\":\"invalid\"}");
            var preference = new LanguagePreference(path);
            Assert.Equal(AppLanguage.System, preference.Load());
            preference.Save(AppLanguage.SimplifiedChinese);
            Assert.Equal(AppLanguage.SimplifiedChinese, new LanguagePreference(path).Load());
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal("Dark", json.RootElement.GetProperty("theme").GetString());
                Assert.Equal(42, json.RootElement.GetProperty("custom").GetInt32());
            }
            new ThemePreference(path).Save(ThemeMode.Light);
            Assert.Equal(AppLanguage.SimplifiedChinese, preference.Load());
            File.WriteAllText(path, "not JSON");
            Assert.Equal(AppLanguage.System, preference.Load());
            Assert.Throws<ArgumentOutOfRangeException>(() => preference.Save((AppLanguage)99));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SystemLanguageAndResourceFormatsHaveSafeFallbacks()
    {
        Assert.Equal("zh-CN", AppText.ResolveCulture(AppLanguage.System, CultureInfo.GetCultureInfo("zh-TW")).Name);
        Assert.Equal("en", AppText.ResolveCulture(AppLanguage.System, CultureInfo.GetCultureInfo("fr-FR")).Name);
        Assert.Equal("en", AppText.ResolveCulture(AppLanguage.English, CultureInfo.GetCultureInfo("zh-CN")).Name);
        Assert.True(AppText.ChineseStrings.Count > 100);
        foreach (var (source, translated) in AppText.ChineseStrings)
        {
            var sourceFormat = CompositeFormat.Parse(source);
            var translatedFormat = CompositeFormat.Parse(translated);
            Assert.Equal(sourceFormat.MinimumArgumentCount, translatedFormat.MinimumArgumentCount);
            static string[] Arguments(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[^}]*?)\}(?!\})")
                .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
            Assert.Equal(Arguments(source), Arguments(translated));
            Assert.False(string.IsNullOrWhiteSpace(translated));
        }
    }

    [AvaloniaFact]
    public async Task LanguageSwitchRefreshesVisibleTextWithoutResettingChartOrStartupFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wattledger-language-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var preference = new LanguagePreference(Path.Combine(directory, "preferences.json"));
        var original = AppText.CurrentLanguage;
        using var service = new WattLedgerService(Path.Combine(directory, "ledger.sqlite"));
        service.Database.Initialize();
        MainWindow? window = null;
        try
        {
            AppText.Initialize(preference);
            AppText.SetLanguage(AppLanguage.English);
            window = new MainWindow(service, false, () => { }, monitorTrayHost: false);
            window.Height = 1050;
            window.Show();
            await window.ReportRefreshTask;
            var chart = window.GetVisualDescendants().OfType<PowerChart>().Single();
            var first = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(8));
            chart.Samples = Enumerable.Range(0, 361).Select(index => new PowerPoint(first.AddSeconds(index * 10),
                8 + 4 * Math.Sin(index / 20d), 70)).ToArray();
            chart.Zoom(.5);
            chart.SelectRange(first.AddMinutes(50), first.AddMinutes(52));
            var range = chart.VisibleRange;
            var selection = chart.Selection!;
            var numberCulture = CultureInfo.CurrentCulture;
            AppText.SetLanguage(AppLanguage.SimplifiedChinese);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "功率与电量 · 今天");
            Assert.Equal(range, chart.VisibleRange);
            Assert.Equal(selection.Start, chart.Selection!.Start);
            Assert.False(chart.IsFollowingLive);
            Assert.Equal(numberCulture, CultureInfo.CurrentCulture);
            Assert.Equal("fallback message", AppText.Get("fallback message"));
            Assert.Equal("raw error {detail}", AppText.Format("raw error {detail}"));
            Assert.Equal(AppLanguage.SimplifiedChinese, window.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => control.SelectedItem is AppLanguage).SelectedItem);

            if (Environment.GetEnvironmentVariable("WATTLEDGER_LOCALIZATION_SCREENSHOT") is { Length: > 0 } path)
            {
                window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1120, 1050));
                bitmap.Render(window);
                using var output = File.Create(path);
                bitmap.Save(output, PngBitmapEncoderOptions.Default);
            }

            window.SetStartupError("writer lease unavailable");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "未在记录");
            AppText.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "NOT RECORDING");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("writer lease unavailable") == true);
            Assert.Equal("language", AppText.Get("language"));
        }
        finally
        {
            AppText.SetLanguage(original);
            AppText.Initialize();
            if (window is not null) await window.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
