namespace OpencodeRemote.Tests.Telegram;

public sealed class CallbackRegistryTests
{
    [Fact]
    public void CallbackCanOnlyBeReservedOnce()
    {
        var registry = new CallbackRegistry();
        var expected = new CallbackAction("session", "C:\\project", "session-1");

        var key = registry.Add(expected);

        Assert.True(registry.TryReserve(key, out var reservation));
        Assert.Equal(expected, reservation!.Action);
        Assert.False(registry.TryReserve(key, out _));
    }

    [Fact]
    public void ExpiredCallbackIsRejected()
    {
        var registry = new CallbackRegistry();
        var key = registry.Add(new CallbackAction("test", "C:\\project"), TimeSpan.FromMilliseconds(-1));

        Assert.False(registry.TryReserve(key, out _));
    }

    [Fact]
    public void KeysAreUniqueAndFitTelegramCallbackLimit()
    {
        var registry = new CallbackRegistry();

        var keys = Enumerable.Range(0, 100)
            .Select(index => registry.Add(new CallbackAction("test", "C:\\project", Value: index.ToString())))
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(keys, key => Assert.InRange(key.Length, 1, 64));
    }

    [Fact]
    public void UnknownCallbackIsRejected()
    {
        var registry = new CallbackRegistry();

        Assert.False(registry.TryReserve("unknown", out var reservation));
        Assert.Null(reservation);
    }

    [Fact]
    public void RestoredCallbackCanBeReservedAgain()
    {
        var registry = new CallbackRegistry();
        var expected = new CallbackAction("session", "C:\\project", "session-1");
        var key = registry.Add(expected);

        Assert.True(registry.TryReserve(key, out var first));
        Assert.True(registry.Restore(first!));
        Assert.True(registry.TryReserve(key, out var second));
        Assert.Equal(expected, second!.Action);
    }

    [Fact]
    public async Task ConcurrentReservationsHaveSingleWinner()
    {
        var registry = new CallbackRegistry();
        var key = registry.Add(new CallbackAction("session", "C:\\project", "session-1"));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => registry.TryReserve(key, out _))));

        Assert.Single(attempts, result => result);
    }

    [Fact]
    public async Task ConcurrentSiblingCallbacksHaveSingleWinner()
    {
        var registry = new CallbackRegistry();
        var group = registry.CreateGroup();
        var firstKey = registry.Add(new CallbackAction("permission", "C:\\project", Value: "once"), group: group);
        var secondKey = registry.Add(new CallbackAction("permission", "C:\\project", Value: "reject"), group: group);

        var attempts = await Task.WhenAll(
            Task.Run(() => registry.TryReserve(firstKey, out _)),
            Task.Run(() => registry.TryReserve(secondKey, out _)));

        Assert.Single(attempts, result => result);
    }

    [Fact]
    public void RestoringReservationRestoresAllSiblingCallbacks()
    {
        var registry = new CallbackRegistry();
        var group = registry.CreateGroup();
        var firstKey = registry.Add(new CallbackAction("permission", "C:\\project", Value: "once"), group: group);
        var secondKey = registry.Add(new CallbackAction("permission", "C:\\project", Value: "reject"), group: group);
        Assert.True(registry.TryReserve(firstKey, out var reservation));

        Assert.True(registry.Restore(reservation!));
        Assert.True(registry.TryReserve(secondKey, out var restored));
        Assert.Equal("reject", restored!.Action.Value);
    }
}
