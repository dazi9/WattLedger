using System.Management;

namespace WattLedger.Core.Native;

public sealed class BrightnessReader
{
    private readonly object _gate = new();
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;
    private int? _lastValue;

    public int? Read(bool force = false)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (!force && now - _lastRead < TimeSpan.FromSeconds(60))
            {
                return _lastValue;
            }

            _lastRead = now;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                using var results = searcher.Get();

                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        var value = item["CurrentBrightness"];
                        if (value is not null)
                        {
                            _lastValue = Convert.ToInt32(value);
                            return _lastValue;
                        }
                    }
                }

                _lastValue = null;
            }
            catch
            {
                _lastValue = null;
            }

            return _lastValue;
        }
    }
}
