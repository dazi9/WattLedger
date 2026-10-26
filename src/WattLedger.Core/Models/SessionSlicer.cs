namespace WattLedger.Core.Models;

/// <summary>Clips an overlapping session to a report window and prorates its values.</summary>
internal static class SessionSlicer
{
    public static SessionRecord? Clip(SessionRecord session, DateTimeOffset from, DateTimeOffset to)
    {
        var totalSeconds = (session.End - session.Start).TotalSeconds;
        if (totalSeconds <= 0 || to <= from) return null;

        var start = session.Start < from ? from : session.Start;
        var end = session.End > to ? to : session.End;
        if (end <= start) return null;

        var startFraction = (start - session.Start).TotalSeconds / totalSeconds;
        var endFraction = (end - session.Start).TotalSeconds / totalSeconds;
        var overlapFraction = (end - start).TotalSeconds / totalSeconds;

        return session with
        {
            Start = start,
            End = end,
            StartPercent = Interpolate(session.StartPercent, session.EndPercent, startFraction),
            EndPercent = Interpolate(session.StartPercent, session.EndPercent, endFraction),
            StartRemainingMwh = Interpolate(session.StartRemainingMwh, session.EndRemainingMwh, startFraction),
            EndRemainingMwh = Interpolate(session.StartRemainingMwh, session.EndRemainingMwh, endFraction),
            EnergyWh = session.EnergyWh * overlapFraction,
            // Average power is unchanged by linear proration; peak cannot be attributed to a
            // particular part of the session, so keep the full-session value as an upper bound.
            AverageWatts = session.AverageWatts,
            PeakWatts = session.PeakWatts
        };
    }

    private static int? Interpolate(int? start, int? end, double fraction) =>
        start is null || end is null ? null : (int)Math.Round(start.Value + (end.Value - start.Value) * fraction);

    private static long? Interpolate(long? start, long? end, double fraction) =>
        start is null || end is null ? null : (long)Math.Round(start.Value + (end.Value - start.Value) * fraction);
}
