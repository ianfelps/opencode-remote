namespace OpencodeRemote.Cli;

internal enum CliCommand
{
    Run,
    Configure,
    ShowConfiguration,
    Help,
    Version,
}

internal sealed record CliArguments(
    CliCommand Command,
    string? ProjectPath = null,
    bool Verbose = false,
    bool Dashboard = true)
{
    public static CliArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliArguments(CliCommand.Run);
        }

        if (args is ["--help" or "-h"] || string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            return new CliArguments(CliCommand.Help);
        }
        if (args is ["--version"])
        {
            return new CliArguments(CliCommand.Version);
        }
        if (string.Equals(args[0], "config", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length == 1)
            {
                return new CliArguments(CliCommand.Configure);
            }
            if (args is [_, "show"])
            {
                return new CliArguments(CliCommand.ShowConfiguration);
            }
            throw new ArgumentException("Uso: opencode-remote config [show]");
        }

        var index = string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        string? projectPath = null;
        var verbose = false;
        var dashboard = true;
        for (; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--verbose" or "-v":
                    verbose = true;
                    dashboard = false;
                    break;
                case "--no-dashboard":
                    dashboard = false;
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Opção desconhecida: {args[index]}");
                    }
                    if (projectPath is not null)
                    {
                        throw new ArgumentException("Informe somente um diretório de projeto.");
                    }
                    projectPath = args[index];
                    break;
            }
        }

        return new CliArguments(CliCommand.Run, projectPath, verbose, dashboard);
    }
}
