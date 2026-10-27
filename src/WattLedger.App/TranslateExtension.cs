using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using WattLedger.Desktop;

namespace WattLedger.App;

/// <summary>Translate an English resource template and refresh it when the application language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension() { }
    public TranslateExtension(string text) => Text = text;
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(AppText.Revision))
        {
            Source = AppText.Instance,
            Mode = BindingMode.OneWay,
            Converter = new TranslationConverter(Text)
        }.ProvideValue(serviceProvider);

    private sealed class TranslationConverter(string text) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => AppText.Get(text);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
