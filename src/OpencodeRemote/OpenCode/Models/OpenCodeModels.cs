using System.Text.Json.Serialization;

namespace OpencodeRemote.OpenCode.Models;

public sealed record OpenCodeProject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("worktree")] string Worktree,
    [property: JsonPropertyName("vcsDir")] string? VcsDirectory = null);

public sealed record OpenCodeSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("time")] SessionTime Time);

public sealed record SessionTime(
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("updated")] long Updated);

public sealed record ConversationMessage(string Role, string Text);

public sealed record AssistantOutcome(string? MessageId, string Text, string? ErrorMessage)
{
    public bool IsError => !string.IsNullOrWhiteSpace(ErrorMessage);
}

public sealed record OpenCodeModelRef(
    [property: JsonPropertyName("providerID")] string ProviderId,
    [property: JsonPropertyName("modelID")] string ModelId);

public sealed record OpenCodeModel(string Id, string ProviderId, string Name, string? Status = null);

public sealed record OpenCodeProvider(string Id, string Name, IReadOnlyList<OpenCodeModel> Models);
