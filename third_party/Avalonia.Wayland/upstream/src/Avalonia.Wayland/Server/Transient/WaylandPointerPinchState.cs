using System;

namespace Avalonia.Wayland.Server.Transient;

// Converts the protocol's cumulative scale into Avalonia's incremental magnify
// contract. Keeping this independent of the proxy makes sequence edge cases testable.
internal sealed class WaylandPointerPinchState
{
    private double _scale;
    public bool IsActive { get; private set; }

    public void Begin()
    {
        _scale = 1;
        IsActive = true;
    }

    public void End() => IsActive = false;

    public bool TryUpdate(double scale, out double magnification)
    {
        magnification = 0;
        if (!IsActive || !double.IsFinite(scale) || scale <= 0)
            return false;
        var relativeScale = scale / _scale;
        if (!double.IsFinite(relativeScale) || relativeScale <= 0)
            return false;
        _scale = scale;
        magnification = relativeScale - 1;
        return magnification != 0;
    }
}
