using OpencodeRemote.Cli;
using Spectre.Console;

namespace OpencodeRemote.Runtime;

internal sealed class DashboardWorker(
    RuntimeStatusStore status,
    CliRunOptions runOptions,
    IHostApplicationLifetime lifetime,
    ApplicationExitState exitState) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runOptions.Dashboard || Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            AnsiConsole.Clear();
            await AnsiConsole.Live(BuildDashboard(status.Get()))
                .AutoClear(false)
                .StartAsync(async context =>
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        context.UpdateTarget(BuildDashboard(status.Get()));
                        context.Refresh();
                        await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                    }
                });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            exitState.Fail();
            AnsiConsole.MarkupLine($"[red]Falha no painel:[/] {Markup.Escape(exception.Message)}");
            lifetime.StopApplication();
        }
    }

    private Rows BuildDashboard(RuntimeSnapshot snapshot)
    {
        var width = Math.Min(100, Math.Max(36, AnsiConsole.Profile.Width - 6));
        var context = CreateTable();
        Add(context, "project", runOptions.ProjectAlias);
        Add(context, "directory", runOptions.ProjectPath);
        Add(context, "session", snapshot.SessionId ?? "nenhuma selecionada");
        Add(context, "agent", snapshot.Agent);
        Add(context, "model", snapshot.Model);

        var task = snapshot.Task;
        var taskText = task is not { IsActive: true }
            ? "ocioso"
            : task.IsPreparing
                ? "enviando solicitação"
                : task.StartedAt is { } started
                    ? $"trabalhando há {FormatDuration(DateTimeOffset.UtcNow - started)}"
                    : "trabalhando";
        var activity = CreateTable();
        Add(activity, "status", taskText);
        if (!string.IsNullOrWhiteSpace(task?.Step))
        {
            Add(activity, "step", task.Step);
        }
        if (!string.IsNullOrWhiteSpace(task?.Activity))
        {
            Add(activity, "activity", task.Activity);
        }
        if (task?.Files > 0)
        {
            Add(activity, "changes", $"{task.Files} arquivo(s), +{task.Additions}/-{task.Deletions}");
        }
        Add(activity, "attention", snapshot.Attention ?? "nenhuma");
        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            Add(activity, "last error", snapshot.LastError);
        }

        var connections = new Markup(
            $"{ServiceMarkup("opencode", snapshot.OpenCode)}   "
            + $"{ServiceMarkup("telegram", snapshot.Telegram)}   "
            + ServiceMarkup("events", snapshot.Events));
        var contextPanel = new Panel(context)
            .Header("[grey] workspace [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Padding(1, 0);
        contextPanel.Width = width;
        var activityPanel = new Panel(activity)
            .Header("[grey] activity [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(task is { IsActive: true } ? Color.DeepSkyBlue1 : Color.Grey35)
            .Padding(1, 0);
        activityPanel.Width = width;

        return new Rows(
            Align.Center(BuildLogo()),
            Align.Center(new Markup("[grey]R  E  M  O  T  E[/]")),
            new Text(""),
            Align.Center(connections),
            new Text(""),
            Align.Center(contextPanel),
            Align.Center(activityPanel),
            new Text(""),
            Align.Center(new Markup("[grey]ctrl+c to exit  |  continue pelo Telegram[/]")));
    }

    private static Markup BuildLogo()
    {
        string[] left =
        [
            "                   ",
            "█▀▀█ █▀▀█ █▀▀█ █▀▀▄",
            "█  █ █  █ █▀▀▀ █  █",
            "▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀  ▀",
        ];
        string[] right =
        [
            "             ▄     ",
            "█▀▀▀ █▀▀█ █▀▀█ █▀▀█",
            "█    █  █ █  █ █▀▀▀",
            "▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀",
        ];
        return new Markup(string.Join(
            Environment.NewLine,
            left.Zip(right, (muted, primary) => $"[grey]{muted}[/] [bold deepskyblue1]{primary}[/]")));
    }

    private static Table CreateTable() => new Table()
        .NoBorder()
        .HideHeaders()
        .AddColumn(new TableColumn("").NoWrap().Width(12))
        .AddColumn(new TableColumn(""));

    private static void Add(Table table, string label, string value)
        => table.AddRow($"[grey]{Markup.Escape(label)}[/]", $"[white]{Markup.Escape(value)}[/]");

    private static string ServiceMarkup(string name, string value)
    {
        var color = value.Contains("conectado", StringComparison.OrdinalIgnoreCase)
            ? "green"
            : value.Contains("indisponível", StringComparison.OrdinalIgnoreCase)
                || value.Contains("encerrado", StringComparison.OrdinalIgnoreCase)
                || value.Contains("erro", StringComparison.OrdinalIgnoreCase)
                    ? "red"
                    : "yellow";
        return $"[{color}][[{Markup.Escape(name)}]][/] [grey]{Markup.Escape(value)}[/]";
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString(@"mm\:ss");
}
