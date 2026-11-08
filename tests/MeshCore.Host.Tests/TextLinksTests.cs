using MeshCore.Data;
using MeshCore.Protocol;
using MeshCore.Host.Web;
using Microsoft.Extensions.DependencyInjection;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Host.Tests;

public class TextLinksTests
{
    private static string Html(string text) => TextLinks.Linkify(text).ToString()!;

    private static string Link(string url) =>
        $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer nofollow\" class=\"app-link\">{url}</a>";

    [Fact]
    public void Web_addresses_become_links_that_open_in_a_new_tab()
    {
        Assert.Equal($"Look at this: {Link("https://litter.catbox.moe/a1b2c3.jpg")}", Html("Look at this: https://litter.catbox.moe/a1b2c3.jpg"));
        Assert.Equal($"{Link("http://192.168.1.73:5080/Device")} and {Link("https://meshcore.io")}",
            Html("http://192.168.1.73:5080/Device and https://meshcore.io"));
    }

    [Theory]
    [InlineData("See https://meshcore.io.", "https://meshcore.io", ".")]
    [InlineData("Is it https://meshcore.io?", "https://meshcore.io", "?")]
    [InlineData("(more at https://meshcore.io/faq)", "https://meshcore.io/faq", ")")]
    [InlineData("https://en.wikipedia.org/wiki/LoRa_(radio)!", "https://en.wikipedia.org/wiki/LoRa_(radio)", "!")]
    [InlineData("\"https://meshcore.io/a\"", "https://meshcore.io/a", "\"")]
    public void Sentence_punctuation_after_the_address_stays_outside_the_link(string text, string url, string after) =>
        Assert.EndsWith(Link(url) + System.Net.WebUtility.HtmlEncode(after), Html(text), StringComparison.Ordinal);

    [Fact]
    public void Query_strings_and_swedish_letters_are_kept_and_encoded()
    {
        var html = Html("Karta: https://maps.example.se/?q=Göteborg&z=12 åäö 👋");

        Assert.Contains("href=\"https://maps.example.se/?q=Göteborg&amp;z=12\"", html, StringComparison.Ordinal);
        Assert.EndsWith(" åäö &#x1F44B;", html, StringComparison.Ordinal); // the browser shows 👋
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("data:text/html,<b>hi</b>")]
    public void Nothing_else_becomes_a_link_or_markup(string text)
    {
        var html = Html(text);

        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_and_tags_inside_an_address_cant_break_out_of_the_link()
    {
        var html = Html("https://evil.example/\"onmouseover=alert(1)<img src=x>");

        Assert.StartsWith("<a href=\"https://evil.example/\" ", html, StringComparison.Ordinal); // stops at the quote
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&quot;onmouseover=alert(1)&lt;img src=x&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_and_empty_messages_are_just_encoded() =>
        Assert.Equal(("a &lt; b &amp; c", ""), (Html("a < b & c"), Html("")));
}

public class MessageLinkPageTests
{
    [Fact]
    public async Task Chat_page_shows_links_in_messages()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();
        var store = app.Services.GetRequiredService<MeshStore>();
        await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, "Alice: Photo from the hill: https://litter.catbox.moe/a1b2c3.jpg <b>!</b>")));

        var chat = await browser.PageAsync("/Messages/Channel/0");

        Assert.Contains("Photo from the hill: <a href=\"https://litter.catbox.moe/a1b2c3.jpg\" target=\"_blank\" rel=\"noopener noreferrer nofollow\"",
            chat, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;!&lt;/b&gt;", chat, StringComparison.Ordinal);
    }
}
