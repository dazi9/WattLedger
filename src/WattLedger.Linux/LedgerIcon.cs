using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace WattLedger.Linux;

internal static class LedgerIcon
{
    public static WindowIcon CreateWindowIcon()
    {
        var bitmap = CreateBitmap();
        return new WindowIcon(bitmap);
    }

    internal static RenderTargetBitmap CreateBitmap(int size = 64)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        var scale = size / 64d;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#172331")), null,
                new RoundedRect(new Rect(5, 5, 54, 54), 15));
            var lightning = Geometry.Parse("M38 9 L18 35 L30 35 L27 55 L47 27 L34 27 Z");
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#3DDC97")), null, lightning);
        }

        return bitmap;
    }
}
