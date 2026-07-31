using OpencodeRemote.Cli;

namespace OpencodeRemote.Tests.Cli;

public sealed class ProjectResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"opencode-remote-project-{Guid.NewGuid():N}");

    [Fact]
    public void FindsGitRootFromNestedCurrentDirectory()
    {
        var nested = Path.Combine(_directory, "src", "feature");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(_directory, ".git"));

        Assert.Equal(Path.GetFullPath(_directory), ProjectResolver.FindGitRoot(nested));
    }

    [Fact]
    public void ExplicitDirectoryIsNotPromotedToGitRoot()
    {
        var nested = Path.Combine(_directory, "src");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(_directory, ".git"));

        Assert.Equal(Path.GetFullPath(nested), ProjectResolver.Resolve(nested));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
