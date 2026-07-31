namespace OpencodeRemote.Tests.Telegram;

public sealed class ToolProgressFormatterTests
{
    [Theory]
    [InlineData("read", "Analisando o projeto")]
    [InlineData("glob", "Analisando o projeto")]
    [InlineData("grep", "Analisando o projeto")]
    [InlineData("list", "Analisando o projeto")]
    [InlineData("bash", "Executando comandos")]
    [InlineData("shell", "Executando comandos")]
    [InlineData("edit", "Alterando arquivos")]
    [InlineData("write", "Alterando arquivos")]
    [InlineData("apply_patch", "Alterando arquivos")]
    [InlineData("webfetch", "Consultando fontes externas")]
    [InlineData("task", "Executando uma etapa delegada")]
    public void DescribesRunningToolsWithoutOperationDetails(string tool, string expected)
    {
        var result = ToolProgressFormatter.Format(tool, "running");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DescribesErrorsWithoutToolNamesOrPayloads()
    {
        var result = ToolProgressFormatter.Format("private-tool-name", "error");

        Assert.Equal("Não foi possível concluir: executando uma operação.", result);
        Assert.DoesNotContain("private-tool-name", result, StringComparison.Ordinal);
    }
}
