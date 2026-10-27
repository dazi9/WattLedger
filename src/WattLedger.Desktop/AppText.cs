using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WattLedger.Desktop;

/// <summary>Desktop-only translations. English source templates are stable fallback keys.</summary>
public sealed class AppText : INotifyPropertyChanged
{
    private static readonly CultureInfo SystemUICulture = CultureInfo.CurrentUICulture;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Catalog = new(LoadCatalog);
    private static LanguagePreference _preference = new();
    public static AppText Instance { get; } = new();
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.System;
    public static CultureInfo Culture { get; private set; } = ResolveCulture(AppLanguage.System, SystemUICulture);
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event EventHandler? LanguageChanged;

    public static void Initialize(LanguagePreference? preference = null)
    {
        _preference = preference ?? new LanguagePreference();
        Apply(_preference.Load());
    }

    public static void SetLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        if (ResolveCulture(language, SystemUICulture).TwoLetterISOLanguageName == "zh") _ = Catalog.Value;
        _preference.Save(language);
        Apply(language);
    }

    private static void Apply(AppLanguage language)
    {
        CurrentLanguage = language;
        Culture = ResolveCulture(language, SystemUICulture);
        // Keep CurrentCulture unchanged: numeric formats and HH:mm input retain their established rules.
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Instance.Revision++;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Revision)));
        LanguageChanged?.Invoke(Instance, EventArgs.Empty);
    }

    public static string Get(string english) => Culture.TwoLetterISOLanguageName == "zh" &&
        Catalog.Value.TryGetValue(english, out var translated) ? translated : english;

    public static string Format(string english, params object?[] arguments) =>
        arguments.Length == 0 ? Get(english) : string.Format(CultureInfo.CurrentCulture, Get(english), arguments);

    public static string SessionState(string type) => Get(type switch
    {
        "Battery" => "Battery",
        "AC" => "AC",
        "Charging" => "Charging",
        "ModernStandby" => "Modern Standby",
        "SleepGap" => "Sleep gap",
        _ => type
    });

    internal static CultureInfo ResolveCulture(AppLanguage language, CultureInfo system) =>
        CultureInfo.GetCultureInfo(language == AppLanguage.SimplifiedChinese ||
            (language == AppLanguage.System && system.TwoLetterISOLanguageName == "zh") ? "zh-CN" : "en");

    internal static IReadOnlyDictionary<string, string> ChineseStrings => Catalog.Value;

    private static IReadOnlyDictionary<string, string> LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WattLedger.Desktop.Strings.zh-CN.json")
            ?? throw new InvalidOperationException("The desktop translation resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The desktop translation resource is empty.");
    }
}
