using OpencodeRemote.Configuration;
using OpencodeRemote.OpenCode;
using OpencodeRemote.Persistence;
using OpencodeRemote.Sessions;
using OpencodeRemote.Telegram;

var envFile = FindEnvFile(AppContext.BaseDirectory);
if (envFile is not null)
{
    DotNetEnv.Env.NoClobber().Load(envFile);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OpenCode Remote");

builder.Services.Configure<RemoteOptions>(builder.Configuration.GetSection(RemoteOptions.SectionName));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<CallbackRegistry>();
builder.Services.AddSingleton<OpenCodeClient>();
builder.Services.AddSingleton<SessionCoordinator>();
builder.Services.AddSingleton<TelegramDelivery>();
builder.Services.AddSingleton<TelegramQuestionFlow>();
builder.Services.AddSingleton<TelegramInteractionHandler>();
builder.Services.AddSingleton<TelegramWorker>();
builder.Services.AddSingleton<IRemoteNotifier>(services => services.GetRequiredService<TelegramWorker>());
builder.Services.AddHostedService<OpenCodeProcessWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<TelegramWorker>());
builder.Services.AddHostedService<OpenCodeEventWorker>();

var host = builder.Build();
host.Run();

static string? FindEnvFile(string startPath)
{
    for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, ".env");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}
