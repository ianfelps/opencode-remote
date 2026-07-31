using System.Net;
using System.Text.RegularExpressions;

namespace OpencodeRemote.Telegram;

public static partial class TelegramTextFormatter
{
    private static readonly Regex FencedCode = new(@"```[^\r\n]*\r?\n([\s\S]*?)```", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`\r\n]+)`", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]\r\n]+)\]\((https?://[^\s)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Heading = new(@"^#{1,6}\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex BoldAsterisk = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldUnderscore = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex Strike = new(@"~~(.+?)~~", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-+]\s+", RegexOptions.Compiled | RegexOptions.Multiline);

    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "Concluído sem resposta textual.";
        }

        var protectedBlocks = new List<string>();
        var text = FencedCode.Replace(markdown, match => Protect(
            $"<pre><code>{WebUtility.HtmlEncode(match.Groups[1].Value.TrimEnd())}</code></pre>", protectedBlocks));
        text = InlineCode.Replace(text, match => Protect(
            $"<code>{WebUtility.HtmlEncode(match.Groups[1].Value)}</code>", protectedBlocks));
        text = WebUtility.HtmlEncode(text);
        text = Link.Replace(text, "<a href=\"$2\">$1</a>");
        text = Heading.Replace(text, "<b>$1</b>");
        text = BoldAsterisk.Replace(text, "<b>$1</b>");
        text = BoldUnderscore.Replace(text, "<b>$1</b>");
        text = Strike.Replace(text, "<s>$1</s>");
        text = Italic.Replace(text, "<i>$1</i>");
        text = Bullet.Replace(text, "• ");

        for (var index = 0; index < protectedBlocks.Count; index++)
        {
            text = text.Replace(Token(index), protectedBlocks[index], StringComparison.Ordinal);
        }

        return text;
    }

    private static string Protect(string html, List<string> blocks)
    {
        var token = Token(blocks.Count);
        blocks.Add(html);
        return token;
    }

    private static string Token(int index) => $"\uE000{index}\uE001";
}
