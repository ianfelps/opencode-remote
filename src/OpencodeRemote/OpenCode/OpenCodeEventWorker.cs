using System.Text.Json;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode.Models;
using OpencodeRemote.Persistence;
using OpencodeRemote.Runtime;
using OpencodeRemote.Sessions;
using OpencodeRemote.Sessions.Models;
using OpencodeRemote.Telegram;
using OpencodeRemote.Telegram.Models;

namespace OpencodeRemote.OpenCode;

public sealed class OpenCodeEventWorker(
    OpenCodeClient client,
    StateStore stateStore,
    SessionCoordinator coordinator,
    IRemoteNotifier notifier,
    IOptions<RemoteOptions> options,
    ILogger<OpenCodeEventWorker> logger,
    RuntimeStatusStore? runtime = null,
    ApplicationExitState? exitState = null) : BackgroundService
{
    private sealed record DeliveredOutcome(string? MessageId, string Signature);
    private sealed record PendingPlanReady(string Signature, Guid? Generation);

    private readonly Dictionary<string, DeliveredOutcome> _deliveredOutcomes = [];
    private readonly Dictionary<string, PendingPlanReady> _pendingPlanReady = [];
    private readonly HashSet<string> _reportedToolStates = [];
    private readonly SemaphoreSlim _terminalGate = new(1, 1);
    private readonly TelegramOptions _telegram = options.Value.Telegram;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            exitState?.Fail();
            throw;
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_telegram.Token) || _telegram.AllowedUserId == 0)
        {
            return;
        }

        await Task.WhenAll(
            RunEventStreamAsync(stoppingToken),
            RunReconciliationLoopAsync(stoppingToken));
    }

    private async Task RunEventStreamAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !await client.IsHealthyAsync(stoppingToken))
        {
            runtime?.SetEvents("aguardando OpenCode");
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                runtime?.SetEvents("conectando");
                await foreach (var document in client.SubscribeEventsAsync(stoppingToken))
                {
                    runtime?.SetEvents("conectado");
                    using (document)
                    {
                        using var eventTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        eventTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                        try
                        {
                            await HandleEventAsync(document.RootElement, eventTimeout.Token);
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogWarning("Tempo limite ao processar evento do OpenCode; continuando o stream");
                        }
                        catch (Exception exception)
                        {
                            runtime?.SetError(exception.Message);
                            logger.LogWarning(exception, "Evento do OpenCode ignorado após falha de processamento");
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                runtime?.SetEvents("reconectando");
                runtime?.SetError(exception.Message);
                logger.LogWarning(exception, "Stream de eventos do OpenCode desconectado; tentando novamente");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    internal async Task HandleEventAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("directory", out var directoryElement)
            || !root.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("type", out var typeElement)
            || !payload.TryGetProperty("properties", out var properties))
        {
            return;
        }

        var directory = directoryElement.GetString() ?? "";
        var type = typeElement.GetString();
        runtime?.Touch();
        var state = await stateStore.GetAsync(cancellationToken);
        if (state.ChatId == 0)
        {
            return;
        }
        if (state.ProjectDirectory is not null
            && !SessionCoordinator.PathsEqual(state.ProjectDirectory, directory))
        {
            return;
        }

        switch (type)
        {
            case "session.idle":
                await HandleIdleAsync(directory, properties, state, cancellationToken);
                break;
            case "session.error":
                await HandleSessionErrorAsync(directory, properties, state, cancellationToken);
                break;
            case "permission.updated":
            case "permission.asked":
                await HandlePermissionAsync(directory, properties, state, false, cancellationToken);
                break;
            case "permission.v2.asked":
                await HandlePermissionAsync(directory, properties, state, true, cancellationToken);
                break;
            case "question.asked":
                await HandleQuestionAsync(directory, properties, state, false, cancellationToken);
                break;
            case "question.v2.asked":
                await HandleQuestionAsync(directory, properties, state, true, cancellationToken);
                break;
            case "message.part.updated":
                await HandlePartUpdatedAsync(properties, state, cancellationToken);
                break;
            case "message.updated":
                await HandleMessageUpdatedAsync(directory, properties, state, cancellationToken);
                break;
            case "todo.updated":
                await HandleTodoUpdatedAsync(properties, state, cancellationToken);
                break;
            case "session.diff":
                await HandleDiffUpdatedAsync(properties, state, cancellationToken);
                break;
        }
    }

    private async Task HandleIdleAsync(string directory, JsonElement properties, RemoteState state, CancellationToken cancellationToken)
    {
        var sessionId = GetString(properties, "sessionID");
        if (sessionId is null || sessionId != state.SessionId)
        {
            return;
        }
        var generation = coordinator.GetActiveGeneration(sessionId);

        if (await IsStaleTerminalEventAsync(directory, sessionId, cancellationToken))
        {
            return;
        }

        var outcome = await client.GetLatestAssistantOutcomeAsync(directory, sessionId, cancellationToken);
        if (outcome is null)
        {
            return;
        }
        if (IsBaselineOutcome(sessionId, outcome))
        {
            return;
        }

        await DeliverOutcomeAsync(directory, state, sessionId, outcome, generation, cancellationToken);
    }

    private async Task HandleSessionErrorAsync(
        string directory,
        JsonElement properties,
        RemoteState state,
        CancellationToken cancellationToken)
    {
        var sessionId = GetString(properties, "sessionID");
        if (sessionId is null || sessionId != state.SessionId)
        {
            return;
        }
        var generation = coordinator.GetActiveGeneration(sessionId);

        var payloadError = properties.TryGetProperty("error", out var error)
            ? OpenCodeClient.GetErrorMessage(error)
            : null;
        if (await IsStaleTerminalEventAsync(directory, sessionId, cancellationToken))
        {
            return;
        }

        AssistantOutcome? outcome = null;
        try
        {
            outcome = await client.GetLatestAssistantOutcomeAsync(directory, sessionId, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogDebug(exception, "Não foi possível consultar a mensagem associada ao erro da sessão");
        }

        if (outcome is not null && IsBaselineOutcome(sessionId, outcome))
        {
            outcome = null;
        }

        if (outcome?.IsError != true)
        {
            outcome = new AssistantOutcome(null, outcome?.Text ?? "", payloadError
                ?? "O OpenCode encontrou um erro e interrompeu o processamento.");
        }
        await DeliverOutcomeAsync(directory, state, sessionId, outcome, generation, cancellationToken);
    }

    private async Task HandleMessageUpdatedAsync(
        string directory,
        JsonElement properties,
        RemoteState state,
        CancellationToken cancellationToken)
    {
        if (!properties.TryGetProperty("info", out var info)
            || GetString(info, "role") != "assistant"
            || GetString(info, "sessionID") is not { } sessionId
            || sessionId != state.SessionId)
        {
            return;
        }

        var outcome = OpenCodeClient.ParseAssistantOutcome(info);
        if (!outcome.IsError)
        {
            return;
        }
        var generation = coordinator.GetActiveGeneration(sessionId);
        if (IsBaselineOutcome(sessionId, outcome))
        {
            return;
        }
        if (await IsStaleTerminalEventAsync(directory, sessionId, cancellationToken))
        {
            return;
        }
        await DeliverOutcomeAsync(directory, state, sessionId, outcome, generation, cancellationToken);
    }

    private async Task DeliverOutcomeAsync(
        string directory,
        RemoteState state,
        string sessionId,
        AssistantOutcome outcome,
        Guid? generation,
        CancellationToken cancellationToken)
    {
        var text = outcome.IsError
            ? $"## Erro na execução\n\n{outcome.ErrorMessage}"
            : outcome.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var signature = outcome.IsError ? $"error:{outcome.ErrorMessage}" : $"text:{outcome.Text}";
        await _terminalGate.WaitAsync(cancellationToken);
        try
        {
            if (coordinator.GetActiveGeneration(sessionId) != generation)
            {
                return;
            }
            if (_pendingPlanReady.TryGetValue(sessionId, out var stalePlan)
                && (stalePlan.Generation != generation || stalePlan.Signature != signature))
            {
                _pendingPlanReady.Remove(sessionId);
            }
            if (_deliveredOutcomes.TryGetValue(sessionId, out var previous)
                && (previous.MessageId is not null && previous.MessageId == outcome.MessageId
                    || !coordinator.IsLocallyActive(sessionId) && previous.Signature == signature))
            {
                if (_pendingPlanReady.TryGetValue(sessionId, out var pendingPlan)
                    && pendingPlan.Generation == generation && pendingPlan.Signature == signature)
                {
                    await notifier.SendPlanReadyAsync(state.ChatId, directory, sessionId, cancellationToken);
                    _pendingPlanReady.Remove(sessionId);
                    ClearSessionProgress(sessionId, generation);
                }
                return;
            }

            await StopTypingBestEffortAsync(state.ChatId);
            await ClearProgressBestEffortAsync(state.ChatId, cancellationToken);
            await notifier.SendTextAsync(state.ChatId, text, cancellationToken);

            _deliveredOutcomes[sessionId] = new DeliveredOutcome(outcome.MessageId, signature);
            runtime?.SetAttention(null);
            if (outcome.IsError)
            {
                runtime?.SetError(outcome.ErrorMessage!);
            }
            if (!outcome.IsError && string.Equals(state.Agent, "plan", StringComparison.OrdinalIgnoreCase))
            {
                _pendingPlanReady[sessionId] = new PendingPlanReady(signature, generation);
                await notifier.SendPlanReadyAsync(state.ChatId, directory, sessionId, cancellationToken);
                _pendingPlanReady.Remove(sessionId);
            }
            ClearSessionProgress(sessionId, generation);
        }
        finally
        {
            _terminalGate.Release();
        }
    }

    private async Task RunReconciliationLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcilePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                runtime?.SetError(exception.Message);
                logger.LogWarning(exception, "Falha ao reconciliar resposta pendente do OpenCode");
            }
        }
    }

    internal async Task ReconcilePendingAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        if (state.ChatId == 0 || state.SessionId is not { } sessionId || !coordinator.IsLocallyActive(sessionId)
            || coordinator.IsPreparingPrompt(sessionId) || coordinator.IsWithinBusyGrace(sessionId))
        {
            return;
        }
        var generation = coordinator.GetActiveGeneration(sessionId);

        var project = coordinator.ResolveProject(state);
        if (project is null || await client.IsSessionBusyAsync(project.Path, sessionId, cancellationToken))
        {
            return;
        }

        var outcome = await client.GetLatestAssistantOutcomeAsync(project.Path, sessionId, cancellationToken);
        if (outcome is not null
            && (outcome.MessageId is null || outcome.MessageId != coordinator.GetBaselineAssistantMessageId(sessionId)))
        {
            await DeliverOutcomeAsync(project.Path, state, sessionId, outcome, generation, cancellationToken);
        }
    }

    private bool IsBaselineOutcome(string sessionId, AssistantOutcome outcome)
        => coordinator.IsLocallyActive(sessionId)
            && outcome.MessageId is { } messageId
            && messageId == coordinator.GetBaselineAssistantMessageId(sessionId);

    private async Task StopTypingBestEffortAsync(long chatId)
    {
        try
        {
            await notifier.StopTypingAsync(chatId);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Falha ao interromper indicador de digitação");
        }
    }

    private async Task HandlePermissionAsync(string directory, JsonElement properties, RemoteState state, bool useV2, CancellationToken cancellationToken)
    {
        var sessionId = GetString(properties, "sessionID");
        var permissionId = GetString(properties, "id");
        if (sessionId != state.SessionId || permissionId is null)
        {
            return;
        }

        runtime?.SetAttention("aguardando permissão no Telegram");
        var title = GetString(properties, "title")
            ?? GetString(properties, "permission")
            ?? GetString(properties, "action")
            ?? "Ação não identificada";
        await notifier.StopTypingAsync(state.ChatId);
        await ClearProgressBestEffortAsync(state.ChatId, cancellationToken);
        await notifier.SendPermissionAsync(state.ChatId, directory, sessionId!, permissionId, title, useV2, cancellationToken);
    }

    private async Task HandleQuestionAsync(string directory, JsonElement properties, RemoteState state, bool useV2, CancellationToken cancellationToken)
    {
        var sessionId = GetString(properties, "sessionID");
        var requestId = GetString(properties, "id");
        if (sessionId != state.SessionId || requestId is null || !properties.TryGetProperty("questions", out var questionsElement))
        {
            return;
        }

        runtime?.SetAttention("aguardando resposta no Telegram");
        var questions = new List<QuestionPrompt>();
        foreach (var question in questionsElement.EnumerateArray())
        {
            var options = question.TryGetProperty("options", out var optionsElement)
                ? optionsElement.EnumerateArray().Select(option => new QuestionOption(
                    GetString(option, "label") ?? "Opção",
                    GetString(option, "description") ?? "")).ToArray()
                : [];
            questions.Add(new QuestionPrompt(
                GetString(question, "question") ?? "Pergunta",
                GetString(question, "header") ?? "Pergunta",
                question.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean(),
                options));
        }

        await notifier.StopTypingAsync(state.ChatId);
        await ClearProgressBestEffortAsync(state.ChatId, cancellationToken);
        await notifier.SendQuestionAsync(state.ChatId, directory, new PendingQuestion(requestId, sessionId!, questions), useV2, cancellationToken);
    }

    private async Task HandlePartUpdatedAsync(JsonElement properties, RemoteState state, CancellationToken cancellationToken)
    {
        if (!properties.TryGetProperty("part", out var part))
        {
            return;
        }

        var sessionId = GetString(properties, "sessionID") ?? GetString(part, "sessionID");
        if (sessionId != state.SessionId || GetString(part, "type") != "tool" || !part.TryGetProperty("state", out var toolState))
        {
            return;
        }

        var status = GetString(toolState, "status");
        if (status is not ("running" or "error"))
        {
            return;
        }

        var partId = GetString(part, "id") ?? GetString(part, "callID") ?? "unknown";
        if (!_reportedToolStates.Add($"{sessionId}:{partId}:{status}"))
        {
            return;
        }

        var tool = GetString(part, "tool") ?? "tool";
        var activity = ToolProgressFormatter.Format(tool, status);
        if (status == "error")
        {
            await notifier.SendTextAsync(state.ChatId, $"## Falha na operação\n\n{activity}", cancellationToken);
            return;
        }

        var progress = coordinator.UpdateTaskActivity(sessionId!, activity);
        if (progress is not null)
        {
            await UpdateProgressAsync(state.ChatId, progress, cancellationToken);
        }
    }

    private async Task HandleTodoUpdatedAsync(JsonElement properties, RemoteState state, CancellationToken cancellationToken)
    {
        var sessionId = GetString(properties, "sessionID");
        if (sessionId != state.SessionId || !properties.TryGetProperty("todos", out var todos))
        {
            return;
        }

        var current = todos.EnumerateArray().FirstOrDefault(todo => GetString(todo, "status") == "in_progress");
        if (current.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        var content = GetString(current, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var progress = coordinator.UpdateTaskStep(sessionId!, content);
        if (progress is not null)
        {
            await UpdateProgressAsync(state.ChatId, progress, cancellationToken);
        }
    }

    private async Task HandleDiffUpdatedAsync(JsonElement properties, RemoteState state, CancellationToken cancellationToken)
    {
        var sessionId = GetString(properties, "sessionID");
        if (sessionId != state.SessionId
            || !properties.TryGetProperty("diff", out var diff)
            || diff.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var files = diff.GetArrayLength();
        var additions = 0;
        var deletions = 0;
        foreach (var file in diff.EnumerateArray())
        {
            var fileAdditions = GetInt32(file, "additions");
            var fileDeletions = GetInt32(file, "deletions");
            additions += fileAdditions;
            deletions += fileDeletions;
        }

        var progress = coordinator.UpdateTaskDiff(sessionId!, files, additions, deletions);
        if (progress is not null)
        {
            await UpdateProgressAsync(state.ChatId, progress, cancellationToken);
        }
    }

    private void ClearSessionProgress(string sessionId, Guid? generation)
    {
        if (generation is { } activeGeneration)
        {
            coordinator.MarkIdle(sessionId, activeGeneration);
        }
        _reportedToolStates.RemoveWhere(key => key.StartsWith(sessionId + ':', StringComparison.Ordinal));
    }

    private async Task ClearProgressBestEffortAsync(long chatId, CancellationToken cancellationToken)
    {
        using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await notifier.ClearProgressAsync(chatId, cleanupTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Tempo limite ao remover mensagem de progresso; enviando a notificação terminal");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Falha ao remover mensagem de progresso; enviando a notificação terminal");
        }
    }

    private async Task<bool> IsStaleTerminalEventAsync(
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!coordinator.IsLocallyActive(sessionId))
        {
            return false;
        }

        if (coordinator.IsPreparingPrompt(sessionId))
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (coordinator.IsPreparingPrompt(sessionId) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            if (coordinator.IsPreparingPrompt(sessionId))
            {
                return true;
            }
            if (!coordinator.IsLocallyActive(sessionId))
            {
                return false;
            }
        }

        try
        {
            if (await client.IsSessionBusyAsync(directory, sessionId, cancellationToken))
            {
                return true;
            }
            if (!coordinator.IsWithinBusyGrace(sessionId))
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return await client.IsSessionBusyAsync(directory, sessionId, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogDebug(exception, "Não foi possível confirmar se o evento terminal está atrasado");
            return false;
        }
    }

    private Task UpdateProgressAsync(long chatId, CurrentTaskStatus progress, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(progress.Step))
        {
            details.Add($"**Etapa:** {progress.Step}");
        }
        if (!string.IsNullOrWhiteSpace(progress.Activity))
        {
            details.Add($"**Atividade:** {progress.Activity}");
        }
        if (progress.Files > 0)
        {
            details.Add($"**Alterações:** {progress.Files} arquivo(s), +{progress.Additions}/-{progress.Deletions}");
        }

        var suffix = details.Count == 0 ? "O OpenCode está trabalhando na tarefa." : string.Join("\n", details);
        return notifier.UpdateProgressAsync(chatId, $"## Processando solicitação\n\n{suffix}", cancellationToken);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
}
