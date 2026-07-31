using Microsoft.Extensions.Options;

namespace OpencodeRemote.Tests.Persistence;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"opencode-remote-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StateSurvivesStoreRecreation()
    {
        var path = Path.Combine(_directory, "state.json");
        var options = Options.Create(new RemoteOptions { StateFile = path });
        var expected = new RemoteState(123, "project", "session");

        await new StateStore(options).SaveAsync(expected, CancellationToken.None);
        var actual = await new StateStore(options).GetAsync(CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MissingFileReturnsEmptyState()
    {
        var path = Path.Combine(_directory, "missing", "state.json");
        var store = new StateStore(Options.Create(new RemoteOptions { StateFile = path }));

        var actual = await store.GetAsync(CancellationToken.None);

        Assert.Equal(new RemoteState(), actual);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SaveOverwritesPersistedAndCachedState()
    {
        var path = Path.Combine(_directory, "state.json");
        var options = Options.Create(new RemoteOptions { StateFile = path });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(1, "old", "session-1"), CancellationToken.None);

        var expected = new RemoteState(2, "new", "session-2");
        await store.SaveAsync(expected, CancellationToken.None);

        Assert.Equal(expected, await store.GetAsync(CancellationToken.None));
        Assert.Equal(expected, await new StateStore(options).GetAsync(CancellationToken.None));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task StateWithoutModelSelectionsRemainsCompatible()
    {
        var path = Path.Combine(_directory, "state.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, """
            {"ChatId":123,"ProjectAlias":"main","SessionId":"session-1","Agent":"plan"}
            """);
        var store = new StateStore(Options.Create(new RemoteOptions { StateFile = path }));

        var state = await store.GetAsync(CancellationToken.None);

        Assert.Equal(new RemoteState(123, "main", "session-1", "plan"), state);
        Assert.Null(state.ModelSelections);
        Assert.Null(state.TelegramHistoryStartMessageId);
    }

    [Fact]
    public async Task TelegramHistoryStartSurvivesStoreRecreation()
    {
        var path = Path.Combine(_directory, "state.json");
        var options = Options.Create(new RemoteOptions { StateFile = path });
        var expected = new RemoteState(123, "project", "session", TelegramHistoryStartMessageId: 456);

        await new StateStore(options).SaveAsync(expected, CancellationToken.None);
        var actual = await new StateStore(options).GetAsync(CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
