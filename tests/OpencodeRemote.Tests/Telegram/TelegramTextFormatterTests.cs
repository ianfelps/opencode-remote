namespace OpencodeRemote.Tests.Telegram;

public sealed class TelegramTextFormatterTests
{
    [Fact]
    public void ConvertsCommonMarkdownToTelegramHtml()
    {
        var result = TelegramTextFormatter.ToHtml("## Título\n**forte** e *itálico*\n- item\n`código`");

        Assert.Equal("<b>T&#237;tulo</b>\n<b>forte</b> e <i>it&#225;lico</i>\n• item\n<code>c&#243;digo</code>", result);
    }

    [Fact]
    public void EscapesRawHtmlButPreservesCodeBlocks()
    {
        var result = TelegramTextFormatter.ToHtml("<script>x</script>\n```csharp\nvar x = 1 < 2;\n```");

        Assert.Equal("&lt;script&gt;x&lt;/script&gt;\n<pre><code>var x = 1 &lt; 2;</code></pre>", result);
    }

    [Fact]
    public void ConvertsLinks()
    {
        var result = TelegramTextFormatter.ToHtml("Veja [OpenCode](https://opencode.ai/docs?a=1&b=2).");

        Assert.Equal("Veja <a href=\"https://opencode.ai/docs?a=1&amp;b=2\">OpenCode</a>.", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void EmptyContentGetsFallbackMessage(string markdown)
    {
        Assert.Equal("Concluído sem resposta textual.", TelegramTextFormatter.ToHtml(markdown));
    }

    [Fact]
    public void MarkdownInsideCodeIsNotFormatted()
    {
        var result = TelegramTextFormatter.ToHtml("`**bold** & <tag>`\n```text\n*italic* & <tag>\n```");

        Assert.Equal("<code>**bold** &amp; &lt;tag&gt;</code>\n<pre><code>*italic* &amp; &lt;tag&gt;</code></pre>", result);
    }

    [Fact]
    public void ConvertsAlternativeFormattingAndBullets()
    {
        var result = TelegramTextFormatter.ToHtml("__bold__ and ~~removed~~\n  + item");

        Assert.Equal("<b>bold</b> and <s>removed</s>\n• item", result);
    }
}
