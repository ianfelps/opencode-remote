namespace OpencodeRemote.Tests.Telegram;

public sealed class TelegramRetryPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(20, 30)]
    public void AppliesExponentialDelayWithThirtySecondLimit(int errors, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TelegramRetryPolicy.GetDelay(errors));
    }
}
