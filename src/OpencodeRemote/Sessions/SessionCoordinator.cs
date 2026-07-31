using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode;
using OpencodeRemote.OpenCode.Models;
using OpencodeRemote.Persistence;
using OpencodeRemote.Sessions.Models;

namespace OpencodeRemote.Sessions;

public sealed class SessionCoordinator(
    IOptions<RemoteOptions> options,
    StateStore stateStore,
    OpenCodeClient client)
{
    private sealed record ActiveSession(
        DateTimeOffset? StartedAt,
        bool IsPreparing,
        string? Step = null,
        string? Activity = null,
        int Files = 0,
        int Additions = 0,
        int Deletions = 0);

    private readonly RemoteOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveSession> _activeSessions = new();
    private static readonly TimeSpan BusyPropagationGrace = TimeSpan.FromSeconds(5);

    public IReadOnlyList<ProjectOptions> Projects => _options.Projects;

    public ProjectOptions? FindProject(string alias) =>
        _options.Projects.FirstOrDefault(project => string.Equals(project.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public async Task<RemoteState> SelectProjectAsync(long chatId, string alias, CancellationToken cancellationToken)
    {
        var project = FindProject(alias) ?? throw new InvalidOperationException("Projeto não autorizado.");
        if (!Directory.Exists(project.Path))
        {
            throw new InvalidOperationException("O diretório configurado para o projeto não existe.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await stateStore.GetAsync(cancellationToken);
            var state = current with { ChatId = chatId, ProjectAlias = project.Alias, SessionId = null, Agent = "build" };
            await stateStore.SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OpenCodeSession>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var (_, project) = await RequireProjectAsync(cancellationToken);
        return (await client.ListSessionsAsync(project.Path, cancellationToken))
            .OrderByDescending(session => session.Time.Updated)
            .Take(10)
            .ToArray();
    }

    public async Task<OpenCodeSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            var session = await client.CreateSessionAsync(project.Path, cancellationToken);
            await stateStore.SaveAsync(state with { SessionId = session.Id, Agent = "build" }, cancellationToken);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SelectSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            var sessions = await client.ListSessionsAsync(project.Path, cancellationToken);
            if (sessions.All(session => session.Id != sessionId))
            {
                throw new InvalidOperationException("Sessão não pertence ao projeto selecionado.");
            }

            var agent = NormalizeAgent(await client.GetLatestUserAgentAsync(project.Path, sessionId, cancellationToken));
            await stateStore.SaveAsync(state with { SessionId = sessionId, Agent = agent }, cancellationToken);
            return agent;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendPromptAsync(
        string text,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeSubmit = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            if (state.SessionId is null)
            {
                throw new InvalidOperationException("Selecione ou crie uma sessão primeiro.");
            }

            if (await IsSessionBusyAsync(project.Path, state.SessionId, cancellationToken))
            {
                throw new InvalidOperationException("A sessão ainda está ocupada. Aguarde ou use /stop.");
            }

            var active = new ActiveSession(DateTimeOffset.UtcNow, true);
            _activeSessions[state.SessionId] = active;
            try
            {
                if (beforeSubmit is not null)
                {
                    await beforeSubmit(cancellationToken);
                }
                await client.SendPromptAsync(
                    project.Path,
                    state.SessionId,
                    text,
                    NormalizeAgent(state.Agent),
                    FindModelSelection(state)?.Model,
                    cancellationToken);
                MarkPromptSubmitted(state.SessionId);
            }
            catch
            {
                _activeSessions.TryRemove(state.SessionId, out _);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void MarkIdle(string sessionId) => _activeSessions.TryRemove(sessionId, out _);

    internal CurrentTaskStatus? UpdateTaskStep(string sessionId, string step)
        => TryUpdateActiveSession(sessionId, active => active with { Step = step });

    internal CurrentTaskStatus? UpdateTaskActivity(string sessionId, string activity)
        => TryUpdateActiveSession(sessionId, active => active with { Activity = activity });

    internal CurrentTaskStatus? UpdateTaskDiff(string sessionId, int files, int additions, int deletions)
        => TryUpdateActiveSession(sessionId, active => active with
        {
            Files = files,
            Additions = additions,
            Deletions = deletions,
        });

    internal bool IsLocallyActive(string sessionId) => _activeSessions.ContainsKey(sessionId);

    internal bool IsPreparingPrompt(string sessionId)
        => _activeSessions.TryGetValue(sessionId, out var active) && active.IsPreparing;

    internal bool IsWithinBusyGrace(string sessionId)
        => _activeSessions.TryGetValue(sessionId, out var active)
            && active.StartedAt is { } startedAt
            && DateTimeOffset.UtcNow - startedAt < BusyPropagationGrace;

    public async Task SetAgentAsync(string agent, CancellationToken cancellationToken)
    {
        agent = NormalizeAgent(agent);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            if (state.SessionId is null)
            {
                throw new InvalidOperationException("Selecione ou crie uma sessão primeiro.");
            }

            if (await IsSessionBusyAsync(project.Path, state.SessionId, cancellationToken))
            {
                throw new InvalidOperationException("A sessão ainda está ocupada. Aguarde ou use /stop.");
            }

            if (!await client.AgentExistsAsync(project.Path, agent, cancellationToken))
            {
                throw new InvalidOperationException($"O agente '{agent}' não está disponível no OpenCode.");
            }

            await stateStore.SaveAsync(state with { Agent = agent }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OpenCodeProvider>> ListProvidersAsync(CancellationToken cancellationToken)
    {
        var (state, project) = await RequireProjectAsync(cancellationToken);
        if (state.SessionId is null)
        {
            throw new InvalidOperationException("Selecione ou crie uma sessão primeiro.");
        }

        return await client.ListProvidersAsync(project.Path, cancellationToken);
    }

    public async Task SetModelAsync(
        string expectedDirectory,
        string expectedSessionId,
        OpenCodeModelRef? model,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            if (state.SessionId is null
                || state.SessionId != expectedSessionId
                || !string.Equals(
                    Path.GetFullPath(project.Path),
                    Path.GetFullPath(expectedDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O projeto ou a sessão selecionada mudou. Use /model novamente.");
            }

            if (await IsSessionBusyAsync(project.Path, state.SessionId, cancellationToken))
            {
                throw new InvalidOperationException("A sessão ainda está ocupada. Aguarde ou use /stop.");
            }

            if (model is not null)
            {
                var providers = await client.ListProvidersAsync(project.Path, cancellationToken);
                var isAvailable = providers.Any(provider =>
                    provider.Id == model.ProviderId
                    && provider.Models.Any(candidate => candidate.Id == model.ModelId));
                if (!isAvailable)
                {
                    throw new InvalidOperationException("O modelo selecionado não está mais disponível.");
                }
            }

            var selections = (state.ModelSelections ?? [])
                .Where(selection => selection.ProjectAlias != project.Alias || selection.SessionId != state.SessionId)
                .ToList();
            selections.Add(new SessionModelSelection(project.Alias, state.SessionId, model));

            await stateStore.SaveAsync(state with { ModelSelections = selections }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CurrentModelInfo> GetCurrentModelAsync(CancellationToken cancellationToken)
        => (await GetStatusAsync(cancellationToken)).Model;

    public async Task<(RemoteState State, CurrentModelInfo Model)> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await stateStore.GetAsync(cancellationToken);
            return (state, await ResolveCurrentModelAsync(state, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CurrentModelInfo> ResolveCurrentModelAsync(RemoteState state, CancellationToken cancellationToken)
    {
        var project = state.ProjectAlias is null ? null : FindProject(state.ProjectAlias);
        if (project is null || state.SessionId is null)
        {
            return new CurrentModelInfo(null, CurrentModelSource.Automatic);
        }

        if (FindModelSelection(state) is { } selected)
        {
            return selected.Model is null
                ? new CurrentModelInfo(null, CurrentModelSource.Automatic)
                : new CurrentModelInfo(selected.Model, CurrentModelSource.Telegram);
        }

        try
        {
            if (await client.GetLatestUserModelAsync(project.Path, state.SessionId, cancellationToken) is { } sessionModel)
            {
                return new CurrentModelInfo(sessionModel, CurrentModelSource.Session);
            }

            if (await client.GetConfiguredModelAsync(project.Path, cancellationToken) is { } configuredModel)
            {
                return new CurrentModelInfo(configuredModel, CurrentModelSource.Configuration);
            }
        }
        catch (HttpRequestException)
        {
            // Status remains useful while OpenCode is unavailable or lacks these endpoints.
        }

        return new CurrentModelInfo(null, CurrentModelSource.Automatic);
    }

    public async Task AbortAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            if (state.SessionId is null)
            {
                throw new InvalidOperationException("Nenhuma sessão selecionada.");
            }

            await client.AbortAsync(project.Path, state.SessionId, cancellationToken);
            MarkIdle(state.SessionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsBusyAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var project = state.ProjectAlias is null ? null : FindProject(state.ProjectAlias);
        return project is not null
            && state.SessionId is not null
            && await IsSessionBusyAsync(project.Path, state.SessionId, cancellationToken);
    }

    public async Task<CurrentTaskStatus> GetCurrentTaskStatusAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var project = state.ProjectAlias is null ? null : FindProject(state.ProjectAlias);
        if (project is null
            || state.SessionId is null
            || !await IsSessionBusyAsync(project.Path, state.SessionId, cancellationToken))
        {
            return new CurrentTaskStatus(false);
        }

        return _activeSessions.TryGetValue(state.SessionId, out var active)
            ? ToCurrentTaskStatus(active)
            : new CurrentTaskStatus(true);
    }

    private async Task<bool> IsSessionBusyAsync(
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (_activeSessions.TryGetValue(sessionId, out var active)
            && active.StartedAt is { } startedAt
            && DateTimeOffset.UtcNow - startedAt < BusyPropagationGrace)
        {
            return true;
        }

        var remoteBusy = await client.IsSessionBusyAsync(directory, sessionId, cancellationToken);
        if (!remoteBusy)
        {
            MarkIdle(sessionId);
        }
        return remoteBusy;
    }

    private CurrentTaskStatus? TryUpdateActiveSession(string sessionId, Func<ActiveSession, ActiveSession> update)
    {
        while (_activeSessions.TryGetValue(sessionId, out var active))
        {
            var updated = update(active);
            if (_activeSessions.TryUpdate(sessionId, updated, active))
            {
                return ToCurrentTaskStatus(updated);
            }
        }

        return null;
    }

    private void MarkPromptSubmitted(string sessionId)
    {
        while (_activeSessions.TryGetValue(sessionId, out var active)
            && !_activeSessions.TryUpdate(sessionId, active with { IsPreparing = false }, active))
        {
        }
    }

    private static CurrentTaskStatus ToCurrentTaskStatus(ActiveSession active)
        => new(
            true,
            active.IsPreparing,
            active.StartedAt,
            active.Step,
            active.Activity,
            active.Files,
            active.Additions,
            active.Deletions);

    public async Task SetTelegramHistoryStartAsync(int messageId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messageId, 1);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await stateStore.GetAsync(cancellationToken);
            await stateStore.SaveAsync(state with { TelegramHistoryStartMessageId = messageId }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(RemoteState State, ProjectOptions Project)> RequireProjectAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var project = state.ProjectAlias is null ? null : FindProject(state.ProjectAlias);
        return project is null
            ? throw new InvalidOperationException("Selecione um projeto com /projects.")
            : (state, project);
    }

    private static string NormalizeAgent(string? agent)
        => string.Equals(agent, "plan", StringComparison.OrdinalIgnoreCase) ? "plan" : "build";

    private static SessionModelSelection? FindModelSelection(RemoteState state)
        => state.ModelSelections?.FirstOrDefault(selection =>
            selection.ProjectAlias == state.ProjectAlias
            && selection.SessionId == state.SessionId);
}
