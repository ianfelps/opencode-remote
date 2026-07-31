namespace OpencodeRemote.Telegram.Models;

public sealed record CallbackAction(
    string Kind,
    string Directory,
    string? SessionId = null,
    string? RequestId = null,
    string? Value = null,
    string? ProviderId = null,
    string? ModelId = null,
    int? QuestionIndex = null);
