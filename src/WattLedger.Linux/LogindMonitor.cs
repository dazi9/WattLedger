using Tmds.DBus.Protocol;
using WattLedger.Core.Engine;

namespace WattLedger.Linux;

internal sealed class LogindMonitor : IAsyncDisposable
{
    private readonly WattLedgerService _service;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private DBusConnection? _connection;
    private IDisposable? _subscription;
    private bool _suspended;
    private bool _disposed;

    public LogindMonitor(WattLedgerService service) => _service = service;

    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(LogindMonitor));
            if (string.IsNullOrWhiteSpace(DBusAddress.System))
            {
                throw new InvalidOperationException("The system D-Bus address is not configured.");
            }

            var connection = new DBusConnection(DBusAddress.System);
            _connection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), linked.Token).ConfigureAwait(false);
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.login1",
                Interface = "org.freedesktop.login1.Manager",
                Member = "PrepareForSleep",
                Path = "/org/freedesktop/login1"
            };
            _subscription = await connection.AddMatchAsync<bool>(
                rule,
                static (message, _) => message.GetBodyReader().ReadBool(),
                HandleSignal,
                flags: ObserverFlags.None,
                emitOnCapturedContext: false).AsTask().WaitAsync(TimeSpan.FromSeconds(3), linked.Token).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Suspend/resume notifications connected to logind.");
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

    private void HandleSignal(Notification<bool> notification)
    {
        if (!notification.HasValue)
        {
            if (_suspended)
            {
                _suspended = false;
                _service.NotifyResume(confirmedByEvent: false);
                StatusChanged?.Invoke(this, "Logind connection ended during suspend; resumed sampling and will infer future sleep gaps.");
            }
            else
            {
                StatusChanged?.Invoke(this, "Logind connection ended; sleep gaps will be inferred from sample timestamps.");
            }

            return;
        }

        var prepareForSleep = notification.Value;
        try
        {
            if (prepareForSleep && !_suspended)
            {
                _suspended = true;
                _service.NotifySuspend();
            }
            else if (!prepareForSleep && _suspended)
            {
                _suspended = false;
                _service.NotifyResume();
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Could not update suspend state: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _stop.Cancel();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        if (_disposed)
        {
            _lifecycle.Release();
            return;
        }
        _disposed = true;
        _subscription?.Dispose();
        _connection?.Dispose();
        _subscription = null;
        _connection = null;
        _lifecycle.Release();
        _stop.Dispose();
        _lifecycle.Dispose();
    }
}
