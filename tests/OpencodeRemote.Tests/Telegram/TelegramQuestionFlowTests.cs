using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace OpencodeRemote.Tests.Telegram;

public sealed class TelegramQuestionFlowTests
{
    [Fact]
    public void QuestionFormattingShowsOneStepAndItsOptions()
    {
        var question = new PendingQuestion(
            "request-1",
            "session-1",
            [
                new QuestionPrompt(
                    "Qual abordagem devemos usar?",
                    "Abordagem",
                    false,
                    [
                        new QuestionOption("Minimalista", "Altera o mínimo necessário"),
                        new QuestionOption("Completa", "Inclui melhorias relacionadas"),
                    ]),
                new QuestionPrompt("Qual nome prefere?", "Nome", false, []),
            ]);

        var html = TelegramTextFormatter.ToHtml(TelegramQuestionFlow.FormatQuestion(question, 0));

        Assert.Contains("<b>Pergunta 1 de 2</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Abordagem</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Minimalista:</b> Altera o m&#237;nimo necess&#225;rio", html, StringComparison.Ordinal);
        Assert.Contains("Toque em uma op&#231;&#227;o", html, StringComparison.Ordinal);
        Assert.DoesNotContain("|", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleQuestionAnswerPreservesPipesAndCommas()
    {
        var prompt = new QuestionPrompt("Descreva", "Descrição", false, []);

        var answers = TelegramQuestionFlow.ParseQuestionAnswer(prompt, "primeiro | segundo, terceiro");

        Assert.Equal(["primeiro | segundo, terceiro"], answers);
    }

    [Fact]
    public void MultipleQuestionAnswerUsesOnlyCommasAsSeparators()
    {
        var prompt = new QuestionPrompt("Escolha", "Opções", true, []);

        var answers = TelegramQuestionFlow.ParseQuestionAnswer(prompt, "primeiro, segundo, terceiro");

        Assert.Equal(["primeiro", "segundo", "terceiro"], answers);
    }

    [Fact]
    public void EmptyQuestionAnswerIsRejected()
    {
        var prompt = new QuestionPrompt("Responda", "Resposta", false, []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TelegramQuestionFlow.ParseQuestionAnswer(prompt, "   "));

        Assert.Contains("Envie uma resposta", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedInitialDeliveryDoesNotLeavePendingQuestion()
    {
        var flow = CreateFlow((_, _) => Task.FromResult(TelegramError()));
        var question = CreateQuestion();
        var action = CreateAction(question, 0);

        await Assert.ThrowsAsync<ApiRequestException>(() =>
            flow.SendQuestionAsync(42, "C:\\project", question, false, CancellationToken.None));

        Assert.False(flow.IsActive(42, action));
    }

    [Fact]
    public async Task FailedReplacementRestoresPreviousQuestion()
    {
        var request = 0;
        var flow = CreateFlow((_, _) => Task.FromResult(
            Interlocked.Increment(ref request) == 1 ? TelegramMessage() : TelegramError()));
        var previous = CreateQuestion();
        var previousAction = CreateAction(previous, 0);
        await flow.SendQuestionAsync(42, "C:\\project", previous, false, CancellationToken.None);
        var replacement = previous with { Id = "request-2" };

        await Assert.ThrowsAsync<ApiRequestException>(() =>
            flow.SendQuestionAsync(42, "C:\\project", replacement, false, CancellationToken.None));

        Assert.True(flow.IsActive(42, previousAction));
        Assert.False(flow.IsActive(42, CreateAction(replacement, 0)));
    }

    [Fact]
    public async Task FailedNextStepRestoresPreviousQuestion()
    {
        var request = 0;
        var flow = CreateFlow((_, _) => Task.FromResult(
            Interlocked.Increment(ref request) == 1 ? TelegramMessage() : TelegramError()));
        var question = CreateQuestion();
        var firstAction = CreateAction(question, 0);

        await flow.SendQuestionAsync(42, "C:\\project", question, false, CancellationToken.None);

        await Assert.ThrowsAsync<ApiRequestException>(() =>
            flow.HandleSelectionAsync(42, firstAction, CancellationToken.None));
        Assert.True(flow.IsActive(42, firstAction));
    }

    [Fact]
    public async Task CancelPendingRemovesActiveQuestion()
    {
        var flow = CreateFlow((_, _) => Task.FromResult(TelegramMessage()));
        var question = CreateQuestion();
        var action = CreateAction(question, 0);
        await flow.SendQuestionAsync(42, "C:\\project", question, false, CancellationToken.None);

        flow.CancelPending(42);

        Assert.False(flow.IsActive(42, action));
    }

    [Fact]
    public async Task SuspendedQuestionCanBeRestoredAfterFailedTransition()
    {
        var flow = CreateFlow((_, _) => Task.FromResult(TelegramMessage()));
        var question = CreateQuestion();
        var action = CreateAction(question, 0);
        await flow.SendQuestionAsync(42, "C:\\project", question, false, CancellationToken.None);

        var restore = flow.SuspendPending(42);
        Assert.False(flow.IsActive(42, action));
        restore!();

        Assert.True(flow.IsActive(42, action));
    }

    [Fact]
    public async Task CancelPendingCancelsSubmissionInFlight()
    {
        var submissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = CreateFlow(
            (_, _) => Task.FromResult(TelegramMessage()),
            async (_, cancellationToken) =>
            {
                submissionStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return StubHttpMessageHandler.Json("{}");
            });
        var question = new PendingQuestion(
            "request-1",
            "session-1",
            [new QuestionPrompt("Continuar?", "Confirmação", false, [new QuestionOption("Sim", "")])]);
        var action = CreateAction(question, 0);
        await flow.SendQuestionAsync(42, "C:\\project", question, false, CancellationToken.None);

        var submission = flow.HandleSelectionAsync(42, action, CancellationToken.None);
        await submissionStarted.Task;
        flow.CancelPending(42);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => submission);
        Assert.Contains("não está mais ativa", exception.Message, StringComparison.Ordinal);
        Assert.False(flow.IsActive(42, action));
    }

    [Fact]
    public async Task CompletedSubmissionDoesNotReactivateCanceledFlow()
    {
        var submissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = CreateFlow(
            (_, _) => Task.FromResult(TelegramMessage()),
            async (_, _) =>
            {
                submissionStarted.SetResult();
                await releaseSubmission.Task;
                return StubHttpMessageHandler.Json("{}");
            });
        var question = new PendingQuestion(
            "request-1",
            "session-1",
            [new QuestionPrompt("Continuar?", "Confirmação", false, [new QuestionOption("Sim", "")])]);
        var action = CreateAction(question, 0);
        await flow.SendQuestionAsync(42, "C:\\project", question, false, CancellationToken.None);

        var submission = flow.HandleSelectionAsync(42, action, CancellationToken.None);
        await submissionStarted.Task;
        flow.CancelPending(42);
        releaseSubmission.SetResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => submission);
        Assert.Contains("não está mais ativa", exception.Message, StringComparison.Ordinal);
        Assert.False(flow.IsActive(42, action));
    }

    private static TelegramQuestionFlow CreateFlow(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> telegramHandler,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? openCodeHandler = null)
    {
        var options = Options.Create(new RemoteOptions());
        var bot = new TelegramBotClient(
            "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
            new HttpClient(new StubHttpMessageHandler(telegramHandler)));
        var delivery = new TelegramDelivery(
            bot,
            new CallbackRegistry(),
            NullLogger<TelegramWorker>.Instance);
        var openCode = new OpenCodeClient(
            options,
            new StubHttpMessageHandler(openCodeHandler ?? ((_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}")))));
        return new TelegramQuestionFlow(openCode, delivery);
    }

    private static PendingQuestion CreateQuestion() => new(
        "request-1",
        "session-1",
        [
            new QuestionPrompt("Primeira?", "Primeira", false, [new QuestionOption("Sim", "")]),
            new QuestionPrompt("Segunda?", "Segunda", false, []),
        ]);

    private static CallbackAction CreateAction(PendingQuestion question, int index) => new(
        "question",
        "C:\\project",
        question.SessionId,
        question.Id,
        "Sim",
        QuestionIndex: index);

    private static HttpResponseMessage TelegramMessage() => StubHttpMessageHandler.Json("""
        {"ok":true,"result":{"message_id":100,"date":1710000000,"chat":{"id":42,"type":"private"}}}
        """);

    private static HttpResponseMessage TelegramError() => StubHttpMessageHandler.Json(
        """{"ok":false,"error_code":500,"description":"Temporary failure"}""",
        HttpStatusCode.InternalServerError);
}
