using WattLedger.Core.Models;

namespace WattLedger.Core.Engine;

public sealed record SessionTrackerResult(
    SessionRecord? Current,
    IReadOnlyList<SessionRecord> Completed);

public sealed class SessionTracker
{
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(60);

    private SampleRecord? _lastSample;
    private SessionBuilder? _current;

    public SessionRecord? Current => _current?.ToRecord();

    public SessionTrackerResult Add(SampleRecord sample) => Add(sample, "ModernStandby", false);

    public SessionTrackerResult Add(
        SampleRecord sample,
        string gapSessionType,
        bool gapConfirmedBySuspendEvents = false)
    {
        var completed = new List<SessionRecord>();

        if (_current is null || _lastSample is null)
        {
            _current = new SessionBuilder(sample, TypeFor(sample));
            _lastSample = sample;
            return new SessionTrackerResult(_current.ToRecord(), completed);
        }

        var gap = sample.Timestamp - _lastSample.Timestamp;
        var confirmedSleepGap = gapSessionType == "SleepGap" &&
                                gapConfirmedBySuspendEvents &&
                                gap > TimeSpan.Zero;
        if (gap > GapThreshold || confirmedSleepGap)
        {
            var closed = _current.Close(_lastSample);
            if (closed.DurationSeconds > 0)
            {
                completed.Add(closed);
            }

            var standby = CreateStandbySession(_lastSample, sample, gapSessionType, gapConfirmedBySuspendEvents);
            if (standby.DurationSeconds > 0)
            {
                completed.Add(standby);
            }

            _current = new SessionBuilder(sample, TypeFor(sample));
            _lastSample = sample;
            return new SessionTrackerResult(_current.ToRecord(), completed);
        }

        var desiredType = TypeFor(sample);
        if (!string.Equals(desiredType, _current.Type, StringComparison.Ordinal))
        {
            _current.Accumulate(_lastSample, sample);
            var closed = _current.Close(sample);
            if (closed.DurationSeconds > 0)
            {
                completed.Add(closed);
            }

            _current = new SessionBuilder(sample, desiredType);
        }
        else
        {
            _current.Accumulate(_lastSample, sample);
        }

        _lastSample = sample;
        return new SessionTrackerResult(_current.ToRecord(), completed);
    }

    public SessionTrackerResult Close()
    {
        var completed = new List<SessionRecord>();
        if (_current is not null && _lastSample is not null)
        {
            var closed = _current.Close(_lastSample);
            if (closed.DurationSeconds > 0)
            {
                completed.Add(closed);
            }
        }

        _current = null;
        _lastSample = null;
        return new SessionTrackerResult(null, completed);
    }

    private static string TypeFor(SampleRecord sample)
    {
        if (sample.IsAcOnline)
        {
            return sample.IsCharging ? "Charging" : "AC";
        }

        return "Battery";
    }

    private static SessionRecord CreateStandbySession(
        SampleRecord previous,
        SampleRecord current,
        string type,
        bool confirmedBySuspendEvents)
    {
        var energyWh = EnergyIntegrator.CapacityDeltaWh(
            previous.RemainingMwh,
            current.RemainingMwh);
        var duration = current.Timestamp - previous.Timestamp;
        var averageWatts = EnergyIntegrator.AverageWatts(energyWh, duration);

        return new SessionRecord
        {
            Start = previous.Timestamp,
            End = current.Timestamp,
            Type = type,
            Note = type == "ModernStandby"
                ? current.IsAcOnline ? "AC" : "Battery"
                : $"{(confirmedBySuspendEvents ? "Suspend/resume event confirmed" : "Inferred sleep gap")}; " +
                  $"{(current.IsAcOnline ? "AC" : "battery")} state; " +
                  (previous.RemainingMwh is not null && current.RemainingMwh is not null
                      ? "energy estimated from capacity change"
                      : "battery energy unavailable"),
            StartPercent = previous.BatteryPercent,
            EndPercent = current.BatteryPercent,
            StartRemainingMwh = previous.RemainingMwh,
            EndRemainingMwh = current.RemainingMwh,
            EnergyWh = energyWh,
            AverageWatts = averageWatts,
            PeakWatts = Math.Abs(averageWatts)
        };
    }

    private sealed class SessionBuilder
    {
        private readonly DateTimeOffset _start;
        private readonly string _type;
        private readonly string? _note;
        private readonly int? _startPercent;
        private readonly long? _startRemainingMwh;

        private DateTimeOffset _end;
        private int? _endPercent;
        private long? _endRemainingMwh;
        private double _energyWh;
        private double _peakWatts;

        public SessionBuilder(SampleRecord sample, string type)
        {
            _start = sample.Timestamp;
            _end = sample.Timestamp;
            _type = type;
            _note = null;
            _startPercent = sample.BatteryPercent;
            _endPercent = sample.BatteryPercent;
            _startRemainingMwh = sample.RemainingMwh;
            _endRemainingMwh = sample.RemainingMwh;
        }

        public string Type => _type;

        public void Accumulate(SampleRecord previous, SampleRecord current)
        {
            var duration = current.Timestamp - previous.Timestamp;
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            var integrated = EnergyIntegrator.IntegrateWh(
                previous.NetDischargeWatts,
                current.NetDischargeWatts,
                duration);

            var capacityDelta = EnergyIntegrator.CapacityDeltaWh(
                previous.RemainingMwh,
                current.RemainingMwh);

            // Prefer the live power integration; fall back to the battery's
            // coulomb counter when the firmware reports no rate.
            var energy = Math.Abs(integrated) > 0.0001 ? integrated : capacityDelta;

            _energyWh += energy;
            _end = current.Timestamp;
            _endPercent = current.BatteryPercent;
            _endRemainingMwh = current.RemainingMwh;

            var watts = current.NetDischargeWatts ?? previous.NetDischargeWatts;
            if (watts is not null)
            {
                _peakWatts = Math.Max(_peakWatts, Math.Abs(watts.Value));
            }
        }

        public SessionRecord Close(SampleRecord last)
        {
            _end = last.Timestamp;
            _endPercent = last.BatteryPercent;
            _endRemainingMwh = last.RemainingMwh;
            return ToRecord();
        }

        public SessionRecord ToRecord()
        {
            var duration = _end - _start;
            var averageWatts = EnergyIntegrator.AverageWatts(_energyWh, duration);
            var peakWatts = _peakWatts > 0 ? _peakWatts : Math.Abs(averageWatts);

            return new SessionRecord
            {
                Start = _start,
                End = _end,
                Type = _type,
                Note = _note,
                StartPercent = _startPercent,
                EndPercent = _endPercent,
                StartRemainingMwh = _startRemainingMwh,
                EndRemainingMwh = _endRemainingMwh,
                EnergyWh = _energyWh,
                AverageWatts = averageWatts,
                PeakWatts = peakWatts
            };
        }
    }
}
