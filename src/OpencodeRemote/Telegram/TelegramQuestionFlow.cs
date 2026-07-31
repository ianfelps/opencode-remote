using System.Collections.Concurrent;
using OpencodeRemote.OpenCode;
using OpencodeRemote.Telegram.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace OpencodeRemote.Telegram;

public sealed class TelegramQuestionFlow(OpenCodeClient openCode, TelegramDelivery delivery)
{
    private sealed record PendingQuestionProgress(
        string Directory,
        PendingQuestion Question,
        bool UseV2,
        int Index,
        IReadOnlyList<IReadOnlyList<string>> Answers,
        CancellationTokenSource FlowCancellation,
        int? MessageId = null);

    private readonly ConcurrentDictionary<long, PendingQuestionProgress> _pendingQuestions = new();

    public async Task SendQuestionAsync(
        long chatId,
        string directory,
        PendingQuestion question,
        bool useV2,
        CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        if (question.Questions.Count == 0)
        {
            await delivery.SendTextAsync(
                chatId,
                "## Pergunta inválida\n\nO OpenCode não enviou nenhuma pergunta para responder.",
                cancellationToken);
            return;
        }

        var progress = new PendingQuestionProgress(directory, question, useV2, 0, [], new CancellationTokenSource());
        _pendingQuestions.TryGetValue(chatId, out var previous);
        _pendingQuestions[chatId] = progress;
        previous?.FlowCancellation.Cancel();
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                progress.FlowCancellation.Token);
            await SendQuestionStepAsync(chatId, progress, operation.Token);
        }
        catch
        {
            if (previous is null || previous.Index >= previous.Question.Questions.Count)
            {
                _pendingQuestions.TryRemove(new KeyValuePair<long, PendingQuestionProgress>(chatId, progress));
            }
            else
            {
                _pendingQuestions.TryUpdate(
                    chatId,
                    previous with { FlowCancellation = new CancellationTokenSource() },
                    progress);
            }
            throw;
        }
    }

    internal void CancelPending(long chatId)
    {
        if (_pendingQuestions.TryRemove(chatId, out var pending))
        {
            pending.FlowCancellation.Cancel();
        }
    }

    internal Action? SuspendPending(long chatId)
    {
        if (!_pendingQuestions.TryRemove(chatId, out var pending))
        {
            return null;
        }

        pending.FlowCancellation.Cancel();
        return () =>
        {
            if (pending.Index < pending.Question.Questions.Count)
            {
                _pendingQuestions.TryAdd(
                    chatId,
                    pending with { FlowCancellation = new CancellationTokenSource() });
            }
        };
    }

    internal bool IsActive(long chatId, CallbackAction action)
        => _pendingQuestions.TryGetValue(chatId, out var pending)
            && pending.Question.Id == action.RequestId
            && pending.Question.SessionId == action.SessionId
            && pending.Index == action.QuestionIndex;

    internal async Task<bool> TryHandleFreeTextAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        if (text.StartsWith('/') || !_pendingQuestions.TryGetValue(chatId, out var pending))
        {
            return false;
        }

        await ReplyFreeTextQuestionAsync(chatId, text, pending, cancellationToken);
        return true;
    }

    internal async Task HandleSelectionAsync(
        long chatId,
        CallbackAction action,
        CancellationToken cancellationToken)
    {
        if (!_pendingQuestions.TryGetValue(chatId, out var pending)
            || pending.Question.Id != action.RequestId
            || pending.Question.SessionId != action.SessionId
            || pending.Index != action.QuestionIndex)
        {
            throw new InvalidOperationException("Esta pergunta não está mais ativa.");
        }

        await AdvanceQuestionAsync(chatId, pending, [action.Value!], cancellationToken);
    }

    private async Task ReplyFreeTextQuestionAsync(
        long chatId,
        string text,
        PendingQuestionProgress pending,
        CancellationToken cancellationToken)
    {
        if (pending.Index >= pending.Question.Questions.Count)
        {
            await delivery.SendTextAsync(
                chatId,
                "## Enviando respostas\n\nAguarde enquanto as respostas são enviadas ao OpenCode.",
                cancellationToken);
            return;
        }

        IReadOnlyList<string> answer;
        try
        {
            answer = ParseQuestionAnswer(pending.Question.Questions[pending.Index], text);
        }
        catch (InvalidOperationException exception)
        {
            await delivery.SendTextAsync(chatId, $"## Resposta inválida\n\n{exception.Message}", cancellationToken);
            return;
        }

        await AdvanceQuestionAsync(chatId, pending, answer, cancellationToken);
        if (pending.MessageId is { } messageId && delivery.Bot is not null)
        {
            await delivery.Bot.EditMessageReplyMarkup(chatId, messageId, cancellationToken: cancellationToken);
        }
    }

    private async Task SendQuestionStepAsync(
        long chatId,
        PendingQuestionProgress progress,
        CancellationToken cancellationToken)
    {
        if (delivery.Bot is null)
        {
            return;
        }

        var prompt = progress.Question.Questions[progress.Index];
        InlineKeyboardMarkup? keyboard = null;
        if (!prompt.Multiple && prompt.Options.Count > 0)
        {
            var callbackGroup = delivery.CreateCallbackGroup();
            keyboard = new InlineKeyboardMarkup(prompt.Options.Select(option => new[]
            {
                delivery.Button(option.Label, new CallbackAction(
                    progress.UseV2 ? "question-v2" : "question",
                    progress.Directory,
                    progress.Question.SessionId,
                    progress.Question.Id,
                    option.Label,
                    QuestionIndex: progress.Index), callbackGroup),
            }));
        }

        var message = await delivery.Bot.SendMessage(
            chatId,
            TelegramTextFormatter.ToHtml(FormatQuestion(progress.Question, progress.Index)),
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
        if (!_pendingQuestions.TryUpdate(chatId, progress with { MessageId = message.Id }, progress))
        {
            try
            {
                await delivery.Bot.DeleteMessage(chatId, message.Id, CancellationToken.None);
            }
            catch
            {
                // Cancellation won the race; removing the stale prompt is best effort.
            }
            throw new InvalidOperationException("Esta pergunta não está mais ativa.");
        }
    }

    private async Task AdvanceQuestionAsync(
        long chatId,
        PendingQuestionProgress progress,
        IReadOnlyList<string> answer,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            progress.FlowCancellation.Token);
        var operationToken = operation.Token;
        var answers = progress.Answers.Append(answer).ToArray();
        if (progress.Index + 1 < progress.Question.Questions.Count)
        {
            var next = progress with { Index = progress.Index + 1, Answers = answers, MessageId = null };
            if (!_pendingQuestions.TryUpdate(chatId, next, progress))
            {
                throw new InvalidOperationException("A pergunta já foi respondida.");
            }
            try
            {
                await SendQuestionStepAsync(chatId, next, operationToken);
            }
            catch (OperationCanceledException exception)
                when (progress.FlowCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _pendingQuestions.TryUpdate(chatId, progress, next);
                throw new InvalidOperationException("Esta pergunta não está mais ativa.", exception);
            }
            catch
            {
                _pendingQuestions.TryUpdate(chatId, progress, next);
                throw;
            }
            return;
        }

        var submitting = progress with { Index = progress.Question.Questions.Count, Answers = answers, MessageId = null };
        if (!_pendingQuestions.TryUpdate(chatId, submitting, progress))
        {
            throw new InvalidOperationException("A pergunta já foi respondida.");
        }

        try
        {
            await delivery.BeginProgressAsync(
                chatId,
                $"## Processando respostas\n\n**Respostas enviadas:** {answers.Length}",
                operationToken);
            await delivery.StartTypingBestEffortAsync(chatId, cancellationToken);
            await openCode.ReplyQuestionAsync(
                progress.Directory,
                progress.Question.SessionId,
                progress.Question.Id,
                answers,
                progress.UseV2,
                operationToken);
        }
        catch (OperationCanceledException exception)
            when (progress.FlowCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _pendingQuestions.TryUpdate(chatId, progress, submitting);
            await delivery.StopTypingAsync(chatId);
            await ClearProgressBestEffortAsync(chatId);
            throw new InvalidOperationException("Esta pergunta não está mais ativa.", exception);
        }
        catch
        {
            _pendingQuestions.TryUpdate(chatId, progress, submitting);
            await delivery.StopTypingAsync(chatId);
            await ClearProgressBestEffortAsync(chatId);
            throw;
        }
        if (!_pendingQuestions.TryRemove(new KeyValuePair<long, PendingQuestionProgress>(chatId, submitting)))
        {
            throw new InvalidOperationException("Esta pergunta não está mais ativa.");
        }
        progress.FlowCancellation.Cancel();
    }

    private async Task ClearProgressBestEffortAsync(long chatId)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await delivery.ClearProgressAsync(chatId, cleanupTimeout.Token);
        }
        catch (Exception)
        {
            // The original question remains active even if progress cleanup fails.
        }
    }

    internal static IReadOnlyList<string> ParseQuestionAnswer(QuestionPrompt prompt, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Envie uma resposta antes de continuar.");
        }

        if (!prompt.Multiple)
        {
            return [text.Trim()];
        }

        var answers = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return answers.Length == 0
            ? throw new InvalidOperationException("Envie ao menos uma opção. Separe múltiplas opções com vírgulas.")
            : answers;
    }

    internal static string FormatQuestion(PendingQuestion question, int index)
    {
        var prompt = question.Questions[index];
        var options = prompt.Options.Count == 0
            ? ""
            : "\n\n**Opções disponíveis**\n" + string.Join("\n", prompt.Options.Select(option =>
                string.IsNullOrWhiteSpace(option.Description)
                    ? $"- **{option.Label}**"
                    : $"- **{option.Label}:** {option.Description}"));
        var instruction = prompt.Multiple
            ? "Envie uma ou mais opções separadas por vírgulas."
            : prompt.Options.Count > 0
                ? "Toque em uma opção ou envie uma resposta personalizada."
                : "Envie sua resposta em uma única mensagem.";
        return $"""
            ## Pergunta {index + 1} de {question.Questions.Count}

            **{prompt.Header}**
            {prompt.Question}{options}

            {instruction}
            """;
    }
}
