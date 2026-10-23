using System.Text.Json.Nodes;
using WattLedger.Desktop;

namespace WattLedger.App.Tests;

public sealed class ThemePreferenceTests
{
    [Fact]
    public void ThemePreferencePersistsEachModeWithoutLosingOtherSettings()
    {
        var directory = Directory.CreateTempSubdirectory("wattledger-theme-");
        try
        {
            var path = Path.Combine(directory.FullName, "preferences.json");
            File.WriteAllText(path, "{\"anotherPreference\": 42}");
            foreach (var mode in Enum.GetValues<ThemeMode>())
            {
                new ThemePreference(path).Save(mode);
                Assert.Equal(mode, new ThemePreference(path).Load());
                Assert.Equal(42, JsonNode.Parse(File.ReadAllText(path))!["anotherPreference"]!.GetValue<int>());
            }
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void ThemePreferenceDefaultsToSystemForMissingOrMalformedSettings()
    {
        var directory = Directory.CreateTempSubdirectory("wattledger-theme-");
        try
        {
            var path = Path.Combine(directory.FullName, "preferences.json");
            Assert.Equal(ThemeMode.System, new ThemePreference(path).Load());
            foreach (var text in new[] { "not json", "[]", "null", "{\"theme\":true}", "{\"theme\":\"Unknown\"}" })
            {
                File.WriteAllText(path, text);
                Assert.Equal(ThemeMode.System, new ThemePreference(path).Load());
            }
        }
        finally { directory.Delete(true); }
    }
}
