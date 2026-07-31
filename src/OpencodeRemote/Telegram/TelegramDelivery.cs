using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.Telegram.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace OpencodeRemote.Telegram;

public sealed class TelegramDelivery
{
    private sealed record ProgressMessage(int MessageId, string Text);

    private const int TelegramMessageLimit = 3500;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _typingIndicators = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _typingLocks = new();
    private readonly ConcurrentDictionary<long, ProgressMessage> _progressMessages = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _progressLocks = new();
    private readonly CallbackRegistry _callbacks;
    private readonly ILogger<TelegramWorker> _logger;
    private CancellationToken _stoppingToken;

    public TelegramDelivery(
        IOptions<RemoteOptions> options,
        CallbackRegistry callbacks,
        ILogger<TelegramWorker> logger)
        : this(CreateBot(options.Value.Telegram.Token), callbacks, logger)
    {
    }

    internal TelegramDelivery(
        ITelegramBotClient? bot,
        CallbackRegistry callbacks,
        ILogger<TelegramWorker> logger)
    {
        _callbacks = callbacks;
        _logger = logger;
        Bot = bot;
    }

    internal ITelegramBotClient? Bot { get; }

    internal void SetStoppingToken(CancellationToken stoppingToken) => _stoppingToken = stoppingToken;

    public async Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        if (Bot is null || chatId == 0)
        {
            return;
        }

        foreach (var chunk in Split(text, TelegramMessageLimit))
        {
            await Bot.SendMessage(
                chatId,
                TelegramTextFormatter.ToHtml(chunk),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

        var typingToken = await GetTypingTokenAsync(chatId, cancellationToken);
        if (typingToken is { IsCancellationRequested: false } token)
        {
            try
            {
                await Bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Typing stopped after the text was delivered; the send itself still succeeded.
            }
        }
    }

    public async Task UpdateProgressAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        if (Bot is null || chatId == 0)
        {
            return;
        }

        var gate = _progressLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_progressMessages.TryGetValue(chatId, out var current))
            {
                if (current.Text == text)
                {
                    return;
                }

                try
                {
                    await Bot.EditMessageText(
                        chatId,
                        current.MessageId,
                        TelegramTextFormatter.ToHtml(text),
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);
                    _progressMessages[chatId] = current with { Text = text };
                    return;
                }
                catch (ApiRequestException exception) when (exception.ErrorCode == 400)
                {
                    _progressMessages.TryRemove(chatId, out _);
                }
            }

            var message = await Bot.SendMessage(
                chatId,
                TelegramTextFormatter.ToHtml(text),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            _progressMessages[chatId] = new ProgressMessage(message.Id, text);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task BeginProgressAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        if (Bot is null || chatId == 0)
        {
            return;
        }

        var gate = _progressLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            _progressMessages.TryGetValue(chatId, out var previous);
            var message = await Bot.SendMessage(
                chatId,
                TelegramTextFormatter.ToHtml(text),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            _progressMessages[chatId] = new ProgressMessage(message.Id, text);

            if (previous is not null && previous.MessageId != message.Id)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await Bot.DeleteMessage(chatId, previous.MessageId, cleanupTimeout.Token);
                }
                catch (ApiRequestException exception) when (exception.ErrorCode == 400)
                {
                    // A fresh progress message is already visible; stale-message cleanup is best effort.
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Não foi possível remover a mensagem de progresso anterior");
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearProgressAsync(long chatId, CancellationToken cancellationToken)
    {
        if (Bot is null || chatId == 0)
        {
            return;
        }

        var gate = _progressLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_progressMessages.TryGetValue(chatId, out var current))
            {
                return;
            }

            try
            {
                await Bot.DeleteMessage(chatId, current.MessageId, cancellationToken);
                _progressMessages.TryRemove(new KeyValuePair<long, ProgressMessage>(chatId, current));
            }
            catch (ApiRequestException exception) when (IsMissingMessage(exception))
            {
                // The progress message may already have been removed by /clear or by the user.
                _progressMessages.TryRemove(new KeyValuePair<long, ProgressMessage>(chatId, current));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartTypingAsync(long chatId, CancellationToken cancellationToken)
    {
        if (Bot is null || chatId == 0)
        {
            return;
        }

        var gate = _typingLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        CancellationTokenSource source;
        try
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);
            if (_typingIndicators.TryGetValue(chatId, out var previous))
            {
                previous.Cancel();
            }
            _typingIndicators[chatId] = source;
        }
        finally
        {
            gate.Release();
        }

        try
        {
            await Bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: source.Token);
            _ = ContinueTypingAsync(chatId, source);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await RemoveTypingSourceAsync(chatId, source);
        }
        catch
        {
            await RemoveTypingSourceAsync(chatId, source);
            throw;
        }
    }

    internal async Task StartTypingBestEffortAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            await StartTypingAsync(chatId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "Não foi possível iniciar o indicador de digitação");
        }
    }

    public async Task StopTypingAsync(long chatId)
    {
        var gate = _typingLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_typingIndicators.TryRemove(chatId, out var source))
            {
                source.Cancel();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task SendKeyboardAsync(
        long chatId,
        string text,
        InlineKeyboardMarkup keyboard,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
        {
            return;
        }

        await Bot.SendMessage(
            chatId,
            TelegramTextFormatter.ToHtml(text),
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    internal string CreateCallbackGroup() => _callbacks.CreateGroup();

    internal InlineKeyboardButton Button(string text, CallbackAction action, string? group = null)
        => InlineKeyboardButton.WithCallbackData(TrimButtonText(text), _callbacks.Add(action, group: group));

    internal bool TryReserveCallback(string key, out CallbackRegistry.Reservation? reservation)
        => _callbacks.TryReserve(key, out reservation);

    internal bool RestoreCallback(CallbackRegistry.Reservation reservation) => _callbacks.Restore(reservation);

    internal static string TrimButtonText(string text) => text.Length <= 50 ? text : text[..47] + "...";

    internal async Task<bool> IsTypingAsync(long chatId)
    {
        var gate = _typingLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return _typingIndicators.ContainsKey(chatId);
        }
        finally
        {
            gate.Release();
        }
    }

    private static IEnumerable<string> Split(string text, int length)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return "Concluído sem resposta textual.";
            yield break;
        }

        for (var offset = 0; offset < text.Length; offset += length)
        {
            yield return text.Substring(offset, Math.Min(length, text.Length - offset));
        }
    }

    private async Task ContinueTypingAsync(long chatId, CancellationTokenSource source)
    {
        try
        {
            while (!source.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), source.Token);
                await Bot!.SendChatAction(chatId, ChatAction.Typing, cancellationToken: source.Token);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Não foi possível renovar o indicador de digitação");
        }
        finally
        {
            await RemoveTypingSourceAsync(chatId, source);
        }
    }

    private async Task<CancellationToken?> GetTypingTokenAsync(long chatId, CancellationToken cancellationToken)
    {
        var gate = _typingLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return _typingIndicators.TryGetValue(chatId, out var source) ? source.Token : null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RemoveTypingSourceAsync(long chatId, CancellationTokenSource source)
    {
        var gate = _typingLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            _typingIndicators.TryRemove(new KeyValuePair<long, CancellationTokenSource>(chatId, source));
        }
        finally
        {
            gate.Release();
            source.Dispose();
        }
    }

    private static ITelegramBotClient? CreateBot(string token)
        => string.IsNullOrWhiteSpace(token) ? null : new TelegramBotClient(token);

    private static bool IsMissingMessage(ApiRequestException exception)
        => exception.ErrorCode == 400
            && exception.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase);
}
