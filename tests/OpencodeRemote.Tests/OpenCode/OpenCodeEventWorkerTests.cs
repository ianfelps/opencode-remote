using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace OpencodeRemote.Tests.OpenCode;

public sealed class OpenCodeEventWorkerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"opencode-event-{Guid.NewGuid():N}");

    [Fact]
    public async Task SessionErrorClearsLocalBusyStateBeforeNotification()
    {
        Directory.CreateDirectory(_directory);
        var options = Options.Create(new RemoteOptions
        {
            Telegram = new TelegramOptions { Token = "token", AllowedUserId = 1 },
            Projects = [new ProjectOptions { Alias = "main", Path = _directory }],
            StateFile = Path.Combine(_directory, "state.json"),
        });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, "main", "session-1"), CancellationToken.None);
        using var client = new OpenCodeClient(options, new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/session/status" => StubHttpMessageHandler.Json("{}"),
                "/session/session-1/prompt_async" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            }));
        var coordinator = new SessionCoordinator(options, store, client);
        await coordinator.SendPromptAsync("work", CancellationToken.None);
        var notifier = new RecordingNotifier();
        var worker = new OpenCodeEventWorker(
            client,
            store,
            coordinator,
            notifier,
            options,
            NullLogger<OpenCodeEventWorker>.Instance);
        using var document = JsonDocument.Parse("""
            {
              "directory": "C:\\project",
              "payload": {
                "type": "session.error",
                "properties": { "sessionID": "session-1" }
              }
            }
            """);

        await worker.HandleEventAsync(document.RootElement, CancellationToken.None);

        Assert.False(await coordinator.IsBusyAsync(CancellationToken.None));
        Assert.Single(notifier.Messages);
    }

    [Fact]
    public async Task IdleResponseIsSentWhenProgressCleanupFails()
    {
        Directory.CreateDirectory(_directory);
        var options = Options.Create(new RemoteOptions
        {
            Telegram = new TelegramOptions { Token = "token", AllowedUserId = 1 },
            Projects = [new ProjectOptions { Alias = "main", Path = _directory }],
            StateFile = Path.Combine(_directory, "state.json"),
        });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, "main", "session-1"), CancellationToken.None);
        using var client = new OpenCodeClient(options, new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""
            [{"info":{"role":"assistant"},"parts":[{"type":"text","text":"finished"}]}]
            """)));
        var notifier = new RecordingNotifier { FailProgressCleanup = true };
        var worker = new OpenCodeEventWorker(
            client,
            store,
            new SessionCoordinator(options, store, client),
            notifier,
            options,
            NullLogger<OpenCodeEventWorker>.Instance);
        using var document = JsonDocument.Parse("""
            {
              "directory": "C:\\project",
              "payload": {
                "type": "session.idle",
                "properties": { "sessionID": "session-1" }
              }
            }
            """);

        await worker.HandleEventAsync(document.RootElement, CancellationToken.None);

        Assert.Equal(["finished"], notifier.Messages);
    }

    [Fact]
    public async Task DelayedIdleDoesNotClearCurrentlyBusyRun()
    {
        Directory.CreateDirectory(_directory);
        var options = Options.Create(new RemoteOptions
        {
            Telegram = new TelegramOptions { Token = "token", AllowedUserId = 1 },
            Projects = [new ProjectOptions { Alias = "main", Path = _directory }],
            StateFile = Path.Combine(_directory, "state.json"),
        });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, "main", "session-1"), CancellationToken.None);
        var statusRequests = 0;
        using var client = new OpenCodeClient(options, new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/session/status" when Interlocked.Increment(ref statusRequests) == 1
                    => StubHttpMessageHandler.Json("{}"),
                "/session/status" => StubHttpMessageHandler.Json("{\"session-1\":{\"type\":\"busy\"}}"),
                "/session/session-1/prompt_async" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            }));
        var coordinator = new SessionCoordinator(options, store, client);
        await coordinator.SendPromptAsync("work", CancellationToken.None);
        var notifier = new RecordingNotifier();
        var worker = new OpenCodeEventWorker(
            client,
            store,
            coordinator,
            notifier,
            options,
            NullLogger<OpenCodeEventWorker>.Instance);
        using var document = JsonDocument.Parse("""
            {
              "directory": "C:\\project",
              "payload": {
                "type": "session.idle",
                "properties": { "sessionID": "session-1" }
              }
            }
            """);

        await worker.HandleEventAsync(document.RootElement, CancellationToken.None);

        Assert.True(coordinator.IsLocallyActive("session-1"));
        Assert.Empty(notifier.Messages);
    }

    [Fact]
    public async Task TodoEventUpdatesCurrentTaskSnapshot()
    {
        Directory.CreateDirectory(_directory);
        var options = Options.Create(new RemoteOptions
        {
            Telegram = new TelegramOptions { Token = "token", AllowedUserId = 1 },
            Projects = [new ProjectOptions { Alias = "main", Path = _directory }],
            StateFile = Path.Combine(_directory, "state.json"),
        });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, "main", "session-1"), CancellationToken.None);
        using var client = new OpenCodeClient(options, new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/session/status" => StubHttpMessageHandler.Json("{}"),
                "/session/session-1/prompt_async" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            }));
        var coordinator = new SessionCoordinator(options, store, client);
        await coordinator.SendPromptAsync("work", CancellationToken.None);
        var notifier = new RecordingNotifier();
        var worker = new OpenCodeEventWorker(
            client,
            store,
            coordinator,
            notifier,
            options,
            NullLogger<OpenCodeEventWorker>.Instance);
        using var document = JsonDocument.Parse("""
            {
              "directory": "C:\\project",
              "payload": {
                "type": "todo.updated",
                "properties": {
                  "sessionID": "session-1",
                  "todos": [{ "content": "Executar os testes", "status": "in_progress" }]
                }
              }
            }
            """);

        await worker.HandleEventAsync(document.RootElement, CancellationToken.None);
        var status = await coordinator.GetCurrentTaskStatusAsync(CancellationToken.None);

        Assert.True(status.IsActive);
        Assert.Equal("Executar os testes", status.Step);
        Assert.Contains("Executar os testes", Assert.Single(notifier.ProgressMessages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateTodoAfterIdleDoesNotRecreateProgress()
    {
        Directory.CreateDirectory(_directory);
        var options = Options.Create(new RemoteOptions
        {
            Telegram = new TelegramOptions { Token = "token", AllowedUserId = 1 },
            Projects = [new ProjectOptions { Alias = "main", Path = _directory }],
            StateFile = Path.Combine(_directory, "state.json"),
        });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, "main", "session-1"), CancellationToken.None);
        using var client = new OpenCodeClient(options, new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""
            [{"info":{"role":"assistant"},"parts":[{"type":"text","text":"finished"}]}]
            """)));
        var coordinator = new SessionCoordinator(options, store, client);
        var notifier = new RecordingNotifier();
        var worker = new OpenCodeEventWorker(
            client,
            store,
            coordinator,
            notifier,
            options,
            NullLogger<OpenCodeEventWorker>.Instance);
        using var idle = JsonDocument.Parse("""
            {
              "directory": "C:\\project",
              "payload": {
                "type": "session.idle",
                "properties": { "sessionID": "session-1" }
              }
            }
            """);
        using var lateTodo = JsonDocument.Parse("""
            {
              "directory": "C:\\project",
              "payload": {
                "type": "todo.updated",
                "properties": {
                  "sessionID": "session-1",
                  "todos": [{ "content": "Evento atrasado", "status": "in_progress" }]
                }
              }
            }
            """);

        await worker.HandleEventAsync(idle.RootElement, CancellationToken.None);
        await worker.HandleEventAsync(lateTodo.RootElement, CancellationToken.None);

        Assert.Equal(["finished"], notifier.Messages);
        Assert.Empty(notifier.ProgressMessages);
        Assert.False(coordinator.IsLocallyActive("session-1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private sealed class RecordingNotifier : IRemoteNotifier
    {
        public List<string> Messages { get; } = [];
        public List<string> ProgressMessages { get; } = [];
        public bool FailProgressCleanup { get; init; }

        public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Messages.Add(text);
            return Task.CompletedTask;
        }

        public Task UpdateProgressAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            ProgressMessages.Add(text);
            return Task.CompletedTask;
        }
        public Task ClearProgressAsync(long chatId, CancellationToken cancellationToken)
            => FailProgressCleanup
                ? Task.FromException(new HttpRequestException("Telegram unavailable"))
                : Task.CompletedTask;
        public Task StartTypingAsync(long chatId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopTypingAsync(long chatId) => Task.CompletedTask;

        public Task SendPermissionAsync(
            long chatId,
            string directory,
            string sessionId,
            string permissionId,
            string title,
            bool useV2,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendQuestionAsync(
            long chatId,
            string directory,
            PendingQuestion question,
            bool useV2,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendPlanReadyAsync(
            long chatId,
            string directory,
            string sessionId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
