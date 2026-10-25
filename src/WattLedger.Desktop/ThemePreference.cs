using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WattLedger.Desktop;

public enum ThemeMode { System, Light, Dark }

/// <summary>UI-only preference shared by both desktop frontends; never stored in the ledger.</summary>
public sealed class ThemePreference
{
    public ThemePreference(string? path = null) => FilePath = path ?? DefaultPath;
    public string FilePath { get; }

    public static string DefaultPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattLedger", "preferences.json")
        : Path.Combine(ConfigHome(), "wattledger", "preferences.json");

    private static string ConfigHome()
    {
        var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    public ThemeMode Load()
    {
        try
        {
            var text = JsonNode.Parse(File.ReadAllText(FilePath))?["theme"]?.GetValue<string>();
            return Enum.TryParse<ThemeMode>(text, true, out var mode) && Enum.IsDefined(mode)
                ? mode : ThemeMode.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ThemeMode.System;
        }
    }

    public void Save(ThemeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        JsonObject settings;
        try { settings = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { settings = new JsonObject(); }
        settings["theme"] = mode.ToString();
        var path = Path.GetFullPath(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // overwrite:true is unlink+rename on Unix rather than an atomic replace; a crash in between can drop the
            // file, which Load already tolerates by defaulting back to System.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>Semantic colors, shared so custom charts and both desktop shells use the same contrast.</summary>
public static class ThemePalette
{
    public static IReadOnlyDictionary<string, string> Colors(bool dark) => dark ? Dark : Light;

    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["WindowBackgroundBrush"] = "#0B0D10",
        ["CardBrush"] = "#14181F",
        ["CardAltBrush"] = "#10141A",
        ["BorderBrush"] = "#2B3745",
        ["TextBrush"] = "#E6EDF3",
        ["MutedBrush"] = "#9AAABD",
        ["GreenBrush"] = "#3DDC97",
        ["OrangeBrush"] = "#FFB454",
        ["BlueBrush"] = "#5AA9FF",
        ["RedBrush"] = "#FF6B6B",
        ["ButtonBrush"] = "#202B38",
        ["ButtonHoverBrush"] = "#29384A",
        ["ButtonPressedBrush"] = "#35475E",
        ["SelectionBrush"] = "#253C57",
        ["RowHoverBrush"] = "#1B2633",
        ["ChartGridBrush"] = "#263341",
        ["ChartZeroBrush"] = "#687D94",
        ["ChartLabelBrush"] = "#9AAABD",
        ["ChartPowerBrush"] = "#3DDC97",
        ["ChartBatteryBrush"] = "#5AA9FF",
        ["ChartSelectionBrush"] = "#405AA9FF",
        ["ChartHoverBrush"] = "#F2111924",
        ["ChartAreaTopColor"] = "#513DDC97",
        ["ChartAreaBottomColor"] = "#053DDC97"
    };
    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["WindowBackgroundBrush"] = "#F3F6FA",
        ["CardBrush"] = "#FFFFFF",
        ["CardAltBrush"] = "#F6F8FB",
        ["BorderBrush"] = "#CDD7E3",
        ["TextBrush"] = "#172537",
        ["MutedBrush"] = "#52647A",
        ["GreenBrush"] = "#087C52",
        ["OrangeBrush"] = "#9A5300",
        ["BlueBrush"] = "#1664B7",
        ["RedBrush"] = "#BB263B",
        ["ButtonBrush"] = "#EAF0F7",
        ["ButtonHoverBrush"] = "#DCE7F4",
        ["ButtonPressedBrush"] = "#CCDCEF",
        ["SelectionBrush"] = "#D6E8FD",
        ["RowHoverBrush"] = "#EDF3FA",
        ["ChartGridBrush"] = "#DFE5ED",
        ["ChartZeroBrush"] = "#8B9CB1",
        ["ChartLabelBrush"] = "#52647A",
        ["ChartPowerBrush"] = "#087C52",
        ["ChartBatteryBrush"] = "#1664B7",
        ["ChartSelectionBrush"] = "#301664B7",
        ["ChartHoverBrush"] = "#F7FFFFFF",
        ["ChartAreaTopColor"] = "#40087C52",
        ["ChartAreaBottomColor"] = "#05087C52"
    };
}
