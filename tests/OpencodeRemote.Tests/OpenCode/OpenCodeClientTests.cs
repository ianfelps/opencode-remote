using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpencodeRemote.Tests.OpenCode;

public sealed class OpenCodeClientTests
{
    [Fact]
    public async Task ListProjectsReturnsKnownWorktrees()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
        {
            captured = request;
            return StubHttpMessageHandler.Json("""
                [{"id":"project-1","worktree":"C:\\work","vcsDir":"C:\\work\\.git","time":{"created":1}}]
                """);
        });

        var projects = await client.ListProjectsAsync(CancellationToken.None);

        var project = Assert.Single(projects);
        Assert.Equal(new OpenCodeProject("project-1", @"C:\work", @"C:\work\.git"), project);
        Assert.Equal("http://127.0.0.1:4096/project", captured!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ListSessionsSendsAuthenticationAndDirectory()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
        {
            captured = request;
            return StubHttpMessageHandler.Json("""
                [{"id":"session-1","title":"Test","directory":"C:\\work","time":{"created":1,"updated":2}}]
                """);
        }, username: "remote", password: "secret");

        var sessions = await client.ListSessionsAsync(@"C:\work dir", CancellationToken.None);

        var session = Assert.Single(sessions);
        Assert.Equal("session-1", session.Id);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("http://127.0.0.1:4096/session?directory=C%3A%5Cwork%20dir", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", captured.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("remote:secret")), captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task SendPromptUsesAsyncEndpointAndTextPart()
    {
        string? body = null;
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await client.SendPromptAsync(@"C:\work", "session/1", "hello", "plan", null, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("http://127.0.0.1:4096/session/session%2F1/prompt_async?directory=C%3A%5Cwork", captured.RequestUri!.AbsoluteUri);
        using var document = JsonDocument.Parse(body!);
        var part = Assert.Single(document.RootElement.GetProperty("parts").EnumerateArray());
        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal("hello", part.GetProperty("text").GetString());
        Assert.Equal("plan", document.RootElement.GetProperty("agent").GetString());
        Assert.False(document.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task SendPromptIncludesSelectedModel()
    {
        string? body = null;
        using var client = CreateClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await client.SendPromptAsync(
            @"C:\work",
            "session-1",
            "hello",
            "build",
            new OpenCodeModelRef("anthropic", "claude/model"),
            CancellationToken.None);

        using var document = JsonDocument.Parse(body!);
        var model = document.RootElement.GetProperty("model");
        Assert.Equal("anthropic", model.GetProperty("providerID").GetString());
        Assert.Equal("claude/model", model.GetProperty("modelID").GetString());
    }

    [Fact]
    public async Task ListProvidersReturnsAvailableModels()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            {
              "providers": [
                {
                  "id": "anthropic",
                  "name": "Anthropic",
                  "models": {
                    "claude-new": {"id":"claude-new","providerID":"anthropic","name":"Claude New","status":"active"},
                    "claude-old": {"id":"claude-old","providerID":"anthropic","name":"Claude Old","status":"deprecated"}
                  }
                },
                {"id":"empty","name":"Empty","models":{}}
              ]
            }
            """));

        var providers = await client.ListProvidersAsync(@"C:\work", CancellationToken.None);

        var provider = Assert.Single(providers);
        Assert.Equal("anthropic", provider.Id);
        var model = Assert.Single(provider.Models);
        Assert.Equal(new OpenCodeModel("claude-new", "anthropic", "Claude New", "active"), model);
    }

    [Theory]
    [InlineData("idle", false)]
    [InlineData("busy", true)]
    [InlineData("retry", true)]
    public async Task IsSessionBusyInterpretsStatus(string status, bool expected)
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json(
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session-1"] = new { type = status },
            })));

        var result = await client.IsSessionBusyAsync(@"C:\work", "session-1", CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task IsSessionBusyReturnsFalseWhenSessionIsMissing()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("{}"));

        var result = await client.IsSessionBusyAsync(@"C:\work", "missing", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task LatestAssistantTextUsesNewestAssistantAndJoinsTextParts()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            [
              {"info":{"role":"assistant"},"parts":[{"type":"text","text":"old"}]},
              {"info":{"role":"user"},"parts":[{"type":"text","text":"question"}]},
              {"info":{"role":"assistant"},"parts":[
                {"type":"text","text":"first"},
                {"type":"tool","text":"ignored"},
                {"type":"text","text":"second"}
              ]}
            ]
            """));

        var result = await client.GetLatestAssistantTextAsync(@"C:\work", "session-1", CancellationToken.None);

        Assert.Equal("first\nsecond", result);
    }

    [Fact]
    public async Task LatestAssistantOutcomeIncludesProviderError()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            [{
              "info": {
                "id": "msg-error",
                "role": "assistant",
                "error": {
                  "name": "APIError",
                  "data": {
                    "message": "Quota exceeded. Check your plan and billing details.",
                    "statusCode": 429
                  }
                }
              },
              "parts": []
            }]
            """));

        var result = await client.GetLatestAssistantOutcomeAsync(@"C:\work", "session-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("msg-error", result.MessageId);
        Assert.True(result.IsError);
        Assert.Equal("Quota exceeded. Check your plan and billing details.", result.ErrorMessage);
    }

    [Fact]
    public async Task RecentConversationReturnsLatestTextMessagesAndSkipsSyntheticParts()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            [
              {"info":{"role":"user"},"parts":[{"type":"text","text":"old"}]},
              {"info":{"role":"assistant"},"parts":[{"type":"text","text":"internal","synthetic":true},{"type":"text","text":"answer"}]},
              {"info":{"role":"user"},"parts":[{"type":"tool","text":"ignored"},{"type":"text","text":"new question"}]}
            ]
            """));

        var result = await client.GetRecentConversationAsync(@"C:\work", "session-1", 2, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new ConversationMessage("assistant", "answer"), result[0]);
        Assert.Equal(new ConversationMessage("user", "new question"), result[1]);
    }

    [Fact]
    public async Task LatestUserAgentReturnsAgentFromNewestUserMessage()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            [
              {"info":{"role":"user","agent":"plan"},"parts":[]},
              {"info":{"role":"assistant"},"parts":[]},
              {"info":{"role":"user","agent":"build"},"parts":[]}
            ]
            """));

        var result = await client.GetLatestUserAgentAsync(@"C:\work", "session-1", CancellationToken.None);

        Assert.Equal("build", result);
    }

    [Fact]
    public async Task LatestUserModelReturnsProviderAndModelFromNewestUserMessage()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            [
              {"info":{"role":"user","model":{"providerID":"openai","modelID":"old"}},"parts":[]},
              {"info":{"role":"assistant"},"parts":[]},
              {"info":{"role":"user","model":{"providerID":"anthropic","modelID":"claude/new"}},"parts":[]}
            ]
            """));

        var result = await client.GetLatestUserModelAsync(@"C:\work", "session-1", CancellationToken.None);

        Assert.Equal(new OpenCodeModelRef("anthropic", "claude/new"), result);
    }

    [Theory]
    [InlineData("\"anthropic/claude/sonnet\"")]
    [InlineData("{\"providerID\":\"anthropic\",\"modelID\":\"claude/sonnet\"}")]
    public async Task ConfiguredModelSupportsStringAndObjectContracts(string modelJson)
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json($"{{\"model\":{modelJson}}}"));

        var result = await client.GetConfiguredModelAsync(@"C:\work", CancellationToken.None);

        Assert.Equal(new OpenCodeModelRef("anthropic", "claude/sonnet"), result);
    }

    [Fact]
    public async Task AgentExistsMatchesAgentNameCaseInsensitively()
    {
        using var client = CreateClient(_ => StubHttpMessageHandler.Json("""
            [{"name":"build"},{"name":"plan"}]
            """));

        Assert.True(await client.AgentExistsAsync(@"C:\work", "PLAN", CancellationToken.None));
        Assert.False(await client.AgentExistsAsync(@"C:\work", "review", CancellationToken.None));
    }

    [Theory]
    [InlineData(false, "permission/request%2F1/reply?directory=C%3A%5Cwork")]
    [InlineData(true, "api/session/session%2F1/permission/request%2F1/reply")]
    public async Task ReplyPermissionSelectsCompatibleEndpoint(bool useV2, string expectedPath)
    {
        string? path = null;
        string? body = null;
        using var client = CreateClient(request =>
        {
            path = request.RequestUri!.PathAndQuery.TrimStart('/');
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.ReplyPermissionAsync(@"C:\work", "session/1", "request/1", "once", useV2, CancellationToken.None);

        Assert.Equal(expectedPath, path);
        Assert.Contains("\"reply\":\"once\"", body);
    }

    [Fact]
    public async Task SubscribeEventsIgnoresNonDataLinesAndParsesEvents()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: message\n\ndata: {\"type\":\"first\"}\n\n: keepalive\ndata: {\"type\":\"second\"}\n")
        });
        var eventTypes = new List<string>();

        await foreach (var document in client.SubscribeEventsAsync(CancellationToken.None))
        {
            using (document)
            {
                eventTypes.Add(document.RootElement.GetProperty("type").GetString()!);
            }
        }

        Assert.Equal(["first", "second"], eventTypes);
    }

    [Fact]
    public async Task SubscribeEventsParsesMultilineDataRecord()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"first\",\ndata: \"value\":42}\n\n")
        });

        await foreach (var document in client.SubscribeEventsAsync(CancellationToken.None))
        {
            using (document)
            {
                Assert.Equal("first", document.RootElement.GetProperty("type").GetString());
                Assert.Equal(42, document.RootElement.GetProperty("value").GetInt32());
            }
        }
    }

    [Fact]
    public async Task IsHealthyReturnsFalseForConnectionFailure()
    {
        using var client = CreateClient((_, _) => throw new HttpRequestException("offline"));

        var result = await client.IsHealthyAsync(CancellationToken.None);

        Assert.False(result);
    }

    private static OpenCodeClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string username = "opencode",
        string password = "password")
        => CreateClient((request, _) => Task.FromResult(handler(request)), username, password);

    private static OpenCodeClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        string username = "opencode",
        string password = "password")
    {
        var options = Options.Create(new RemoteOptions
        {
            OpenCode = new OpenCodeOptions
            {
                BaseUrl = "http://127.0.0.1:4096",
                Username = username,
                Password = password,
            },
        });
        return new OpenCodeClient(options, new StubHttpMessageHandler(handler));
    }
}
