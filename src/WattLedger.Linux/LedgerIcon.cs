using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace WattLedger.Linux;

internal static class LedgerIcon
{
    public static WindowIcon CreateWindowIcon()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#172331")), null,
                new RoundedRect(new Rect(2, 2, 60, 60), 15));
            var lightning = Geometry.Parse("M38 9 L18 35 L30 35 L27 55 L47 27 L34 27 Z");
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#3DDC97")), null, lightning);
        }

        return new WindowIcon(bitmap);
    }
}
