using System.Text.Json;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;

namespace OpencodeRemote.Persistence;

public sealed class StateStore(IOptions<RemoteOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Environment.ExpandEnvironmentVariables(options.Value.StateFile);
    private RemoteState? _state;

    public async Task<RemoteState> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state is not null)
            {
                return _state;
            }

            if (!File.Exists(_path))
            {
                return _state = new RemoteState();
            }

            await using var stream = File.OpenRead(_path);
            return _state = await JsonSerializer.DeserializeAsync<RemoteState>(stream, cancellationToken: cancellationToken)
                ?? new RemoteState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RemoteState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = _path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, _path, true);
            _state = state;
        }
        finally
        {
            _gate.Release();
        }
    }
}
