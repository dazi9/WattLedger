namespace WattLedger.Core.Time;

/// <summary>
/// Local-calendar day boundaries. A day is a half-open interval
/// <c>[local midnight, next local midnight)</c>; resolving each boundary separately keeps
/// 23-hour and 25-hour daylight-saving days correct.
/// </summary>
public static class LocalDay
{
    public static DateTimeOffset Start(DateOnly date, TimeZoneInfo? timeZone = null)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var zone = timeZone ?? TimeZoneInfo.Local;
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static DateTimeOffset End(DateOnly date, TimeZoneInfo? timeZone = null) =>
        Start(date.AddDays(1), timeZone);

    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly date, TimeZoneInfo? timeZone = null) =>
        (Start(date, timeZone), End(date, timeZone));

    public static DateOnly DateOf(DateTimeOffset timestamp, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone ?? TimeZoneInfo.Local);
        return DateOnly.FromDateTime(local.DateTime);
    }
}
