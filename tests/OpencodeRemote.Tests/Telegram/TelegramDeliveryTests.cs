using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace OpencodeRemote.Tests.Telegram;

public sealed class TelegramDeliveryTests
{
    [Fact]
    public async Task FailedProgressDeletionRetainsMessageForRetry()
    {
        var deleteAttempts = 0;
        var delivery = CreateDelivery((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                return Task.FromResult(TelegramMessage());
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/deleteMessage", StringComparison.Ordinal)
                && Interlocked.Increment(ref deleteAttempts) == 1)
            {
                return Task.FromResult(TelegramError());
            }

            return Task.FromResult(TelegramSuccess());
        });

        await delivery.UpdateProgressAsync(42, "Processando", CancellationToken.None);

        await Assert.ThrowsAsync<ApiRequestException>(() =>
            delivery.ClearProgressAsync(42, CancellationToken.None));
        await delivery.ClearProgressAsync(42, CancellationToken.None);

        Assert.Equal(2, deleteAttempts);
    }

    [Fact]
    public async Task ReplacedTypingIndicatorsAreStoppedAndRemoved()
    {
        var delivery = CreateDelivery((_, _) => Task.FromResult(TelegramSuccess()));
        delivery.SetStoppingToken(CancellationToken.None);

        await delivery.StartTypingAsync(42, CancellationToken.None);
        await delivery.StartTypingAsync(42, CancellationToken.None);
        await delivery.StopTypingAsync(42);

        Assert.False(await delivery.IsTypingAsync(42));
    }

    [Fact]
    public async Task InternalTypingCancellationDoesNotFailStart()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = CreateDelivery(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TelegramSuccess();
        });
        delivery.SetStoppingToken(CancellationToken.None);

        var start = delivery.StartTypingAsync(42, CancellationToken.None);
        await requestStarted.Task;
        await delivery.StopTypingAsync(42);

        await start;
        Assert.False(await delivery.IsTypingAsync(42));
    }

    [Fact]
    public async Task MissingProgressMessageIsForgotten()
    {
        var deleteAttempts = 0;
        var delivery = CreateDelivery((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                return Task.FromResult(TelegramMessage());
            }

            Interlocked.Increment(ref deleteAttempts);
            return Task.FromResult(StubHttpMessageHandler.Json(
                """{"ok":false,"error_code":400,"description":"Bad Request: message to delete not found"}""",
                HttpStatusCode.BadRequest));
        });
        await delivery.UpdateProgressAsync(42, "Processando", CancellationToken.None);

        await delivery.ClearProgressAsync(42, CancellationToken.None);
        await delivery.ClearProgressAsync(42, CancellationToken.None);

        Assert.Equal(1, deleteAttempts);
    }

    [Fact]
    public async Task OtherBadRequestRetainsProgressMessage()
    {
        var deleteAttempts = 0;
        var delivery = CreateDelivery((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                return Task.FromResult(TelegramMessage());
            }

            return Task.FromResult(Interlocked.Increment(ref deleteAttempts) == 1
                ? StubHttpMessageHandler.Json(
                    """{"ok":false,"error_code":400,"description":"Bad Request: message can't be deleted"}""",
                    HttpStatusCode.BadRequest)
                : TelegramSuccess());
        });
        await delivery.UpdateProgressAsync(42, "Processando", CancellationToken.None);

        await Assert.ThrowsAsync<ApiRequestException>(() =>
            delivery.ClearProgressAsync(42, CancellationToken.None));
        await delivery.ClearProgressAsync(42, CancellationToken.None);

        Assert.Equal(2, deleteAttempts);
    }

    [Fact]
    public async Task BeginProgressCreatesFreshMessageForIdenticalText()
    {
        var sends = 0;
        var deletes = 0;
        var delivery = CreateDelivery((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                var id = Interlocked.Increment(ref sends);
                var json = """{"ok":true,"result":{"message_id":__ID__,"date":1710000000,"chat":{"id":42,"type":"private"}}}"""
                    .Replace("__ID__", id.ToString(), StringComparison.Ordinal);
                return Task.FromResult(StubHttpMessageHandler.Json(json));
            }

            Interlocked.Increment(ref deletes);
            return Task.FromResult(TelegramSuccess());
        });

        await delivery.BeginProgressAsync(42, "Processando", CancellationToken.None);
        await delivery.BeginProgressAsync(42, "Processando", CancellationToken.None);

        Assert.Equal(2, sends);
        Assert.Equal(1, deletes);
    }

    [Fact]
    public async Task TypingApiFailureIsBestEffort()
    {
        var delivery = CreateDelivery((_, _) => Task.FromResult(TelegramError()));
        delivery.SetStoppingToken(CancellationToken.None);

        await delivery.StartTypingBestEffortAsync(42, CancellationToken.None);

        Assert.False(await delivery.IsTypingAsync(42));
    }

    private static TelegramDelivery CreateDelivery(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var bot = new TelegramBotClient(
            "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
            new HttpClient(new StubHttpMessageHandler(handler)));
        return new TelegramDelivery(bot, new CallbackRegistry(), NullLogger<TelegramWorker>.Instance);
    }

    private static HttpResponseMessage TelegramMessage() => StubHttpMessageHandler.Json("""
        {"ok":true,"result":{"message_id":100,"date":1710000000,"chat":{"id":42,"type":"private"}}}
        """);

    private static HttpResponseMessage TelegramSuccess() => StubHttpMessageHandler.Json("""{"ok":true,"result":true}""");

    private static HttpResponseMessage TelegramError() => StubHttpMessageHandler.Json(
        """{"ok":false,"error_code":500,"description":"Temporary failure"}""",
        HttpStatusCode.InternalServerError);
}
