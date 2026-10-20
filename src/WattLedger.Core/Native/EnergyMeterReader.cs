using System.Runtime.InteropServices;
using WattLedger.Core.Models;

namespace WattLedger.Core.Native;

/// <summary>
/// Reads the Windows "Energy Meter" performance counter set. On Intel and AMD
/// machines this is backed by the CPU's RAPL energy counters and exposes the
/// package, core, graphics and DRAM power without a kernel driver.
/// Counter values are reported in milliwatts; this class converts them to watts.
/// </summary>
public sealed class EnergyMeterReader : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _counter;
    private bool _disposed;

    public EnergyMeterReader()
    {
        TryInitialize();
    }

    public RaplSnapshot Read()
    {
        lock (_gate)
        {
            if (_disposed || _query == IntPtr.Zero || _counter == IntPtr.Zero)
            {
                return RaplSnapshot.Unavailable;
            }

            try
            {
                if (PdhCollectQueryData(_query) != 0)
                {
                    return RaplSnapshot.Unavailable;
                }

                if (!TryReadItems(out var items) || items.Count == 0)
                {
                    return RaplSnapshot.Unavailable;
                }

                double package = 0;
                double cpu = 0;
                double gpu = 0;
                double dram = 0;
                var components = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (var (name, milliwatts) in items)
                {
                    if (milliwatts <= 0)
                    {
                        continue;
                    }

                    var watts = milliwatts / 1000.0;
                    components[name] = watts;

                    var normalized = name.ToUpperInvariant();
                    if (normalized.Contains("PKG", StringComparison.Ordinal))
                    {
                        package += watts;
                    }
                    else if (normalized.Contains("PP0", StringComparison.Ordinal) ||
                             normalized.Contains("CORE", StringComparison.Ordinal))
                    {
                        cpu += watts;
                    }
                    else if (normalized.Contains("PP1", StringComparison.Ordinal) ||
                             normalized.Contains("GFX", StringComparison.Ordinal) ||
                             normalized.Contains("GPU", StringComparison.Ordinal))
                    {
                        gpu += watts;
                    }
                    else if (normalized.Contains("DRAM", StringComparison.Ordinal))
                    {
                        dram += watts;
                    }
                }

                if (components.Count == 0)
                {
                    return RaplSnapshot.Unavailable;
                }

                if (package <= 0)
                {
                    package = cpu + gpu + dram;
                }

                var uncore = Math.Max(0, package - cpu - gpu - dram);

                return new RaplSnapshot
                {
                    Available = true,
                    PackageWatts = package > 0 ? package : null,
                    CpuWatts = cpu > 0 ? cpu : null,
                    GpuWatts = gpu > 0 ? gpu : null,
                    DramWatts = dram > 0 ? dram : null,
                    UncoreWatts = uncore > 0 ? uncore : null,
                    Components = components
                };
            }
            catch
            {
                return RaplSnapshot.Unavailable;
            }
        }
    }

    public IReadOnlyDictionary<string, double> ReadRawMilliwatts()
    {
        lock (_gate)
        {
            if (_disposed || _query == IntPtr.Zero || _counter == IntPtr.Zero)
            {
                return new Dictionary<string, double>();
            }

            try
            {
                if (PdhCollectQueryData(_query) != 0)
                {
                    return new Dictionary<string, double>();
                }

                return TryReadItems(out var items)
                    ? items.ToDictionary(i => i.Name, i => i.Milliwatts, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>();
            }
            catch
            {
                return new Dictionary<string, double>();
            }
        }
    }

    private void TryInitialize()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0)
        {
            return;
        }

        if (PdhAddEnglishCounterW(query, @"\Energy Meter(*)\Power", IntPtr.Zero, out var counter) != 0)
        {
            PdhCloseQuery(query);
            return;
        }

        _query = query;
        _counter = counter;

        // Prime the counter. The first sample is often invalid; subsequent reads
        // (5 seconds later in the sampler) return real data.
        PdhCollectQueryData(_query);
    }

    private bool TryReadItems(out List<(string Name, double Milliwatts)> items)
    {
        items = new List<(string, double)>();

        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(
            _counter,
            PdhFmtDouble,
            ref size,
            out var count,
            IntPtr.Zero);

        if (status != PdhMoreData || size == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(
                _counter,
                PdhFmtDouble,
                ref size,
                out count,
                buffer);

            if (status != 0 || count == 0)
            {
                return false;
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (var i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(buffer, i * itemSize);
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(itemPtr);
                if (item.SzName == IntPtr.Zero)
                {
                    continue;
                }

                if (item.FmtValue.CStatus is not (0 or 1))
                {
                    continue;
                }

                var name = Marshal.PtrToStringUni(item.SzName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                items.Add((name, item.FmtValue.DoubleValue));
            }

            return items.Count > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_counter != IntPtr.Zero)
            {
                PdhRemoveCounter(_counter);
                _counter = IntPtr.Zero;
            }

            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr SzName;
        public PdhFmtCounterValue FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        IntPtr query,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
