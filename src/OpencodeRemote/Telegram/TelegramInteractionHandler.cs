using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode;
using OpencodeRemote.OpenCode.Models;
using OpencodeRemote.Persistence;
using OpencodeRemote.Sessions;
using OpencodeRemote.Sessions.Models;
using OpencodeRemote.Telegram.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace OpencodeRemote.Telegram;

public sealed class TelegramInteractionHandler(
    IOptions<RemoteOptions> options,
    SessionCoordinator coordinator,
    OpenCodeClient openCode,
    StateStore stateStore,
    TelegramDelivery delivery,
    TelegramQuestionFlow questions,
    ILogger<TelegramWorker> logger)
{
    internal const string HelpText = """
        ## OpenCode Remote
        Selecione um projeto e uma sessão antes de enviar mensagens.

        **Projeto e sessão**
        - `/projects` - seleciona um projeto autorizado
        - `/session` - seleciona uma sessão existente e limpa o chat
        - `/sessions` - alias de `/session`
        - `/new` - cria uma sessão, ativa Build e limpa o chat

        **Modos e prompts**
        - `/plan [mensagem]` - ativa Plan e, se informada, envia a mensagem
        - `/build [mensagem]` - ativa Build e, se informada, envia a mensagem
        - `/mode` - mostra o modo atual
        - `/model` - seleciona o provider e o modelo da sessão

        **Estado e controle**
        - `/status` - mostra projeto, sessão, modo e modelo atuais
        - `/task` - mostra o progresso da tarefa atual
        - `/stop` - interrompe a execução atual
        - `/clear` - limpa as mensagens da sessão atual no Telegram

        **Ajuda**
        - `/help` - mostra esta ajuda
        - `/start` - alias de `/help`

        Depois de selecionar projeto e sessão, envie uma mensagem comum para o OpenCode usando o modo atual.
        """;

    private readonly TelegramOptions _settings = options.Value.Telegram;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _messageLocks = new();

    internal async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callback)
        {
            if (callback.Message is not { } callbackMessage)
            {
                await HandleCallbackAsync(callback, cancellationToken);
                return;
            }

            var callbackGate = _messageLocks.GetOrAdd(callbackMessage.Chat.Id, _ => new SemaphoreSlim(1, 1));
            await callbackGate.WaitAsync(cancellationToken);
            try
            {
                await HandleCallbackAsync(callback, cancellationToken);
            }
            finally
            {
                callbackGate.Release();
            }
            return;
        }

        if (update.Message is not { Text: { } text } message || message.From?.Id != _settings.AllowedUserId)
        {
            return;
        }

        if (string.Equals(text.Trim(), "/stop", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMessageSafelyAsync(message.Chat.Id, message.Id, text.Trim(), cancellationToken);
            return;
        }

        var gate = _messageLocks.GetOrAdd(message.Chat.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await questions.TryHandleFreeTextAsync(message.Chat.Id, text, cancellationToken))
            {
                return;
            }

            await HandleMessageSafelyAsync(message.Chat.Id, message.Id, text.Trim(), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task HandleMessageSafelyAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleMessageAsync(chatId, messageId, text, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Falha ao processar mensagem do Telegram");
            await delivery.SendTextAsync(chatId, $"## Não foi possível concluir\n\n{exception.Message}", cancellationToken);
        }
        catch (ApiRequestException exception)
        {
            logger.LogWarning(exception, "Telegram indisponível após processar mensagem");
        }
    }

    public async Task SendPermissionAsync(
        long chatId,
        string directory,
        string sessionId,
        string permissionId,
        string title,
        bool useV2,
        CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        var callbackGroup = delivery.CreateCallbackGroup();
        var keyboard = new InlineKeyboardMarkup([
            [delivery.Button("Permitir uma vez", new CallbackAction(useV2 ? "permission-v2" : "permission", directory, sessionId, permissionId, "once"), callbackGroup)],
            [delivery.Button("Sempre nesta sessão", new CallbackAction(useV2 ? "permission-v2" : "permission", directory, sessionId, permissionId, "always"), callbackGroup)],
            [delivery.Button("Rejeitar", new CallbackAction(useV2 ? "permission-v2" : "permission", directory, sessionId, permissionId, "reject"), callbackGroup)],
        ]);
        await delivery.SendKeyboardAsync(
            chatId,
            $"## Autorização solicitada\n\n**Ação:** {title}\n\nEscolha como o OpenCode deve prosseguir:",
            keyboard,
            cancellationToken);
    }

    public async Task SendPlanReadyAsync(
        long chatId,
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        var keyboard = new InlineKeyboardMarkup([
            [delivery.Button("Implementar este plano", new CallbackAction("implement-plan", directory, sessionId))],
        ]);
        await delivery.SendKeyboardAsync(
            chatId,
            "## Plano concluído\n\nO plano está pronto. Deseja iniciar a implementação nesta sessão?",
            keyboard,
            cancellationToken);
    }

    private async Task HandleMessageAsync(long chatId, int messageId, string text, CancellationToken cancellationToken)
    {
        var segments = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = segments[0].ToLowerInvariant();
        var arguments = segments.Length > 1 ? segments[1] : "";
        switch (command)
        {
            case "/start":
            case "/help":
                await delivery.SendTextAsync(chatId, HelpText, cancellationToken);
                break;
            case "/projects":
                await SendProjectsAsync(chatId, cancellationToken);
                break;
            case "/session":
            case "/sessions":
                await EnsureSessionIsIdleAsync(cancellationToken);
                await SendSessionsAsync(chatId, cancellationToken);
                await ClearChatAsync(chatId, messageId, cancellationToken);
                break;
            case "/new":
                await EnsureSessionIsIdleAsync(cancellationToken);
                var restoreQuestion = questions.SuspendPending(chatId);
                OpenCodeSession session;
                try
                {
                    session = await coordinator.CreateSessionAsync(cancellationToken);
                }
                catch
                {
                    restoreQuestion?.Invoke();
                    throw;
                }
                await delivery.SendTextAsync(
                    chatId,
                    $"## Nova sessão\n\n**Título:** {session.Title}\n**Criada em:** `{SessionTimeFormatter.Format(session.Time.Created)} ({SessionTimeFormatter.GetUtcOffsetLabel(session.Time.Created)})`\n**ID:** `{session.Id}`\n**Modo:** Build\n**Modelo:** automático do OpenCode",
                    cancellationToken);
                await ClearChatAsync(chatId, messageId, cancellationToken);
                break;
            case "/plan":
                await ChangeAgentAsync(chatId, "plan", arguments, cancellationToken);
                break;
            case "/build":
                await ChangeAgentAsync(chatId, "build", arguments, cancellationToken);
                break;
            case "/mode":
                var modeState = await stateStore.GetAsync(cancellationToken);
                await delivery.SendTextAsync(chatId, $"## Modo atual\n\n**Agente:** {AgentLabel(modeState.Agent)}", cancellationToken);
                break;
            case "/model":
                await EnsureSessionIsIdleAsync(cancellationToken);
                await SendModelProvidersAsync(chatId, cancellationToken);
                break;
            case "/status":
                var status = await coordinator.GetStatusAsync(cancellationToken);
                await delivery.SendTextAsync(chatId, FormatStatus(status.State, status.Model), cancellationToken);
                break;
            case "/task":
                var taskStatus = await coordinator.GetCurrentTaskStatusAsync(cancellationToken);
                await delivery.SendTextAsync(chatId, FormatTaskStatus(taskStatus), cancellationToken);
                break;
            case "/stop":
                questions.CancelPending(chatId);
                await coordinator.AbortAsync(cancellationToken);
                await delivery.StopTypingAsync(chatId);
                await delivery.ClearProgressAsync(chatId, cancellationToken);
                await delivery.SendTextAsync(
                    chatId,
                    "## Execução interrompida\n\nA solicitação de cancelamento foi enviada ao OpenCode.",
                    cancellationToken);
                break;
            case "/clear":
                await EnsureSessionIsIdleAsync(cancellationToken);
                await delivery.SendTextAsync(
                    chatId,
                    "## Chat limpo\n\nAs mensagens removíveis desta sessão foram apagadas. A sessão do OpenCode foi preservada.",
                    cancellationToken);
                await ClearChatAsync(chatId, messageId, cancellationToken);
                break;
            default:
                await SendPromptWithProgressAsync(
                    chatId,
                    "## Processando solicitação\n\nO OpenCode está trabalhando na tarefa.",
                    text,
                    cancellationToken);
                break;
        }
    }

    private async Task SendProjectsAsync(long chatId, CancellationToken cancellationToken)
    {
        if (delivery.Bot is null || coordinator.Projects.Count == 0)
        {
            await delivery.SendTextAsync(
                chatId,
                "## Projetos indisponíveis\n\nNenhum projeto autorizado foi configurado.",
                cancellationToken);
            return;
        }

        var callbackGroup = delivery.CreateCallbackGroup();
        var keyboard = new InlineKeyboardMarkup(coordinator.Projects.Select(project => new[]
        {
            delivery.Button(project.Alias, new CallbackAction("project", project.Path, Value: project.Alias), callbackGroup),
        }));
        await delivery.SendKeyboardAsync(
            chatId,
            "## Selecionar projeto\n\nEscolha um dos projetos autorizados:",
            keyboard,
            cancellationToken);
    }

    private async Task SendSessionsAsync(long chatId, CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        var (_, project) = await coordinator.RequireProjectAsync(cancellationToken);
        var sessions = await coordinator.ListSessionsAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            await delivery.SendTextAsync(
                chatId,
                "## Nenhuma sessão encontrada\n\nUse `/new` para criar a primeira sessão deste projeto.",
                cancellationToken);
            return;
        }

        var callbackGroup = delivery.CreateCallbackGroup();
        var keyboard = new InlineKeyboardMarkup(sessions.Select(session => new[]
        {
            delivery.Button(
                TelegramDelivery.TrimButtonText($"{SessionTimeFormatter.Format(session.Time.Updated)} | {session.Title}"),
                new CallbackAction("session", project.Path, session.Id, Value: session.Title),
                callbackGroup),
        }));
        await delivery.SendKeyboardAsync(
            chatId,
            "## Selecionar sessão\n\nEscolha uma das sessões mais recentes:",
            keyboard,
            cancellationToken);
    }

    private async Task SendModelProvidersAsync(long chatId, CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        var (state, project) = await coordinator.RequireProjectAsync(cancellationToken);
        if (state.SessionId is null)
        {
            throw new InvalidOperationException("Selecione ou crie uma sessão primeiro.");
        }

        var providers = await coordinator.ListProvidersAsync(cancellationToken);
        if (providers.Count == 0)
        {
            await delivery.SendTextAsync(
                chatId,
                "## Modelos indisponíveis\n\nO OpenCode não retornou providers com modelos disponíveis.",
                cancellationToken);
            return;
        }

        var callbackGroup = delivery.CreateCallbackGroup();
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { delivery.Button("Automático (OpenCode)", new CallbackAction("model-auto", project.Path, state.SessionId), callbackGroup) },
        };
        buttons.AddRange(providers.Select(provider => new[]
        {
            delivery.Button(provider.Name, new CallbackAction(
                "model-provider",
                project.Path,
                state.SessionId,
                ProviderId: provider.Id), callbackGroup),
        }));

        await delivery.SendKeyboardAsync(
            chatId,
            "## Selecionar modelo\n\nEscolha o provider ou retorne ao modo automático do OpenCode:",
            new InlineKeyboardMarkup(buttons),
            cancellationToken);
    }

    private async Task SendModelsAsync(
        long chatId,
        string expectedDirectory,
        string expectedSessionId,
        string providerId,
        CancellationToken cancellationToken)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var (_, project) = await coordinator.RequireProjectAsync(cancellationToken);
        if (state.SessionId != expectedSessionId
            || !string.Equals(
                Path.GetFullPath(project.Path),
                Path.GetFullPath(expectedDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O projeto ou a sessão selecionada mudou. Use /model novamente.");
        }

        var providers = await coordinator.ListProvidersAsync(cancellationToken);
        var provider = providers.FirstOrDefault(candidate => candidate.Id == providerId)
            ?? throw new InvalidOperationException("O provider selecionado não está mais disponível.");
        var callbackGroup = delivery.CreateCallbackGroup();
        var keyboard = new InlineKeyboardMarkup(provider.Models.Select(model => new[]
        {
            delivery.Button(model.Name, new CallbackAction(
                "model-select",
                project.Path,
                expectedSessionId,
                ProviderId: provider.Id,
                ModelId: model.Id), callbackGroup),
        }));

        await delivery.SendKeyboardAsync(
            chatId,
            $"## Modelos de {provider.Name}\n\nEscolha o modelo que será usado nos próximos prompts desta sessão:",
            keyboard,
            cancellationToken);
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        if (delivery.Bot is null || callback.From.Id != _settings.AllowedUserId || callback.Data is null)
        {
            return;
        }

        if (callback.Message is not { } callbackMessage)
        {
            await delivery.Bot.AnswerCallbackQuery(
                callback.Id,
                "Esta ação não está associada a uma mensagem válida.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        if (!delivery.TryReserveCallback(callback.Data, out var reservation) || reservation is null)
        {
            await delivery.Bot.AnswerCallbackQuery(
                callback.Id,
                "Ação expirada.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        var action = reservation.Action;
        var committed = false;
        try
        {
            string? confirmation = null;
            switch (action.Kind)
            {
                case "project":
                    var restoreProjectQuestion = questions.SuspendPending(callbackMessage.Chat.Id);
                    try
                    {
                        await coordinator.SelectProjectAsync(callbackMessage.Chat.Id, action.Value!, cancellationToken);
                    }
                    catch
                    {
                        restoreProjectQuestion?.Invoke();
                        throw;
                    }
                    committed = true;
                    confirmation = $"## Projeto selecionado\n\n**Projeto:** `{action.Value}`\n\nAgora selecione ou crie uma sessão.";
                    break;
                case "session":
                    var restoreSessionQuestion = questions.SuspendPending(callbackMessage.Chat.Id);
                    string selectedAgent;
                    try
                    {
                        selectedAgent = await coordinator.SelectSessionAsync(action.SessionId!, cancellationToken);
                    }
                    catch
                    {
                        restoreSessionQuestion?.Invoke();
                        throw;
                    }
                    committed = true;
                    var history = await openCode.GetRecentConversationAsync(
                        action.Directory,
                        action.SessionId!,
                        8,
                        cancellationToken);
                    confirmation = SessionHistoryFormatter.Format(action.Value ?? action.SessionId!, history)
                        + $"\n\n**Modo recuperado:** {AgentLabel(selectedAgent)}";
                    break;
                case "model-provider":
                    await SendModelsAsync(
                        callbackMessage.Chat.Id,
                        action.Directory,
                        action.SessionId!,
                        action.ProviderId!,
                        cancellationToken);
                    break;
                case "model-select":
                    var selectedModel = new OpenCodeModelRef(action.ProviderId!, action.ModelId!);
                    await coordinator.SetModelAsync(action.Directory, action.SessionId!, selectedModel, cancellationToken);
                    committed = true;
                    confirmation = FormatModelChanged(selectedModel);
                    break;
                case "model-auto":
                    await coordinator.SetModelAsync(action.Directory, action.SessionId!, null, cancellationToken);
                    committed = true;
                    confirmation = "## Modelo automático\n\nO OpenCode voltará a escolher o modelo para os próximos prompts desta sessão.";
                    break;
                case "implement-plan":
                    var currentState = await stateStore.GetAsync(cancellationToken);
                    if (currentState.SessionId != action.SessionId)
                    {
                        throw new InvalidOperationException("Esse plano pertence a outra sessão.");
                    }
                    await coordinator.SetAgentAsync("build", cancellationToken);
                    await SendPromptWithProgressAsync(
                        callbackMessage.Chat.Id,
                        "## Implementando plano\n\nO OpenCode está trabalhando na tarefa.",
                        "Implemente o plano definido e aprovado anteriormente.",
                        cancellationToken);
                    committed = true;
                    break;
                case "permission":
                case "permission-v2":
                    await RunWithProgressAsync(
                        callbackMessage.Chat.Id,
                        "## Processando autorização\n\nO OpenCode está retomando a tarefa.",
                        token => openCode.ReplyPermissionAsync(
                            action.Directory,
                            action.SessionId!,
                            action.RequestId!,
                            action.Value!,
                            action.Kind.EndsWith("-v2"),
                            token),
                        cancellationToken);
                    committed = true;
                    confirmation = action.Value switch
                    {
                        "once" => "## Autorização respondida\n\n**Decisão:** Permitir uma vez",
                        "always" => "## Autorização respondida\n\n**Decisão:** Sempre nesta sessão",
                        "reject" => "## Autorização respondida\n\n**Decisão:** Rejeitar",
                        _ => $"## Autorização respondida\n\n**Decisão:** {action.Value}",
                    };
                    break;
                case "question":
                case "question-v2":
                    await questions.HandleSelectionAsync(callbackMessage.Chat.Id, action, cancellationToken);
                    committed = true;
                    break;
            }

            committed = true;
            await delivery.Bot.AnswerCallbackQuery(callback.Id, "Confirmado.", cancellationToken: cancellationToken);
            await delivery.Bot.EditMessageReplyMarkup(
                callbackMessage.Chat.Id,
                callbackMessage.Id,
                cancellationToken: cancellationToken);
            if (confirmation is not null)
            {
                await delivery.SendTextAsync(callbackMessage.Chat.Id, confirmation, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or ApiRequestException)
        {
            if (!committed
                && (action.Kind is not ("question" or "question-v2")
                    || questions.IsActive(callbackMessage.Chat.Id, action)))
            {
                delivery.RestoreCallback(reservation);
            }
            logger.LogWarning(exception, "Falha ao executar callback");
            await delivery.Bot.AnswerCallbackQuery(
                callback.Id,
                exception.Message,
                showAlert: true,
                cancellationToken: cancellationToken);
        }
        catch
        {
            if (!committed
                && (action.Kind is not ("question" or "question-v2")
                    || questions.IsActive(callbackMessage.Chat.Id, action)))
            {
                delivery.RestoreCallback(reservation);
            }
            throw;
        }
    }

    private async Task ClearChatAsync(long chatId, int latestMessageId, CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        var state = await stateStore.GetAsync(cancellationToken);
        var firstMessageId = Math.Max(1, state.TelegramHistoryStartMessageId ?? 1);
        foreach (var batch in BuildDeleteBatches(firstMessageId, latestMessageId))
        {
            await delivery.Bot.DeleteMessages(chatId, batch, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        await coordinator.SetTelegramHistoryStartAsync(latestMessageId + 1, cancellationToken);
    }

    internal static IEnumerable<int[]> BuildDeleteBatches(int firstMessageId, int latestMessageId)
    {
        if (firstMessageId < 1 || latestMessageId < firstMessageId)
        {
            yield break;
        }

        for (var last = latestMessageId; last >= firstMessageId; last -= 100)
        {
            var first = Math.Max(firstMessageId, last - 99);
            yield return Enumerable.Range(first, last - first + 1).ToArray();
        }
    }

    private async Task EnsureSessionIsIdleAsync(CancellationToken cancellationToken)
    {
        if (await coordinator.IsBusyAsync(cancellationToken))
        {
            throw new InvalidOperationException("A sessão está ocupada. Aguarde a resposta ou use /stop antes de continuar.");
        }
    }

    private async Task ChangeAgentAsync(
        long chatId,
        string agent,
        string prompt,
        CancellationToken cancellationToken)
    {
        await coordinator.SetAgentAsync(agent, cancellationToken);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await delivery.SendTextAsync(chatId, $"## Modo atualizado\n\n**Agente:** {AgentLabel(agent)}", cancellationToken);
            return;
        }

        await SendPromptWithProgressAsync(
            chatId,
            $"## Processando solicitação\n\n**Modo:** {AgentLabel(agent)}",
            prompt,
            cancellationToken);
    }

    private async Task SendPromptWithProgressAsync(
        long chatId,
        string progressText,
        string prompt,
        CancellationToken cancellationToken)
    {
        var feedbackStarted = false;
        try
        {
            await coordinator.SendPromptAsync(
                prompt,
                cancellationToken,
                async token =>
                {
                    await delivery.BeginProgressAsync(chatId, progressText, token);
                    feedbackStarted = true;
                    await delivery.StartTypingBestEffortAsync(chatId, token);
                });
        }
        catch
        {
            if (feedbackStarted)
            {
                await CleanupProgressAfterFailureAsync(chatId);
            }
            throw;
        }
    }

    private async Task RunWithProgressAsync(
        long chatId,
        string progressText,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await delivery.BeginProgressAsync(chatId, progressText, cancellationToken);
        await delivery.StartTypingBestEffortAsync(chatId, cancellationToken);
        try
        {
            await operation(cancellationToken);
        }
        catch
        {
            await CleanupProgressAfterFailureAsync(chatId);
            throw;
        }
    }

    private async Task CleanupProgressAfterFailureAsync(long chatId)
    {
        await delivery.StopTypingAsync(chatId);
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await delivery.ClearProgressAsync(chatId, cleanupTimeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Não foi possível remover o progresso após falha da operação");
        }
    }

    internal static string FormatStatus(RemoteState state, CurrentModelInfo currentModel)
    {
        var provider = currentModel.Model?.ProviderId ?? "automático";
        var model = currentModel.Model?.ModelId ?? "definido pelo OpenCode";
        var source = currentModel.Source switch
        {
            CurrentModelSource.Telegram => "seleção do Telegram",
            CurrentModelSource.Session => "último prompt da sessão",
            CurrentModelSource.Configuration => "configuração do OpenCode",
            _ => "automático do OpenCode",
        };

        return $"""
            ## Status atual

            **Projeto:** `{state.ProjectAlias ?? "nenhum"}`
            **Sessão:** `{state.SessionId ?? "nenhuma"}`
            **Modo:** {AgentLabel(state.Agent)}
            **Provider:** `{provider}`
            **Modelo:** `{model}`
            **Origem:** {source}
            """;
    }

    internal static string FormatTaskStatus(CurrentTaskStatus status, DateTimeOffset? now = null)
    {
        if (!status.IsActive)
        {
            return "## Tarefa atual\n\nNão há tarefa em execução nesta sessão.";
        }

        var details = new List<string>
        {
            $"**Estado:** {(status.IsPreparing ? "Preparando envio" : "Em execução")}",
        };
        if (status.StartedAt is { } startedAt)
        {
            details.Add($"**Tempo:** {FormatElapsed((now ?? DateTimeOffset.UtcNow) - startedAt)}");
        }
        if (!string.IsNullOrWhiteSpace(status.Step))
        {
            details.Add($"**Etapa:** {status.Step}");
        }
        if (!string.IsNullOrWhiteSpace(status.Activity))
        {
            details.Add($"**Atividade:** {status.Activity}");
        }
        if (status.Files > 0)
        {
            details.Add($"**Alterações:** {status.Files} arquivo(s), +{status.Additions}/-{status.Deletions}");
        }

        return $"## Tarefa atual\n\n{string.Join('\n', details)}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min {elapsed.Seconds} s";
        }
        if (elapsed.TotalMinutes >= 1)
        {
            return $"{elapsed.Minutes} min {elapsed.Seconds} s";
        }
        return $"{elapsed.Seconds} s";
    }

    private static string FormatModelChanged(OpenCodeModelRef model) => $"""
        ## Modelo atualizado

        **Provider:** `{model.ProviderId}`
        **Modelo:** `{model.ModelId}`

        A escolha prevalecerá sobre o modelo dos agentes nos próximos prompts desta sessão.
        """;

    private static string AgentLabel(string? agent)
        => string.Equals(agent, "plan", StringComparison.OrdinalIgnoreCase) ? "Plan" : "Build";
}
