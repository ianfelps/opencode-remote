namespace OpencodeRemote.Telegram;

public static class TelegramRetryPolicy
{
    public static TimeSpan GetDelay(int consecutiveErrors)
    {
        var exponent = Math.Clamp(consecutiveErrors, 1, 5);
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, exponent)));
    }
}
