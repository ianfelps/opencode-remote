using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode.Models;

namespace OpencodeRemote.OpenCode;

public sealed class OpenCodeClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly TimeSpan _eventInactivityTimeout;

    public OpenCodeClient(IOptions<RemoteOptions> options) : this(options, new HttpClientHandler())
    {
    }

    internal OpenCodeClient(
        IOptions<RemoteOptions> options,
        HttpMessageHandler handler,
        TimeSpan? eventInactivityTimeout = null)
    {
        var settings = options.Value.OpenCode;
        _client = new HttpClient(handler) { BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));
        _eventInactivityTimeout = eventInactivityTimeout ?? TimeSpan.FromSeconds(45);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync("global/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OpenCodeSession>> ListSessionsAsync(string directory, CancellationToken cancellationToken)
    {
        return await _client.GetFromJsonAsync<List<OpenCodeSession>>(WithDirectory("session", directory), JsonOptions, cancellationToken) ?? [];
    }

    public async Task<OpenCodeSession> CreateSessionAsync(string directory, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(WithDirectory("session", directory), new { }, JsonOptions, cancellationToken);
        return await ReadAsync<OpenCodeSession>(response, cancellationToken);
    }

    public async Task SendPromptAsync(
        string directory,
        string sessionId,
        string text,
        string agent,
        OpenCodeModelRef? model,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["agent"] = agent,
            ["parts"] = new[] { new { type = "text", text } },
        };
        if (model is not null)
        {
            body["model"] = model;
        }

        using var response = await _client.PostAsJsonAsync(
            WithDirectory($"session/{Uri.EscapeDataString(sessionId)}/prompt_async", directory), body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<OpenCodeProvider>> ListProvidersAsync(string directory, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(WithDirectory("config/providers", directory), cancellationToken);
        if (!document.RootElement.TryGetProperty("providers", out var providersElement)
            || providersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var providers = new List<OpenCodeProvider>();
        foreach (var providerElement in providersElement.EnumerateArray())
        {
            var providerId = GetString(providerElement, "id");
            if (string.IsNullOrWhiteSpace(providerId))
            {
                continue;
            }

            var models = new List<OpenCodeModel>();
            if (providerElement.TryGetProperty("models", out var modelsElement)
                && modelsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var modelProperty in modelsElement.EnumerateObject())
                {
                    var modelId = GetString(modelProperty.Value, "id") ?? modelProperty.Name;
                    var status = GetString(modelProperty.Value, "status");
                    if (string.Equals(status, "deprecated", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    models.Add(new OpenCodeModel(
                        modelId,
                        GetString(modelProperty.Value, "providerID") ?? providerId,
                        GetString(modelProperty.Value, "name") ?? modelId,
                        status));
                }
            }

            providers.Add(new OpenCodeProvider(
                providerId,
                GetString(providerElement, "name") ?? providerId,
                models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        return providers
            .Where(provider => provider.Models.Count > 0)
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<bool> AgentExistsAsync(string directory, string agent, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(WithDirectory("agent", directory), cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Array
            && document.RootElement.EnumerateArray().Any(item =>
                item.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), agent, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsSessionBusyAsync(string directory, string sessionId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(WithDirectory("session/status", directory), cancellationToken);
        if (!document.RootElement.TryGetProperty(sessionId, out var status)
            || !status.TryGetProperty("type", out var type))
        {
            return false;
        }

        return type.GetString() != "idle";
    }

    public async Task AbortAsync(string directory, string sessionId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(
            WithDirectory($"session/{Uri.EscapeDataString(sessionId)}/abort", directory), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReplyPermissionAsync(
        string directory,
        string sessionId,
        string permissionId,
        string responseValue,
        bool useV2,
        CancellationToken cancellationToken)
    {
        var path = useV2
            ? $"api/session/{Uri.EscapeDataString(sessionId)}/permission/{Uri.EscapeDataString(permissionId)}/reply"
            : WithDirectory($"permission/{Uri.EscapeDataString(permissionId)}/reply", directory);
        using var response = await _client.PostAsJsonAsync(
            path, new { reply = responseValue }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReplyQuestionAsync(
        string directory,
        string sessionId,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        bool useV2,
        CancellationToken cancellationToken)
    {
        var path = useV2
            ? $"api/session/{Uri.EscapeDataString(sessionId)}/question/{Uri.EscapeDataString(requestId)}/reply"
            : WithDirectory($"question/{Uri.EscapeDataString(requestId)}/reply", directory);
        using var response = await _client.PostAsJsonAsync(
            path, new { answers }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetLatestAssistantTextAsync(string directory, string sessionId, CancellationToken cancellationToken)
    {
        var path = WithDirectory($"session/{Uri.EscapeDataString(sessionId)}/message?limit=10", directory, true);
        using var document = await GetJsonAsync(path, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in document.RootElement.EnumerateArray().Reverse())
        {
            if (!message.TryGetProperty("info", out var info)
                || !info.TryGetProperty("role", out var role)
                || role.GetString() != "assistant"
                || !message.TryGetProperty("parts", out var parts))
            {
                continue;
            }

            var texts = parts.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(part => part.TryGetProperty("text", out var textPart) ? textPart.GetString() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text));
            return string.Join("\n", texts!);
        }

        return null;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetRecentConversationAsync(
        string directory,
        string sessionId,
        int messageLimit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messageLimit, 1);
        var path = WithDirectory($"session/{Uri.EscapeDataString(sessionId)}/message?limit=20", directory, true);
        using var document = await GetJsonAsync(path, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var messages = new List<ConversationMessage>();
        foreach (var message in document.RootElement.EnumerateArray())
        {
            if (!message.TryGetProperty("info", out var info)
                || !info.TryGetProperty("role", out var roleElement)
                || !message.TryGetProperty("parts", out var parts))
            {
                continue;
            }

            var role = roleElement.GetString();
            if (role is not ("user" or "assistant"))
            {
                continue;
            }

            var texts = parts.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Where(part => !part.TryGetProperty("synthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.True)
                .Select(part => part.TryGetProperty("text", out var textPart) ? textPart.GetString() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text));
            var text = string.Join("\n", texts!).Trim();
            if (text.Length > 0)
            {
                messages.Add(new ConversationMessage(role, text));
            }
        }

        return messages.TakeLast(messageLimit).ToArray();
    }

    public async Task<string?> GetLatestUserAgentAsync(string directory, string sessionId, CancellationToken cancellationToken)
    {
        var path = WithDirectory($"session/{Uri.EscapeDataString(sessionId)}/message?limit=20", directory, true);
        using var document = await GetJsonAsync(path, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in document.RootElement.EnumerateArray().Reverse())
        {
            if (message.TryGetProperty("info", out var info)
                && GetString(info, "role") == "user"
                && GetString(info, "agent") is { Length: > 0 } agent)
            {
                return agent;
            }
        }

        return null;
    }

    public async Task<OpenCodeModelRef?> GetLatestUserModelAsync(
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var path = WithDirectory($"session/{Uri.EscapeDataString(sessionId)}/message?limit=20", directory, true);
        using var document = await GetJsonAsync(path, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in document.RootElement.EnumerateArray().Reverse())
        {
            if (message.TryGetProperty("info", out var info)
                && GetString(info, "role") == "user"
                && info.TryGetProperty("model", out var model)
                && TryReadModel(model) is { } modelReference)
            {
                return modelReference;
            }
        }

        return null;
    }

    public async Task<OpenCodeModelRef?> GetConfiguredModelAsync(string directory, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(WithDirectory("config", directory), cancellationToken);
        if (!document.RootElement.TryGetProperty("model", out var model))
        {
            return null;
        }

        if (TryReadModel(model) is { } modelReference)
        {
            return modelReference;
        }

        if (model.ValueKind != JsonValueKind.String || model.GetString() is not { } configured)
        {
            return null;
        }

        var separator = configured.IndexOf('/');
        return separator <= 0 || separator == configured.Length - 1
            ? null
            : new OpenCodeModelRef(configured[..separator], configured[(separator + 1)..]);
    }

    public async IAsyncEnumerable<JsonDocument> SubscribeEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "global/event");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inactivity.CancelAfter(_eventInactivityTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(inactivity.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("OpenCode event stream became inactive.", exception);
            }

            if (line is null)
            {
                if (data.Length > 0)
                {
                    yield return JsonDocument.Parse(data.ToString());
                }
                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return JsonDocument.Parse(data.ToString());
                    data.Clear();
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.AppendLine();
                }
                data.Append(line[5..].TrimStart());
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("OpenCode returned an empty response.");
    }

    private static string WithDirectory(string path, string directory, bool alreadyHasQuery = false)
        => $"{path}{(alreadyHasQuery ? '&' : '?')}directory={Uri.EscapeDataString(Path.GetFullPath(directory))}";

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static OpenCodeModelRef? TryReadModel(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var providerId = GetString(model, "providerID");
        var modelId = GetString(model, "modelID") ?? GetString(model, "id");
        return string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)
            ? null
            : new OpenCodeModelRef(providerId, modelId);
    }

    public void Dispose() => _client.Dispose();
}
