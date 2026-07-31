using System.Text.Json;
using OpencodeRemote.Cli;

namespace OpencodeRemote.Tests.Cli;

public sealed class ConfigurationCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"opencode-remote-config-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavePreservesAdvancedSettingsAndExistingKeyCasing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.json");
        await File.WriteAllTextAsync(path, """
            {
              "remote": {
                "telegram": { "token": "old", "allowedUserId": 1 },
                "openCode": {
                  "username": "old-user",
                  "password": "old-password",
                  "BaseUrl": "http://127.0.0.1:5000",
                  "ManageProcess": false
                }
              },
              "FutureSetting": true
            }
            """);

        await ConfigurationCommand.SaveAsync(path, "new-token", 42, "new-user", "new-password", CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        Assert.True(root.GetProperty("FutureSetting").GetBoolean());
        var remote = root.GetProperty("remote");
        Assert.False(root.TryGetProperty("Remote", out _));
        Assert.Equal("new-token", remote.GetProperty("telegram").GetProperty("token").GetString());
        Assert.Equal(42, remote.GetProperty("telegram").GetProperty("allowedUserId").GetInt64());
        var openCode = remote.GetProperty("openCode");
        Assert.Equal("new-user", openCode.GetProperty("username").GetString());
        Assert.Equal("new-password", openCode.GetProperty("password").GetString());
        Assert.Equal("http://127.0.0.1:5000", openCode.GetProperty("BaseUrl").GetString());
        Assert.False(openCode.GetProperty("ManageProcess").GetBoolean());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
