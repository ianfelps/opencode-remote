using OpencodeRemote.OpenCode.Models;

namespace OpencodeRemote.Sessions.Models;

public sealed record SessionModelSelection(string ProjectAlias, string SessionId, OpenCodeModelRef? Model);

public enum CurrentModelSource
{
    Automatic,
    Configuration,
    Session,
    Telegram,
}

public sealed record CurrentModelInfo(OpenCodeModelRef? Model, CurrentModelSource Source);

public sealed record CurrentTaskStatus(
    bool IsActive,
    bool IsPreparing = false,
    DateTimeOffset? StartedAt = null,
    string? Step = null,
    string? Activity = null,
    int Files = 0,
    int Additions = 0,
    int Deletions = 0);
