using OpencodeRemote.Runtime;
using OpencodeRemote.Sessions;

namespace OpencodeRemote.Cli;

internal sealed class StartupProjectWorker(
    SessionCoordinator coordinator,
    CliRunOptions options,
    RuntimeStatusStore runtime) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var state = await coordinator.ActivateProjectAsync(options.ProjectAlias, cancellationToken);
        runtime.SetSelection(state.SessionId, state.Agent);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
