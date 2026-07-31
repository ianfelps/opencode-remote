using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode;
using OpencodeRemote.OpenCode.Models;
using OpencodeRemote.Persistence;
using OpencodeRemote.Runtime;
using OpencodeRemote.Sessions.Models;

namespace OpencodeRemote.Sessions;

public sealed class SessionCoordinator(
    IOptions<RemoteOptions> options,
    StateStore stateStore,
    OpenCodeClient client,
    RuntimeStatusStore? runtime = null)
{
    private sealed record ActiveSession(
        Guid Generation,
        DateTimeOffset? StartedAt,
        bool IsPreparing,
        string? BaselineAssistantMessageId,
        string? Step = null,
        string? Activity = null,
        int Files = 0,
        int Additions = 0,
        int Deletions = 0);

    private readonly RemoteOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveSession> _activeSessions = new();
    private static readonly TimeSpan BusyPropagationGrace = TimeSpan.FromSeconds(5);

    public ProjectOptions? FindProject(string alias) =>
        _options.Projects.FirstOrDefault(project =>
            string.Equals(project.Id, alias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public async Task<RemoteState> InitializeProjectAsync(string fallbackAlias, CancellationToken cancellationToken)
    {
        var current = await stateStore.GetAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(current.ProjectDirectory) && Directory.Exists(current.ProjectDirectory))
        {
            runtime?.SetProject(current.ProjectAlias ?? current.ProjectId ?? "projeto", current.ProjectDirectory);
            runtime?.SetSelection(current.SessionId, current.Agent);
            return current;
        }

        return await ActivateProjectAsync(fallbackAlias, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectOptions>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        return (await client.ListProjectsAsync(cancellationToken))
            .Where(project => !string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Worktree))
            .Select(project => new ProjectOptions
            {
                Id = project.Id,
                Alias = GetProjectName(project.Worktree),
                Path = Path.GetFullPath(project.Worktree),
            })
            .OrderBy(project => project.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RemoteState> ActivateProjectAsync(string alias, CancellationToken cancellationToken)
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
            var sameProject = string.Equals(current.ProjectAlias, project.Alias, StringComparison.OrdinalIgnoreCase);
            var state = sameProject
                ? current with
                {
                    ProjectId = project.Id,
                    ProjectDirectory = Path.GetFullPath(project.Path),
                }
                : current with
                {
                    ProjectAlias = project.Alias,
                    ProjectId = project.Id,
                    ProjectDirectory = Path.GetFullPath(project.Path),
                    SessionId = null,
                    Agent = "build",
                };
            await stateStore.SaveAsync(state, cancellationToken);
            runtime?.SetProject(project.Alias, project.Path);
            runtime?.SetSelection(state.SessionId, state.Agent);
            var model = FindModelSelection(state)?.Model;
            runtime?.SetModel(model is null ? "automático" : $"{model.ProviderId}/{model.ModelId}");
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RemoteState> MoveProjectAsync(
        string projectId,
        string expectedDirectory,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var projects = await ListProjectsAsync(cancellationToken);
            var project = projects.FirstOrDefault(candidate =>
                candidate.Id == projectId && PathsEqual(candidate.Path, expectedDirectory));
            if (project is null)
            {
                throw new InvalidOperationException("O projeto selecionado não está mais disponível no OpenCode.");
            }
            if (!Directory.Exists(project.Path))
            {
                throw new InvalidOperationException("O diretório do projeto selecionado não existe.");
            }

            var current = await stateStore.GetAsync(cancellationToken);
            var currentProject = ResolveProject(current);
            if (currentProject is not null
                && current.SessionId is not null
                && await IsSessionBusyAsync(currentProject.Path, current.SessionId, cancellationToken))
            {
                throw new InvalidOperationException("A sessão ainda está ocupada. Aguarde ou use /stop.");
            }

            var state = current with
            {
                ProjectAlias = project.Alias,
                ProjectId = project.Id,
                ProjectDirectory = project.Path,
                SessionId = null,
                Agent = "build",
            };
            await stateStore.SaveAsync(state, cancellationToken);
            runtime?.SetProject(project.Alias, project.Path);
            runtime?.SetSelection(null, "build");
            runtime?.SetModel("automático");
            runtime?.SetTask(new CurrentTaskStatus(false));
            runtime?.SetAttention(null);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetChatIdAsync(long chatId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await stateStore.GetAsync(cancellationToken);
            if (state.ChatId != chatId)
            {
                await stateStore.SaveAsync(state with { ChatId = chatId }, cancellationToken);
            }
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
            runtime?.SetSelection(session.Id, "build");
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SelectSessionAsync(
        string expectedDirectory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (state, project) = await RequireProjectAsync(cancellationToken);
            if (!PathsEqual(project.Path, expectedDirectory))
            {
                throw new InvalidOperationException("O projeto selecionado mudou. Use /session novamente.");
            }
            var sessions = await client.ListSessionsAsync(project.Path, cancellationToken);
            if (sessions.All(session => session.Id != sessionId))
            {
                throw new InvalidOperationException("Sessão não pertence ao projeto selecionado.");
            }

            var agent = NormalizeAgent(await client.GetLatestUserAgentAsync(project.Path, sessionId, cancellationToken));
            await stateStore.SaveAsync(state with { SessionId = sessionId, Agent = agent }, cancellationToken);
            runtime?.SetSelection(sessionId, agent);
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

            var baseline = await client.GetLatestAssistantOutcomeAsync(project.Path, state.SessionId, cancellationToken);
            var active = new ActiveSession(Guid.NewGuid(), DateTimeOffset.UtcNow, true, baseline?.MessageId);
            _activeSessions[state.SessionId] = active;
            runtime?.SetTask(ToCurrentTaskStatus(active));
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
                runtime?.SetTask(new CurrentTaskStatus(false));
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void MarkIdle(string sessionId)
    {
        var wasActive = _activeSessions.TryRemove(sessionId, out _);
        if (wasActive || runtime?.Get().SessionId == sessionId)
        {
            runtime?.SetTask(new CurrentTaskStatus(false));
        }
    }

    internal void MarkIdle(string sessionId, Guid generation)
    {
        while (_activeSessions.TryGetValue(sessionId, out var active) && active.Generation == generation)
        {
            if (_activeSessions.TryRemove(new KeyValuePair<string, ActiveSession>(sessionId, active)))
            {
                runtime?.SetTask(new CurrentTaskStatus(false));
                return;
            }
        }
    }

    internal CurrentTaskStatus? UpdateTaskStep(string sessionId, string step)
        => UpdateRuntime(TryUpdateActiveSession(sessionId, active => active with { Step = step }));

    internal CurrentTaskStatus? UpdateTaskActivity(string sessionId, string activity)
        => UpdateRuntime(TryUpdateActiveSession(sessionId, active => active with { Activity = activity }));

    internal CurrentTaskStatus? UpdateTaskDiff(string sessionId, int files, int additions, int deletions)
        => UpdateRuntime(TryUpdateActiveSession(sessionId, active => active with
        {
            Files = files,
            Additions = additions,
            Deletions = deletions,
        }));

    internal bool IsLocallyActive(string sessionId) => _activeSessions.ContainsKey(sessionId);

    internal bool IsPreparingPrompt(string sessionId)
        => _activeSessions.TryGetValue(sessionId, out var active) && active.IsPreparing;

    internal bool IsWithinBusyGrace(string sessionId)
        => _activeSessions.TryGetValue(sessionId, out var active)
            && active.StartedAt is { } startedAt
            && DateTimeOffset.UtcNow - startedAt < BusyPropagationGrace;

    internal string? GetBaselineAssistantMessageId(string sessionId)
        => _activeSessions.TryGetValue(sessionId, out var active) ? active.BaselineAssistantMessageId : null;

    internal Guid? GetActiveGeneration(string sessionId)
        => _activeSessions.TryGetValue(sessionId, out var active) ? active.Generation : null;

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
            runtime?.SetSelection(state.SessionId, agent);
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
                throw new InvalidOperationException("A sessão selecionada mudou. Use /model novamente.");
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
                .Where(selection => selection.ProjectAlias != ProjectKey(state, project) || selection.SessionId != state.SessionId)
                .ToList();
            selections.Add(new SessionModelSelection(ProjectKey(state, project), state.SessionId, model));

            await stateStore.SaveAsync(state with { ModelSelections = selections }, cancellationToken);
            runtime?.SetModel(model is null ? "automático" : $"{model.ProviderId}/{model.ModelId}");
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
            var model = await ResolveCurrentModelAsync(state, cancellationToken);
            runtime?.SetModel(model.Model is null ? "automático" : $"{model.Model.ProviderId}/{model.Model.ModelId}");
            return (state, model);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CurrentModelInfo> ResolveCurrentModelAsync(RemoteState state, CancellationToken cancellationToken)
    {
        var project = ResolveProject(state);
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
        var project = ResolveProject(state);
        return project is not null
            && state.SessionId is not null
            && await IsSessionBusyAsync(project.Path, state.SessionId, cancellationToken);
    }

    public async Task<CurrentTaskStatus> GetCurrentTaskStatusAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var project = ResolveProject(state);
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
        var generation = GetActiveGeneration(sessionId);
        if (_activeSessions.TryGetValue(sessionId, out var active)
            && active.StartedAt is { } startedAt
            && DateTimeOffset.UtcNow - startedAt < BusyPropagationGrace)
        {
            return true;
        }

        var remoteBusy = await client.IsSessionBusyAsync(directory, sessionId, cancellationToken);
        if (!remoteBusy && generation is { } observedGeneration)
        {
            MarkIdle(sessionId, observedGeneration);
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
        if (_activeSessions.TryGetValue(sessionId, out var submitted))
        {
            runtime?.SetTask(ToCurrentTaskStatus(submitted));
        }
    }

    private CurrentTaskStatus? UpdateRuntime(CurrentTaskStatus? status)
    {
        if (status is not null)
        {
            runtime?.SetTask(status);
        }
        return status;
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
        var project = ResolveProject(state);
        return project is null
            ? throw new InvalidOperationException("O projeto desta instância não está disponível.")
            : (state, project);
    }

    public ProjectOptions? ResolveProject(RemoteState state)
    {
        var configured = state.ProjectId is not null
            ? FindProject(state.ProjectId)
            : state.ProjectAlias is not null ? FindProject(state.ProjectAlias) : null;
        if (configured is not null)
        {
            return configured;
        }
        if (string.IsNullOrWhiteSpace(state.ProjectDirectory))
        {
            return null;
        }

        return new ProjectOptions
        {
            Id = state.ProjectId,
            Alias = state.ProjectAlias ?? GetProjectName(state.ProjectDirectory),
            Path = state.ProjectDirectory,
        };
    }

    public async Task EnsureCurrentContextAsync(
        string expectedDirectory,
        string? expectedSessionId,
        CancellationToken cancellationToken)
    {
        var (state, project) = await RequireProjectAsync(cancellationToken);
        if (!PathsEqual(project.Path, expectedDirectory)
            || expectedSessionId is not null && state.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException("Esta ação pertence a outro projeto ou sessão.");
        }
    }

    private static string NormalizeAgent(string? agent)
        => string.Equals(agent, "plan", StringComparison.OrdinalIgnoreCase) ? "plan" : "build";

    private SessionModelSelection? FindModelSelection(RemoteState state)
    {
        var project = ResolveProject(state);
        var key = project is null ? state.ProjectId ?? state.ProjectAlias : ProjectKey(state, project);
        return state.ModelSelections?.FirstOrDefault(selection =>
            selection.ProjectAlias == key
            && selection.SessionId == state.SessionId);
    }

    private static string ProjectKey(RemoteState state, ProjectOptions project)
        => state.ProjectId ?? project.Id ?? project.Alias;

    private static string GetProjectName(string directory)
    {
        var name = new DirectoryInfo(Path.TrimEndingDirectorySeparator(directory)).Name;
        return string.IsNullOrWhiteSpace(name) ? directory : name;
    }

    public static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
