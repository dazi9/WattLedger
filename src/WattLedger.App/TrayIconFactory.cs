using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WattLedger.App;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var body = RoundedRect(new Rectangle(5, 8, 20, 16), 4);
        using var background = new SolidBrush(Color.FromArgb(24, 32, 42));
        graphics.FillPath(background, body);

        using var outline = new Pen(Color.FromArgb(225, 233, 242), 2);
        graphics.DrawPath(outline, body);

        using var terminal = new SolidBrush(Color.FromArgb(225, 233, 242));
        graphics.FillRectangle(terminal, 25, 13, 3, 6);

        using var level = new SolidBrush(Color.FromArgb(61, 220, 151));
        graphics.FillRectangle(level, 8, 11, 14, 10);

        using var bolt = new SolidBrush(Color.FromArgb(11, 13, 16));
        graphics.FillPolygon(bolt, new[]
        {
            new PointF(17, 9),
            new PointF(12, 18),
            new PointF(16, 18),
            new PointF(14, 24),
            new PointF(21, 14),
            new PointF(17, 14)
        });

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
