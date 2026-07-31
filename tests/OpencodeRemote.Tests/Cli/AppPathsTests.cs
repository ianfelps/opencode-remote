using OpencodeRemote.Cli;

namespace OpencodeRemote.Tests.Cli;

public sealed class AppPathsTests
{
    [Fact]
    public void UsesSharedConfigurationAndProjectSpecificState()
    {
        var first = AppPaths.ForProject(Path.Combine(Path.GetTempPath(), "project-one"));
        var second = AppPaths.ForProject(Path.Combine(Path.GetTempPath(), "project-two"));

        Assert.Equal(first.ConfigurationFile, second.ConfigurationFile);
        Assert.Equal(first.LockFile, second.LockFile);
        Assert.NotEqual(first.StateFile, second.StateFile);
        Assert.EndsWith(".json", first.StateFile);
    }

    [Fact]
    public void StatePathIsStableForSameProject()
    {
        var project = Path.Combine(Path.GetTempPath(), "stable-project");

        Assert.Equal(AppPaths.ForProject(project).StateFile, AppPaths.ForProject(project).StateFile);
    }
}
