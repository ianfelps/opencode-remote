using OpencodeRemote.Sessions.Models;

namespace OpencodeRemote.Runtime;

public sealed record RuntimeSnapshot(
    string OpenCode = "iniciando",
    string Telegram = "iniciando",
    string Events = "aguardando",
    string? SessionId = null,
    string Agent = "build",
    string Model = "automático",
    CurrentTaskStatus? Task = null,
    string? Attention = null,
    string? LastError = null,
    DateTimeOffset? LastEventAt = null,
    string? Project = null,
    string? Directory = null);

public sealed class RuntimeStatusStore
{
    private readonly object _gate = new();
    private RuntimeSnapshot _snapshot = new();

    public RuntimeSnapshot Get()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void SetOpenCode(string value) => Update(current => current with { OpenCode = value });
    public void SetTelegram(string value) => Update(current => current with { Telegram = value });
    public void SetEvents(string value) => Update(current => current with { Events = value });
    public void SetProject(string project, string directory) => Update(current => current with { Project = project, Directory = directory });
    public void SetSelection(string? sessionId, string agent) => Update(current => current with { SessionId = sessionId, Agent = agent });
    public void SetModel(string value) => Update(current => current with { Model = value });
    public void SetTask(CurrentTaskStatus task) => Update(current => current with { Task = task, Attention = task.IsActive ? current.Attention : null });
    public void SetAttention(string? value) => Update(current => current with { Attention = value });
    public void SetError(string value) => Update(current => current with { LastError = value, LastEventAt = DateTimeOffset.UtcNow });
    public void Touch() => Update(current => current with { LastEventAt = DateTimeOffset.UtcNow });

    private void Update(Func<RuntimeSnapshot, RuntimeSnapshot> update)
    {
        lock (_gate)
        {
            _snapshot = update(_snapshot);
        }
    }
}
