using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace WattLedger.Linux;

internal sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _socketPath;
    private int _disposed;

    private SingleInstance(Socket listener, string socketPath, Action activate)
    {
        _listener = listener;
        _socketPath = socketPath;
        _ = ListenAsync(activate);
    }

    public static SingleInstance? TryStart(Action activate)
    {
        var path = GetSocketPath();
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(8);
            return new SingleInstance(listener, path, activate);
        }
        catch (SocketException bindError) when (bindError.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            listener.Dispose();
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                client.Connect(new UnixDomainSocketEndPoint(path));
                client.Send(Encoding.UTF8.GetBytes("activate\n"));
                return null;
            }
            catch (SocketException connectError) when (connectError.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Confirm that no owner appeared between the initial bind and refusal before
                // removing this app's socket endpoint.
                using var confirmation = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    confirmation.Connect(new UnixDomainSocketEndPoint(path));
                    confirmation.Send(Encoding.UTF8.GetBytes("activate\n"));
                    return null;
                }
                catch (SocketException secondError) when (secondError.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    try { File.Delete(path); }
                    catch (IOException) { return null; }
                    catch (UnauthorizedAccessException) { return null; }
                }
                catch (SocketException)
                {
                    return null;
                }

                var retry = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    retry.Bind(new UnixDomainSocketEndPoint(path));
                    retry.Listen(8);
                    return new SingleInstance(retry, path, activate);
                }
                catch (SocketException)
                {
                    retry.Dispose();
                    return null;
                }
            }
        }
    }

    private async Task ListenAsync(Action activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                break;
            }

            using (client)
            {
                var buffer = new byte[32];
                try
                {
                    var read = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false);
                    if (Encoding.UTF8.GetString(buffer, 0, read).StartsWith("activate", StringComparison.Ordinal))
                    {
                        activate();
                    }
                }
                catch (SocketException) { }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static string GetSocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime) || !Directory.Exists(runtime))
        {
            runtime = Path.Combine(Path.GetTempPath(), $"wattledger-{GetUserId()}");
            Directory.CreateDirectory(runtime);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        return Path.Combine(runtime, "wattledger-linux.sock");
    }

    private static uint GetUserId()
    {
        try { return GetUid(); }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Dispose();
        try { File.Delete(_socketPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _stop.Dispose();
    }
}
