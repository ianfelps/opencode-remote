using OpencodeRemote.Cli;

namespace OpencodeRemote.Tests.Cli;

public sealed class CliArgumentsTests
{
    [Fact]
    public void EmptyArgumentsRunCurrentDirectoryWithDashboard()
    {
        var result = CliArguments.Parse([]);

        Assert.Equal(CliCommand.Run, result.Command);
        Assert.Null(result.ProjectPath);
        Assert.True(result.Dashboard);
        Assert.False(result.Verbose);
    }

    [Fact]
    public void RunAcceptsDirectoryAndVerboseMode()
    {
        var result = CliArguments.Parse(["run", "project", "--verbose"]);

        Assert.Equal(CliCommand.Run, result.Command);
        Assert.Equal("project", result.ProjectPath);
        Assert.True(result.Verbose);
        Assert.False(result.Dashboard);
    }

    [Theory]
    [InlineData("config", 1)]
    [InlineData("config show", 2)]
    [InlineData("--help", 3)]
    [InlineData("--version", 4)]
    public void ParsesStandaloneCommands(string command, int expected)
    {
        var result = CliArguments.Parse(command.Split(' '));

        Assert.Equal((CliCommand)expected, result.Command);
    }

    [Fact]
    public void RejectsUnknownOption()
    {
        var exception = Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--unknown"]));

        Assert.Contains("Opção desconhecida", exception.Message);
    }
}
