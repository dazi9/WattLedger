using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;
using WattLedger.Linux;

namespace WattLedger.Linux.Tests;

public sealed class IconTests
{
    [AvaloniaFact]
    public void Raster_icon_has_transparent_corners_dark_body_and_green_bolt()
    {
        using var bitmap = LedgerIcon.CreateBitmap();
        var pixels = new byte[64 * 64 * 4];
        var pinnedPixels = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, 64, 64), pinnedPixels.AddrOfPinnedObject(), pixels.Length, 64 * 4);
        }
        finally
        {
            pinnedPixels.Free();
        }

        Assert.Equal(0, ReadPixel(pixels, 0, 0)[3]);
        AssertRgbNear(new byte[] { 0x17, 0x23, 0x31 }, ReadPixel(pixels, 32, 55));
        AssertRgbNear(new byte[] { 0x3D, 0xDC, 0x97 }, ReadPixel(pixels, 25, 31));
    }

    private static byte[] ReadPixel(byte[] pixels, int x, int y)
    {
        var offset = (y * 64 + x) * 4;
        return pixels[offset..(offset + 4)];
    }

    private static void AssertRgbNear(byte[] expected, byte[] actual)
    {
        Assert.Equal(255, actual[3]);
        var forwardError = Enumerable.Range(0, 3).Sum(channel => Math.Abs(expected[channel] - actual[channel]));
        var reverseError = Math.Abs(expected[0] - actual[2]) + Math.Abs(expected[1] - actual[1]) + Math.Abs(expected[2] - actual[0]);
        var rgb = forwardError <= reverseError ? actual[..3] : [actual[2], actual[1], actual[0]];
        for (var channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(expected[channel] - rgb[channel]), 0, 3);
    }
}
