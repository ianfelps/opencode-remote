using System.Text.Json;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
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
    private readonly Dictionary<string, string> _lastResponses = [];
    private readonly HashSet<string> _reportedToolStates = [];
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

        switch (type)
        {
            case "session.idle":
                await HandleIdleAsync(directory, properties, state, cancellationToken);
                break;
            case "session.error":
                var failedSessionId = GetString(properties, "sessionID");
                if (failedSessionId != state.SessionId)
                {
                    break;
                }
                if (failedSessionId is not null
                    && await IsStaleTerminalEventAsync(directory, failedSessionId, cancellationToken))
                {
                    break;
                }
                if (failedSessionId is not null && failedSessionId == state.SessionId)
                {
                    runtime?.SetAttention(null);
                    runtime?.SetError("O OpenCode interrompeu a execução.");
                    ClearSessionProgress(failedSessionId);
                }
                if (failedSessionId == state.SessionId)
                {
                    await notifier.StopTypingAsync(state.ChatId);
                    await ClearProgressBestEffortAsync(state.ChatId, cancellationToken);
                    await notifier.SendTextAsync(
                        state.ChatId,
                        "## Erro na execução\n\nO OpenCode encontrou um erro e interrompeu o processamento.",
                        cancellationToken);
                }
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

        if (await IsStaleTerminalEventAsync(directory, sessionId, cancellationToken))
        {
            return;
        }
        runtime?.SetAttention(null);
        ClearSessionProgress(sessionId);

        await notifier.StopTypingAsync(state.ChatId);
        await ClearProgressBestEffortAsync(state.ChatId, cancellationToken);
        var text = await client.GetLatestAssistantTextAsync(directory, sessionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(text)
            || (_lastResponses.TryGetValue(sessionId, out var previous) && previous == text))
        {
            return;
        }

        await notifier.SendTextAsync(state.ChatId, text, cancellationToken);
        _lastResponses[sessionId] = text;
        if (string.Equals(state.Agent, "plan", StringComparison.OrdinalIgnoreCase))
        {
            await notifier.SendPlanReadyAsync(state.ChatId, directory, sessionId, cancellationToken);
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

    private void ClearSessionProgress(string sessionId)
    {
        coordinator.MarkIdle(sessionId);
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
