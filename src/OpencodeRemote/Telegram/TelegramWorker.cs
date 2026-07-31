using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.Runtime;
using OpencodeRemote.Telegram.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpencodeRemote.Telegram;

public sealed class TelegramWorker(
    IOptions<RemoteOptions> options,
    TelegramDelivery delivery,
    TelegramQuestionFlow questions,
    TelegramInteractionHandler interactions,
    ILogger<TelegramWorker> logger,
    RuntimeStatusStore? runtime = null,
    ApplicationExitState? exitState = null) : BackgroundService, IRemoteNotifier
{
    private readonly TelegramOptions _settings = options.Value.Telegram;
    private int _consecutivePollingErrors;

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
        delivery.SetStoppingToken(stoppingToken);
        if (delivery.Bot is null || _settings.AllowedUserId == 0)
        {
            runtime?.SetTelegram("não configurado");
            logger.LogWarning("Telegram desativado. Configure Remote:Telegram:Token e AllowedUserId.");
            return;
        }

        var receiverOptions = new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] };
        runtime?.SetTelegram("conectando");
        delivery.Bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);
        var me = await delivery.Bot.GetMe(stoppingToken);
        runtime?.SetTelegram($"conectado como @{me.Username}");
        logger.LogInformation("Bot Telegram @{Username} iniciado", me.Username);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken)
        => delivery.SendTextAsync(chatId, text, cancellationToken);

    public Task UpdateProgressAsync(long chatId, string text, CancellationToken cancellationToken)
        => delivery.UpdateProgressAsync(chatId, text, cancellationToken);

    public Task ClearProgressAsync(long chatId, CancellationToken cancellationToken)
        => delivery.ClearProgressAsync(chatId, cancellationToken);

    public Task StartTypingAsync(long chatId, CancellationToken cancellationToken)
        => delivery.StartTypingAsync(chatId, cancellationToken);

    public Task StopTypingAsync(long chatId) => delivery.StopTypingAsync(chatId);

    public Task SendPermissionAsync(
        long chatId,
        string directory,
        string sessionId,
        string permissionId,
        string title,
        bool useV2,
        CancellationToken cancellationToken)
        => interactions.SendPermissionAsync(
            chatId,
            directory,
            sessionId,
            permissionId,
            title,
            useV2,
            cancellationToken);

    public Task SendQuestionAsync(
        long chatId,
        string directory,
        PendingQuestion question,
        bool useV2,
        CancellationToken cancellationToken)
        => questions.SendQuestionAsync(chatId, directory, question, useV2, cancellationToken);

    public Task SendPlanReadyAsync(
        long chatId,
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
        => interactions.SendPlanReadyAsync(chatId, directory, sessionId, cancellationToken);

    private Task HandleUpdateAsync(
        ITelegramBotClient bot,
        Update update,
        CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _consecutivePollingErrors, 0);
        runtime?.SetTelegram("conectado");
        return interactions.HandleUpdateAsync(update, cancellationToken);
    }

    private async Task HandleErrorAsync(
        ITelegramBotClient bot,
        Exception exception,
        HandleErrorSource source,
        CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _consecutivePollingErrors);
        var delay = TelegramRetryPolicy.GetDelay(attempt);
        runtime?.SetTelegram($"reconectando em {delay.TotalSeconds:0}s");
        runtime?.SetError(exception.Message);
        if (exception is ApiRequestException { ErrorCode: >= 500 } apiException)
        {
            logger.LogWarning(
                "Telegram temporariamente indisponível (HTTP {StatusCode}). Nova tentativa em {DelaySeconds}s",
                apiException.ErrorCode,
                delay.TotalSeconds);
        }
        else
        {
            logger.LogError(
                exception,
                "Erro de polling do Telegram ({Source}). Nova tentativa em {DelaySeconds}s",
                source,
                delay.TotalSeconds);
        }

        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
