using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using OpencodeRemote.Configuration;
using OpencodeRemote.Runtime;

namespace OpencodeRemote.OpenCode;

public sealed class OpenCodeProcessWorker(
    IOptions<RemoteOptions> options,
    OpenCodeClient client,
    ILogger<OpenCodeProcessWorker> logger,
    RuntimeStatusStore? runtime = null,
    ApplicationExitState? exitState = null) : BackgroundService
{
    private readonly OpenCodeOptions _settings = options.Value.OpenCode;
    private readonly StringBuilder _output = new();
    private Process? _process;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            exitState?.Fail();
            throw;
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_settings.ManageProcess && string.IsNullOrWhiteSpace(_settings.Password))
        {
            throw new InvalidOperationException("Configure uma senha para o servidor OpenCode antes de iniciá-lo.");
        }

        runtime?.SetOpenCode("verificando servidor");
        if (await client.IsHealthyAsync(stoppingToken))
        {
            runtime?.SetOpenCode($"conectado em {_settings.BaseUrl}");
            return;
        }
        if (!_settings.ManageProcess)
        {
            runtime?.SetOpenCode("indisponível");
            throw new InvalidOperationException($"OpenCode não está disponível em {_settings.BaseUrl}.");
        }

        var address = new Uri(_settings.BaseUrl);
        var startInfo = CreateStartInfo(address);
        startInfo.Environment["OPENCODE_SERVER_USERNAME"] = _settings.Username;
        startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = _settings.Password;
        logger.LogInformation("Iniciando OpenCode em {BaseUrl}", _settings.BaseUrl);
        runtime?.SetOpenCode("iniciando processo");
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o OpenCode.");
        _process.OutputDataReceived += (_, eventArgs) => Log(eventArgs.Data);
        _process.ErrorDataReceived += (_, eventArgs) => Log(eventArgs.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        for (var attempt = 0; attempt < 30 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (_process.HasExited)
            {
                runtime?.SetOpenCode("processo encerrado");
                throw new InvalidOperationException(
                    $"O processo do OpenCode encerrou com código {_process.ExitCode}. {_output.ToString().Trim()}");
            }

            if (await client.IsHealthyAsync(stoppingToken))
            {
                logger.LogInformation("OpenCode disponível em {BaseUrl}", _settings.BaseUrl);
                runtime?.SetOpenCode($"conectado em {_settings.BaseUrl}");
                await _process.WaitForExitAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                {
                    runtime?.SetOpenCode($"encerrado com código {_process.ExitCode}");
                    throw new InvalidOperationException($"O processo do OpenCode encerrou com código {_process.ExitCode}.");
                }
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        runtime?.SetOpenCode("tempo limite ao iniciar");
        throw new InvalidOperationException("OpenCode não ficou disponível dentro do tempo esperado.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(true);
            await _process.WaitForExitAsync(cancellationToken);
        }

        _process?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    private void Log(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            lock (_output)
            {
                _output.AppendLine(message);
            }
            logger.LogDebug("OpenCode: {Message}", message);
        }
    }

    private ProcessStartInfo CreateStartInfo(Uri address)
    {
        var executable = OperatingSystem.IsWindows() ? ResolveWindowsExecutable(_settings.Executable) : _settings.Executable;
        var isCommandScript = OperatingSystem.IsWindows()
            && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript ? "cmd.exe" : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (isCommandScript)
        {
            // cmd.exe requires the outer quotes when the command itself starts with a quoted path.
            startInfo.Arguments = $"/d /s /c \"\"{executable}\" serve --hostname {address.Host} --port {address.Port}\"";
        }
        else
        {
            startInfo.ArgumentList.Add("serve");
            startInfo.ArgumentList.Add("--hostname");
            startInfo.ArgumentList.Add(address.Host);
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(address.Port.ToString());
        }

        return startInfo;
    }

    private static string ResolveWindowsExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable) && File.Exists(executable))
        {
            return executable;
        }

        string[] extensions = Path.HasExtension(executable) ? [""] : [".exe", ".cmd", ".bat"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), executable + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Executável '{executable}' não foi encontrado no PATH.");
    }
}
