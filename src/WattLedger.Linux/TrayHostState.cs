namespace WattLedger.Linux;

/// <summary>Interprets the filtered D-Bus NameOwnerChanged signal for the tray watcher.</summary>
internal static class TrayHostState
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher";

    public static bool? ApplyOwnerChanged(string name, string newOwner)
    {
        if (!string.Equals(name, WatcherName, StringComparison.Ordinal)) return null;
        return !string.IsNullOrEmpty(newOwner);
    }

    public static bool ConnectionLost() => false;
}
