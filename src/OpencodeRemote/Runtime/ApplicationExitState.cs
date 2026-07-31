namespace OpencodeRemote.Runtime;

public sealed class ApplicationExitState
{
    private int _exitCode;

    public int ExitCode => Volatile.Read(ref _exitCode);

    public void Fail() => Interlocked.Exchange(ref _exitCode, 1);
}
