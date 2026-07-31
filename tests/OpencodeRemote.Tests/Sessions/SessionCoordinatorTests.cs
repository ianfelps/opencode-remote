using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpencodeRemote.Tests.Sessions;

public sealed class SessionCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"opencode-remote-coordinator-{Guid.NewGuid():N}");

    [Fact]
    public async Task SelectProjectIsCaseInsensitiveAndClearsPreviousSession()
    {
        Directory.CreateDirectory(_directory);
        var options = CreateOptions(new ProjectOptions { Alias = "Main", Path = _directory });
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(1, "old", "session-1"), CancellationToken.None);
        using var client = CreateClient(options, _ => throw new InvalidOperationException("HTTP should not be called."));
        var coordinator = new SessionCoordinator(options, store, client);

        var state = await coordinator.SelectProjectAsync(42, "main", CancellationToken.None);

        Assert.Equal(new RemoteState(42, "Main", null), state);
        Assert.Equal(state, await new StateStore(options).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SelectProjectRejectsUnauthorizedAlias()
    {
        var options = CreateOptions();
        var store = new StateStore(options);
        using var client = CreateClient(options, _ => throw new InvalidOperationException("HTTP should not be called."));
        var coordinator = new SessionCoordinator(options, store, client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SelectProjectAsync(42, "unknown", CancellationToken.None));

        Assert.Equal("Projeto não autorizado.", exception.Message);
    }

    [Fact]
    public async Task SelectProjectRejectsMissingDirectory()
    {
        var missing = Path.Combine(_directory, "missing");
        var options = CreateOptions(new ProjectOptions { Alias = "missing", Path = missing });
        var store = new StateStore(options);
        using var client = CreateClient(options, _ => throw new InvalidOperationException("HTTP should not be called."));
        var coordinator = new SessionCoordinator(options, store, client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SelectProjectAsync(42, "missing", CancellationToken.None));

        Assert.Equal("O diretório configurado para o projeto não existe.", exception.Message);
    }

    [Fact]
    public async Task ListSessionsReturnsTenMostRecentlyUpdated()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias), CancellationToken.None);
        var sessions = Enumerable.Range(1, 12)
            .Select(index => new OpenCodeSession($"session-{index}", $"Session {index}", _directory, new SessionTime(0, index)))
            .ToArray();
        using var client = CreateClient(options, _ => StubHttpMessageHandler.Json(JsonSerializer.Serialize(sessions)));
        var coordinator = new SessionCoordinator(options, store, client);

        var result = await coordinator.ListSessionsAsync(CancellationToken.None);

        Assert.Equal(10, result.Count);
        Assert.Equal("session-12", result[0].Id);
        Assert.Equal("session-3", result[^1].Id);
    }

    [Fact]
    public async Task CreateSessionPersistsSelection()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias), CancellationToken.None);
        var expected = new OpenCodeSession("session-new", "New", _directory, new SessionTime(1, 1));
        using var client = CreateClient(options, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return StubHttpMessageHandler.Json(JsonSerializer.Serialize(expected));
        });
        var coordinator = new SessionCoordinator(options, store, client);

        var result = await coordinator.CreateSessionAsync(CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal("session-new", (await store.GetAsync(CancellationToken.None)).SessionId);
    }

    [Fact]
    public async Task SetAgentValidatesAndPersistsPlanMode()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-1"), CancellationToken.None);
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => StubHttpMessageHandler.Json("{}"),
            "/agent" => StubHttpMessageHandler.Json("[{\"name\":\"build\"},{\"name\":\"plan\"}]"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var coordinator = new SessionCoordinator(options, store, client);

        await coordinator.SetAgentAsync("plan", CancellationToken.None);

        Assert.Equal("plan", (await store.GetAsync(CancellationToken.None)).Agent);
    }

    [Fact]
    public async Task SendPromptUsesPersistedAgent()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-1", "plan"), CancellationToken.None);
        string? promptBody = null;
        using var client = CreateClient(options, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/status")
            {
                return StubHttpMessageHandler.Json("{}");
            }

            promptBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var coordinator = new SessionCoordinator(options, store, client);

        await coordinator.SendPromptAsync("analisar", CancellationToken.None);

        using var document = JsonDocument.Parse(promptBody!);
        Assert.Equal("plan", document.RootElement.GetProperty("agent").GetString());
        Assert.False(document.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task SelectedModelIsPersistedAndSentForCurrentSession()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-1"), CancellationToken.None);
        string? promptBody = null;
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => StubHttpMessageHandler.Json("{}"),
            "/config/providers" => StubHttpMessageHandler.Json(ProviderResponse()),
            "/session/session-1/prompt_async" => CapturePrompt(request, value => promptBody = value),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var coordinator = new SessionCoordinator(options, store, client);
        var selected = new OpenCodeModelRef("anthropic", "claude/sonnet");

        await coordinator.SetModelAsync(project.Path, "session-1", selected, CancellationToken.None);
        await coordinator.SendPromptAsync("analisar", CancellationToken.None);

        var selection = Assert.Single((await store.GetAsync(CancellationToken.None)).ModelSelections!);
        Assert.Equal(new SessionModelSelection(project.Alias, "session-1", selected), selection);
        using var document = JsonDocument.Parse(promptBody!);
        Assert.Equal("claude/sonnet", document.RootElement.GetProperty("model").GetProperty("modelID").GetString());
    }

    [Fact]
    public async Task AutomaticModelRemovesOnlyCurrentSessionSelection()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        var first = new SessionModelSelection(project.Alias, "session-1", new OpenCodeModelRef("anthropic", "one"));
        var second = new SessionModelSelection(project.Alias, "session-2", new OpenCodeModelRef("anthropic", "two"));
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-1", ModelSelections: [first, second]), CancellationToken.None);
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath == "/session/status"
            ? StubHttpMessageHandler.Json("{}")
            : throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"));
        var coordinator = new SessionCoordinator(options, store, client);

        await coordinator.SetModelAsync(project.Path, "session-1", null, CancellationToken.None);

        var selections = (await store.GetAsync(CancellationToken.None)).ModelSelections!;
        Assert.Equal(2, selections.Count);
        Assert.Contains(second, selections);
        Assert.Contains(new SessionModelSelection(project.Alias, "session-1", null), selections);
        Assert.Equal(
            new CurrentModelInfo(null, CurrentModelSource.Automatic),
            await coordinator.GetCurrentModelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CurrentModelPrefersTelegramSelectionWithoutCallingApi()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var selected = new OpenCodeModelRef("anthropic", "claude");
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(
            42,
            project.Alias,
            "session-1",
            ModelSelections: [new SessionModelSelection(project.Alias, "session-1", selected)]), CancellationToken.None);
        using var client = CreateClient(options, _ => throw new InvalidOperationException("HTTP should not be called."));
        var coordinator = new SessionCoordinator(options, store, client);

        var current = await coordinator.GetCurrentModelAsync(CancellationToken.None);

        Assert.Equal(new CurrentModelInfo(selected, CurrentModelSource.Telegram), current);
    }

    [Fact]
    public async Task ModelSelectionDoesNotLeakToAnotherSession()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        var selection = new SessionModelSelection(
            project.Alias,
            "session-1",
            new OpenCodeModelRef("anthropic", "claude"));
        await store.SaveAsync(new RemoteState(
            42,
            project.Alias,
            "session-2",
            ModelSelections: [selection]), CancellationToken.None);
        string? promptBody = null;
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => StubHttpMessageHandler.Json("{}"),
            "/session/session-2/prompt_async" => CapturePrompt(request, value => promptBody = value),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var coordinator = new SessionCoordinator(options, store, client);

        await coordinator.SendPromptAsync("analisar", CancellationToken.None);

        using var document = JsonDocument.Parse(promptBody!);
        Assert.False(document.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task SetModelRejectsCallbackFromPreviousSession()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-2"), CancellationToken.None);
        using var client = CreateClient(options, _ => throw new InvalidOperationException("HTTP should not be called."));
        var coordinator = new SessionCoordinator(options, store, client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SetModelAsync(
            project.Path,
            "session-1",
            new OpenCodeModelRef("anthropic", "claude"),
            CancellationToken.None));

        Assert.Contains("projeto ou a sessão", exception.Message);
        Assert.Null((await store.GetAsync(CancellationToken.None)).ModelSelections);
    }

    [Fact]
    public async Task SelectSessionRecoversLatestAgent()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias), CancellationToken.None);
        var session = new OpenCodeSession("session-1", "Existing", _directory, new SessionTime(1, 2));
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath switch
        {
            "/session" => StubHttpMessageHandler.Json(JsonSerializer.Serialize(new[] { session })),
            "/session/session-1/message" => StubHttpMessageHandler.Json("[{\"info\":{\"role\":\"user\",\"agent\":\"plan\"},\"parts\":[]}]"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var coordinator = new SessionCoordinator(options, store, client);

        var agent = await coordinator.SelectSessionAsync(session.Id, CancellationToken.None);

        Assert.Equal("plan", agent);
        Assert.Equal("plan", (await store.GetAsync(CancellationToken.None)).Agent);
    }

    [Fact]
    public async Task CurrentTaskStatusTracksLocallyStartedPrompt()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-1"), CancellationToken.None);
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => StubHttpMessageHandler.Json("{}"),
            "/session/session-1/prompt_async" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var coordinator = new SessionCoordinator(options, store, client);

        await coordinator.SendPromptAsync(
            "analisar",
            CancellationToken.None,
            _ =>
            {
                coordinator.UpdateTaskStep("session-1", "Preparar contexto");
                return Task.CompletedTask;
            });
        var status = await coordinator.GetCurrentTaskStatusAsync(CancellationToken.None);

        Assert.True(status.IsActive);
        Assert.False(status.IsPreparing);
        Assert.NotNull(status.StartedAt);
        Assert.Equal("Preparar contexto", status.Step);
    }

    [Fact]
    public async Task CurrentTaskStatusRecognizesRemoteExecutionWithoutLocalDetails()
    {
        Directory.CreateDirectory(_directory);
        var project = new ProjectOptions { Alias = "main", Path = _directory };
        var options = CreateOptions(project);
        var store = new StateStore(options);
        await store.SaveAsync(new RemoteState(42, project.Alias, "session-1"), CancellationToken.None);
        using var client = CreateClient(options, request => request.RequestUri!.AbsolutePath == "/session/status"
            ? StubHttpMessageHandler.Json("{\"session-1\":{\"type\":\"busy\"}}")
            : throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"));
        var coordinator = new SessionCoordinator(options, store, client);

        var status = await coordinator.GetCurrentTaskStatusAsync(CancellationToken.None);

        Assert.True(status.IsActive);
        Assert.Null(status.StartedAt);
        Assert.Null(status.Step);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private IOptions<RemoteOptions> CreateOptions(params ProjectOptions[] projects)
        => Options.Create(new RemoteOptions
        {
            StateFile = Path.Combine(_directory, "state.json"),
            Projects = [.. projects],
            OpenCode = new OpenCodeOptions { Password = "password" },
        });

    private static OpenCodeClient CreateClient(
        IOptions<RemoteOptions> options,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(options, new StubHttpMessageHandler(handler));

    private static string ProviderResponse() => """
        {
          "providers": [{
            "id": "anthropic",
            "name": "Anthropic",
            "models": {
              "claude/sonnet": {
                "id": "claude/sonnet",
                "providerID": "anthropic",
                "name": "Claude Sonnet",
                "status": "active"
              }
            }
          }]
        }
        """;

    private static HttpResponseMessage CapturePrompt(HttpRequestMessage request, Action<string> capture)
    {
        capture(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}
