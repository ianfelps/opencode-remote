namespace OpencodeRemote.Tests.Sessions;

public sealed class SessionHistoryFormatterTests
{
    [Fact]
    public void FormatsRecentConversationWithRoleLabels()
    {
        var messages = new[]
        {
            new ConversationMessage("user", "Como funciona?"),
            new ConversationMessage("assistant", "Funciona assim."),
        };

        var result = SessionHistoryFormatter.Format("Minha sessão", messages);

        Assert.Contains("## Sessão selecionada", result);
        Assert.Contains("**Título:** Minha sessão", result);
        Assert.Contains($"**Você:**{Environment.NewLine}Como funciona?", result);
        Assert.Contains($"**OpenCode:**{Environment.NewLine}Funciona assim.", result);
    }

    [Fact]
    public void ReportsWhenSessionHasNoTextHistory()
    {
        var result = SessionHistoryFormatter.Format("Vazia", []);

        Assert.Contains("Nenhum histórico textual encontrado.", result);
    }

    [Fact]
    public void TruncatesLongMessages()
    {
        var result = SessionHistoryFormatter.Format("Longa", [new ConversationMessage("assistant", new string('x', 900))]);

        Assert.True(result.Length < 800);
        Assert.EndsWith("...", result);
    }
}
