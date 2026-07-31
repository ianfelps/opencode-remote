namespace OpencodeRemote.Tests.Sessions;

public sealed class SessionTimeFormatterTests
{
    [Fact]
    public void FormatsSessionTimeUsingProvidedTimeZone()
    {
        var timestamp = DateTimeOffset.Parse("2026-07-30T03:15:00Z").ToUnixTimeMilliseconds();
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("test-utc-minus-three", TimeSpan.FromHours(-3), "Test", "Test");

        var result = SessionTimeFormatter.Format(timestamp, timeZone);

        Assert.Equal("30/07/2026 00:15", result);
    }

    [Fact]
    public void DefaultFormatUsesComputerTimeZone()
    {
        var timestamp = DateTimeOffset.Parse("2026-07-30T03:15:00Z").ToUnixTimeMilliseconds();
        var expected = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), TimeZoneInfo.Local)
            .ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, SessionTimeFormatter.Format(timestamp));
    }
}
