using OpencodeRemote.Sessions.Models;

namespace OpencodeRemote.Persistence;

public sealed record RemoteState(
    long ChatId = 0,
    string? ProjectAlias = null,
    string? SessionId = null,
    string Agent = "build",
    IReadOnlyList<SessionModelSelection>? ModelSelections = null,
    int? TelegramHistoryStartMessageId = null,
    string? ProjectId = null,
    string? ProjectDirectory = null);
