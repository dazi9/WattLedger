namespace WattLedger.Linux;

/// <summary>Tracks report invalidation while keeping hidden windows free of report work.</summary>
internal sealed class ReportRefreshGate
{
    private bool _visible;
    private bool _dirty = true;

    public bool IsDirty => _dirty;
    public bool IsVisible => _visible;

    public bool MarkDirty()
    {
        _dirty = true;
        return _visible;
    }

    public bool SetVisible(bool visible)
    {
        var becameVisible = visible && !_visible;
        _visible = visible;
        return becameVisible && _dirty;
    }

    public bool TryConsumeRefresh()
    {
        if (!_visible || !_dirty) return false;
        _dirty = false;
        return true;
    }
}
