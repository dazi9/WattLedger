namespace WattLedger.Desktop;

/// <summary>Converts input scales to chart time-span factors; fractional wheel input is preserved.</summary>
internal static class ChartGestureScale
{
    public static double WheelFactor(double steps) => double.IsFinite(steps)
        ? Math.Pow(.8, Math.Clamp(steps, -5, 5)) : 1;

    // Finger expansion magnifies the plot, so the visible time span shrinks.
    public static double MagnificationFactor(double relativeScale) =>
        double.IsFinite(relativeScale) && relativeScale > 0
            ? Math.Clamp(1 / relativeScale, .2, 5) : 1;
}

/// <summary>Turns cumulative distances/scales into incremental factors without compounding the whole gesture.</summary>
internal sealed class ChartScaleSession
{
    private double? _previous;

    public void Begin(double initialScale = 1) => _previous = Valid(initialScale) ? initialScale : null;
    public void End() => _previous = null;

    public double Update(double cumulativeScale)
    {
        if (!Valid(cumulativeScale)) return 1;
        var previous = _previous;
        _previous = cumulativeScale;
        return previous is { } value
            ? ChartGestureScale.MagnificationFactor(cumulativeScale / value) : 1;
    }

    private static bool Valid(double value) => double.IsFinite(value) && value > 0;
}
