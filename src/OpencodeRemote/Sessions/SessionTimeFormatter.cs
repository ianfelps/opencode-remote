using System.Globalization;

namespace OpencodeRemote.Sessions;

public static class SessionTimeFormatter
{
    public static string Format(long unixTimeMilliseconds)
        => Format(unixTimeMilliseconds, TimeZoneInfo.Local);

    public static string Format(long unixTimeMilliseconds, TimeZoneInfo timeZone)
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMilliseconds);
        return TimeZoneInfo.ConvertTime(instant, timeZone)
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public static string GetUtcOffsetLabel(long unixTimeMilliseconds)
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMilliseconds);
        var offset = TimeZoneInfo.Local.GetUtcOffset(instant);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"UTC{sign}{offset.Hours:00}:{offset.Minutes:00}";
    }

}
