using OpencodeRemote.Cli;

namespace OpencodeRemote.Tests.Cli;

public sealed class InstanceLockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"opencode-remote-lock-{Guid.NewGuid():N}");

    [Fact]
    public void PreventsConcurrentInstanceAndReleasesLock()
    {
        var path = Path.Combine(_directory, "app.lock");
        using (InstanceLock.Acquire(path))
        {
            Assert.Throws<InvalidOperationException>(() => InstanceLock.Acquire(path));
        }

        using var acquiredAgain = InstanceLock.Acquire(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
