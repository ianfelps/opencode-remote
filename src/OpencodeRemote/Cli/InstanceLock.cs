namespace OpencodeRemote.Cli;

internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            return new InstanceLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Outra instância do OpenCode Remote já está em execução.", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
