using OpencodeRemote.Runtime;

namespace OpencodeRemote.Tests.Runtime;

public sealed class RuntimeStatusStoreTests
{
    [Fact]
    public void TracksServiceAndTaskState()
    {
        var store = new RuntimeStatusStore();
        var started = DateTimeOffset.UtcNow;

        store.SetOpenCode("conectado");
        store.SetTelegram("conectado");
        store.SetSelection("session-1", "plan");
        store.SetTask(new CurrentTaskStatus(true, StartedAt: started, Step: "Testar"));
        store.SetAttention("aguardando resposta");

        var snapshot = store.Get();
        Assert.Equal("conectado", snapshot.OpenCode);
        Assert.Equal("conectado", snapshot.Telegram);
        Assert.Equal("session-1", snapshot.SessionId);
        Assert.Equal("plan", snapshot.Agent);
        Assert.Equal(started, snapshot.Task?.StartedAt);
        Assert.Equal("Testar", snapshot.Task?.Step);
        Assert.Equal("aguardando resposta", snapshot.Attention);
    }

    [Fact]
    public void IdleTaskClearsAttention()
    {
        var store = new RuntimeStatusStore();
        store.SetAttention("aguardando permissão");

        store.SetTask(new CurrentTaskStatus(false));

        Assert.Null(store.Get().Attention);
    }
}
