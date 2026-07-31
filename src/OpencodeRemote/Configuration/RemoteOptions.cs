namespace OpencodeRemote.Configuration;

public sealed class RemoteOptions
{
    public const string SectionName = "Remote";

    public TelegramOptions Telegram { get; init; } = new();
    public OpenCodeOptions OpenCode { get; init; } = new();
    public List<ProjectOptions> Projects { get; init; } = [];
    public string StateFile { get; set; } = "";
}

public sealed class TelegramOptions
{
    public string Token { get; init; } = "";
    public long AllowedUserId { get; init; }
}

public sealed class OpenCodeOptions
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:4096";
    public string Username { get; init; } = "opencode";
    public string Password { get; init; } = "";
    public string Executable { get; init; } = "opencode";
    public bool ManageProcess { get; init; } = true;
}

public sealed class ProjectOptions
{
    public string? Id { get; init; }
    public required string Alias { get; init; }
    public required string Path { get; init; }
}
