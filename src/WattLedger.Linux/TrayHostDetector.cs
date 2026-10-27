using Tmds.DBus.Protocol;

namespace WattLedger.Linux;

/// <summary>Watches the session bus for the StatusNotifier host for this window's lifetime.</summary>
internal sealed class TrayHostDetector : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private DBusConnection? _connection;
    private IDisposable? _subscription;
    private bool _disposed;
    private int _availabilityState = -1;

    public event EventHandler<bool>? AvailabilityChanged;
    public bool? IsAvailable => Volatile.Read(ref _availabilityState) switch
    {
        0 => false,
        1 => true,
        _ => null
    };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TrayHostDetector));
            if (_connection is not null) return;
            if (string.IsNullOrWhiteSpace(DBusAddress.Session))
            {
                SetAvailability(false);
                return;
            }

            var connection = new DBusConnection(DBusAddress.Session);
            _connection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.DBus",
                Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged",
                Path = "/org/freedesktop/DBus",
                Arg0 = TrayHostState.WatcherName
            };
            var signalRevision = 0;
            _subscription = await connection.AddMatchAsync<string[]>(
                rule,
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return [reader.ReadString(), reader.ReadString(), reader.ReadString()];
                },
                notification =>
                {
                    if (!notification.HasValue)
                    {
                        NotifyConnectionLost();
                        return;
                    }

                    var values = notification.Value;
                    Interlocked.Increment(ref signalRevision);
                    if (values.Length == 3 && TrayHostState.ApplyOwnerChanged(values[0], values[2]) is { } available)
                    {
                        SetAvailability(available);
                    }
                },
                flags: ObserverFlags.None,
                emitOnCapturedContext: false).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            var revisionBeforeCheck = Volatile.Read(ref signalRevision);
            var hasWatcher = await HasWatcherAsync(connection, cancellationToken).ConfigureAwait(false);
            if (revisionBeforeCheck == Volatile.Read(ref signalRevision))
            {
                SetAvailability(hasWatcher);
            }
        }
        catch
        {
            _subscription?.Dispose();
            _subscription = null;
            _connection?.Dispose();
            _connection = null;
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    internal void SetAvailability(bool available)
    {
        Interlocked.Exchange(ref _availabilityState, available ? 1 : 0);
        AvailabilityChanged?.Invoke(this, available);
    }

    internal void NotifyConnectionLost() => SetAvailability(TrayHostState.ConnectionLost());

    private static async Task<bool> HasWatcherAsync(DBusConnection connection, CancellationToken cancellationToken)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                signature: "s",
                member: "NameHasOwner");
            writer.WriteString(TrayHostState.WatcherName);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message,
                static (reply, _) => reply.GetBodyReader().ReadBool())
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _subscription?.Dispose();
            _connection?.Dispose();
            _subscription = null;
            _connection = null;
        }
        finally { _lifecycle.Release(); }
    }
}
