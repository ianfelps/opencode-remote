namespace OpencodeRemote.Tests.Telegram;

public sealed class TelegramInteractionHandlerTests
{
    [Theory]
    [InlineData("/start")]
    [InlineData("/help")]
    [InlineData("/session")]
    [InlineData("/sessions")]
    [InlineData("/move")]
    [InlineData("/new")]
    [InlineData("/plan [mensagem]")]
    [InlineData("/build [mensagem]")]
    [InlineData("/mode")]
    [InlineData("/model")]
    [InlineData("/status")]
    [InlineData("/task")]
    [InlineData("/stop")]
    [InlineData("/clear")]
    public void HelpListsAvailableCommands(string command)
    {
        Assert.Contains($"`{command}`", TelegramInteractionHandler.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpUsesSupportedFormattingAndUsageGuidance()
    {
        var html = TelegramTextFormatter.ToHtml(TelegramInteractionHandler.HelpText);

        Assert.Contains("<b>OpenCode Remote</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Sess&#227;o</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Modos e prompts</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Estado e controle</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Ajuda</b>", html, StringComparison.Ordinal);
        Assert.Contains("Selecione uma sess&#227;o", html, StringComparison.Ordinal);
        Assert.Contains("mensagem comum", html, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusFormatsModelAndSource()
    {
        var state = new RemoteState(42, "main", "session-1", "plan");
        var model = new CurrentModelInfo(
            new OpenCodeModelRef("anthropic", "claude/sonnet"),
            CurrentModelSource.Telegram);

        var html = TelegramTextFormatter.ToHtml(TelegramInteractionHandler.FormatStatus(state, model));

        Assert.Contains("<b>Status atual</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Projeto:</b> <code>main</code>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Modo:</b> Plan", html, StringComparison.Ordinal);
        Assert.Contains("<b>Provider:</b> <code>anthropic</code>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Modelo:</b> <code>claude/sonnet</code>", html, StringComparison.Ordinal);
        Assert.Contains("sele&#231;&#227;o do Telegram", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskStatusFormatsAvailableProgress()
    {
        var startedAt = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var status = new CurrentTaskStatus(
            true,
            StartedAt: startedAt,
            Step: "Executar os testes",
            Activity: "Executando comando",
            Files: 4,
            Additions: 82,
            Deletions: 17);

        var html = TelegramTextFormatter.ToHtml(TelegramInteractionHandler.FormatTaskStatus(
            status,
            startedAt.AddMinutes(3).AddSeconds(18)));

        Assert.Contains("<b>Tarefa atual</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Estado:</b> Em execu&#231;&#227;o", html, StringComparison.Ordinal);
        Assert.Contains("<b>Tempo:</b> 3 min 18 s", html, StringComparison.Ordinal);
        Assert.Contains("<b>Etapa:</b> Executar os testes", html, StringComparison.Ordinal);
        Assert.Contains("<b>Atividade:</b> Executando comando", html, StringComparison.Ordinal);
        Assert.Contains("<b>Altera&#231;&#245;es:</b> 4 arquivo(s), +82/-17", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskStatusReportsWhenThereIsNoActiveTask()
    {
        var result = TelegramInteractionHandler.FormatTaskStatus(new CurrentTaskStatus(false));

        Assert.Equal("## Tarefa atual\n\nNão há tarefa em execução nesta sessão.", result);
    }

    [Fact]
    public void DeleteBatchesCoverCompleteHistoryFromNewestToOldest()
    {
        var batches = TelegramInteractionHandler.BuildDeleteBatches(50, 275).ToArray();

        Assert.Equal(3, batches.Length);
        Assert.Equal(Enumerable.Range(176, 100), batches[0]);
        Assert.Equal(Enumerable.Range(76, 100), batches[1]);
        Assert.Equal(Enumerable.Range(50, 26), batches[2]);
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 100));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(20, 10)]
    public void DeleteBatchesIgnoreInvalidRanges(int firstMessageId, int latestMessageId)
    {
        Assert.Empty(TelegramInteractionHandler.BuildDeleteBatches(firstMessageId, latestMessageId));
    }
}
