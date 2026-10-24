namespace WattLedger.App;

/// <summary>Starts the sampler once and observes failures independently of window visibility.</summary>
internal sealed class SamplerStartup
{
    private readonly Func<Task> _start;
    private readonly Action<Exception> _reportFailure;
    private readonly Lazy<Task<bool>> _startup;

    public SamplerStartup(Func<Task> start, Action<Exception> reportFailure)
    {
        _start = start;
        _reportFailure = reportFailure;
        _startup = new Lazy<Task<bool>>(StartCoreAsync);
    }

    public Task<bool> StartAsync() => _startup.Value;

    private async Task<bool> StartCoreAsync()
    {
        try
        {
            await _start();
            return true;
        }
        catch (Exception ex)
        {
            _reportFailure(ex);
            return false;
        }
    }
}
