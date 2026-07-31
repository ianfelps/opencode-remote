using System.Security.Cryptography;
using System.Text;

namespace OpencodeRemote.Cli;

internal sealed record AppPaths(string ConfigurationFile, string StateFile, string LockFile)
{
    public static AppPaths ForProject(string projectPath)
    {
        var configRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var stateRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            stateRoot = configRoot;
        }

        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..16].ToLowerInvariant();
        return new AppPaths(
            Path.Combine(configRoot, "OpenCodeRemote", "config.json"),
            Path.Combine(stateRoot, "OpenCodeRemote", "projects", $"{hash}.json"),
            Path.Combine(stateRoot, "OpenCodeRemote", "opencode-remote.lock"));
    }
}
