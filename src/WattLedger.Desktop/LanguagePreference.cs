using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WattLedger.Desktop;

public enum AppLanguage { System, English, SimplifiedChinese }

/// <summary>Language and theme share a preferences file; saving preserves all unrelated fields.</summary>
public sealed class LanguagePreference(string? path = null)
{
    public string FilePath { get; } = path ?? ThemePreference.DefaultPath;

    public AppLanguage Load()
    {
        try
        {
            var value = JsonNode.Parse(File.ReadAllText(FilePath))?["language"]?.GetValue<string>();
            return Enum.TryParse<AppLanguage>(value, true, out var language) && Enum.IsDefined(language)
                ? language : AppLanguage.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return AppLanguage.System;
        }
    }

    public void Save(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        JsonObject settings;
        try { settings = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { settings = new JsonObject(); }
        settings["language"] = language.ToString();
        var path = Path.GetFullPath(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // As with theme saving, Unix overwrite has an unlink/rename boundary; Load tolerates a missing file.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
