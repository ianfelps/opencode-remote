using System.Text;
using OpencodeRemote.OpenCode.Models;

namespace OpencodeRemote.Sessions;

public static class SessionHistoryFormatter
{
    private const int MaximumMessageLength = 700;

    public static string Format(string title, IReadOnlyList<ConversationMessage> messages)
    {
        var result = new StringBuilder()
            .AppendLine("## Sessão selecionada")
            .AppendLine()
            .Append("**Título:** ")
            .Append(title)
            .AppendLine();

        if (messages.Count == 0)
        {
            return result.AppendLine().Append("Nenhum histórico textual encontrado.").ToString();
        }

        result.AppendLine().AppendLine("**Histórico recente:**");
        foreach (var message in messages)
        {
            var label = message.Role == "user" ? "Você" : "OpenCode";
            var text = message.Text.Length <= MaximumMessageLength
                ? message.Text
                : message.Text[..(MaximumMessageLength - 3)] + "...";
            result.AppendLine().Append("**").Append(label).AppendLine(":**").AppendLine(text);
        }

        return result.ToString().TrimEnd();
    }
}
