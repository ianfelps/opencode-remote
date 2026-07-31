namespace OpencodeRemote.Cli;

internal static class ProjectResolver
{
    public static string Resolve(string? path)
    {
        var requested = Path.GetFullPath(path ?? Environment.CurrentDirectory);
        if (!Directory.Exists(requested))
        {
            throw new DirectoryNotFoundException($"O diretório do projeto não existe: {requested}");
        }

        return path is not null
            ? Path.TrimEndingDirectorySeparator(requested)
            : FindGitRoot(requested) ?? Path.TrimEndingDirectorySeparator(requested);
    }

    internal static string? FindGitRoot(string startPath)
    {
        var requested = Path.GetFullPath(startPath);
        for (var directory = new DirectoryInfo(requested); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }

        return null;
    }
}
