using System.Reflection;
using Microsoft.Extensions.Options;
using OpencodeRemote.Cli;
using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode;
using OpencodeRemote.Persistence;
using OpencodeRemote.Runtime;
using OpencodeRemote.Sessions;
using OpencodeRemote.Telegram;

Console.OutputEncoding = System.Text.Encoding.UTF8;
return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    CliArguments cli;
    try
    {
        cli = CliArguments.Parse(args);
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        Console.Error.WriteLine("Use 'opencode-remote --help' para ver os comandos disponíveis.");
        return 2;
    }

    if (cli.Command == CliCommand.Help)
    {
        PrintHelp();
        return 0;
    }
    if (cli.Command == CliCommand.Version)
    {
        Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development");
        return 0;
    }

    string projectPath;
    try
    {
        projectPath = ProjectResolver.Resolve(cli.ProjectPath);
    }
    catch (DirectoryNotFoundException exception)
    {
        Console.Error.WriteLine($"Erro: {exception.Message}");
        return 1;
    }
    var paths = AppPaths.ForProject(projectPath);
    try
    {
        if (cli.Command == CliCommand.Configure)
        {
            return await ConfigurationCommand.ConfigureAsync(paths.ConfigurationFile, CancellationToken.None);
        }
        if (cli.Command == CliCommand.ShowConfiguration)
        {
            return await ConfigurationCommand.ShowAsync(paths.ConfigurationFile, CancellationToken.None);
        }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"Erro ao acessar a configuração: {exception.Message}");
        return 1;
    }

    var alias = new DirectoryInfo(projectPath).Name;
    if (string.IsNullOrWhiteSpace(alias))
    {
        alias = projectPath;
    }
    var runOptions = new CliRunOptions(projectPath, alias, cli.Dashboard && !Console.IsOutputRedirected, cli.Verbose);
    try
    {
        using var instanceLock = InstanceLock.Acquire(paths.LockFile);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration
            .AddJsonFile(paths.ConfigurationFile, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        if (runOptions.Dashboard)
        {
            builder.Logging.ClearProviders();
        }
        else
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            if (runOptions.Verbose)
            {
                builder.Logging.SetMinimumLevel(LogLevel.Debug);
            }
        }

        builder.Services.Configure<RemoteOptions>(builder.Configuration.GetSection(RemoteOptions.SectionName));
        builder.Services.PostConfigure<RemoteOptions>(options =>
        {
            options.StateFile = paths.StateFile;
            options.Projects.Clear();
            options.Projects.Add(new ProjectOptions { Alias = alias, Path = projectPath });
        });
        builder.Services.AddSingleton(runOptions);
        builder.Services.AddSingleton<ApplicationExitState>();
        builder.Services.AddSingleton<RuntimeStatusStore>();
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton<CallbackRegistry>();
        builder.Services.AddSingleton<OpenCodeClient>();
        builder.Services.AddSingleton<SessionCoordinator>();
        builder.Services.AddSingleton<TelegramDelivery>();
        builder.Services.AddSingleton<TelegramQuestionFlow>();
        builder.Services.AddSingleton<TelegramInteractionHandler>();
        builder.Services.AddSingleton<TelegramWorker>();
        builder.Services.AddSingleton<IRemoteNotifier>(services => services.GetRequiredService<TelegramWorker>());
        builder.Services.AddHostedService<StartupProjectWorker>();
        builder.Services.AddHostedService<OpenCodeProcessWorker>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TelegramWorker>());
        builder.Services.AddHostedService<OpenCodeEventWorker>();
        builder.Services.AddHostedService<DashboardWorker>();

        using var host = builder.Build();
        Validate(host.Services.GetRequiredService<IOptions<RemoteOptions>>().Value);
        await host.RunAsync();
        return host.Services.GetRequiredService<ApplicationExitState>().ExitCode;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(cli.Verbose ? exception : $"Erro: {exception.Message}");
        return 1;
    }
}

static void Validate(RemoteOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Telegram.Token) || options.Telegram.AllowedUserId <= 0)
    {
        throw new InvalidOperationException("Telegram não configurado. Execute 'opencode-remote config'.");
    }
    if (options.OpenCode.ManageProcess && string.IsNullOrWhiteSpace(options.OpenCode.Password))
    {
        throw new InvalidOperationException("Senha do OpenCode não configurada. Execute 'opencode-remote config'.");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
        OpenCode Remote

        Uso:
          opencode-remote [diretório] [opções]
          opencode-remote run [diretório] [opções]
          opencode-remote config
          opencode-remote config show

        Opções:
          -v, --verbose       Exibe logs detalhados em vez do painel
          --no-dashboard      Exibe logs simples
          -h, --help          Exibe esta ajuda
          --version           Exibe a versão
        """);
}
