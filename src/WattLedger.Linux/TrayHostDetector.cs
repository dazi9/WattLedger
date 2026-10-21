using Tmds.DBus.Protocol;

namespace WattLedger.Linux;

internal static class TrayHostDetector
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    public static async Task<bool> HasWatcherAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Address.Session))
        {
            return false;
        }

        using var connection = new Connection(Address.Session);
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                signature: "s",
                member: "NameHasOwner");
            writer.WriteString(WatcherName);
            message = writer.CreateMessage();
        }
        return await connection.CallMethodAsync(message,
            static (reply, _) => reply.GetBodyReader().ReadBool())
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }
}
