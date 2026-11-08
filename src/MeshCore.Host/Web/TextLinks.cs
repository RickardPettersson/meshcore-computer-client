using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.AspNetCore.Html;

namespace MeshCore.Host.Web;

/// <summary>
/// Turns web addresses in message text into links that open in a new tab. Everything else stays plain,
/// HTML-encoded text. Only <c>http://</c> and <c>https://</c> addresses become links (never <c>javascript:</c> and the like).
/// chat.js has the same rules for messages that arrive live (<c>linkify</c>); keep the two in step.
/// </summary>
public static partial class TextLinks
{
    /// <summary>Trailing characters that usually end the sentence, not the address ("see https://x.se/a.").</summary>
    private const string TrailingPunctuation = ".,;:!?'\"*";

    /// <summary>Leaves letters like å ä ö readable in the page source (emoji become &amp;#x…; entities, shown the same); still encodes &lt; &gt; &amp; and quotes.</summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    public static IHtmlContent Linkify(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return HtmlString.Empty;
        }

        var html = new StringBuilder(text.Length + 64);
        var last = 0;
        foreach (Match m in UrlRegex().Matches(text))
        {
            var url = TrimUrl(m.Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            {
                continue;
            }
            html.Append(Encoder.Encode(text[last..m.Index]));
            html.Append("<a href=\"").Append(Encoder.Encode(url))
                .Append("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\" class=\"app-link\">")
                .Append(Encoder.Encode(url)).Append("</a>");
            last = m.Index + url.Length;
        }
        html.Append(Encoder.Encode(text[last..]));
        return new HtmlString(html.ToString());
    }

    /// <summary>Drops trailing punctuation, and a closing bracket that has no opening one inside the address.</summary>
    internal static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            var c = url[^1];
            if (TrailingPunctuation.Contains(c, StringComparison.Ordinal)
                || (c == ')' && url.Count(ch => ch == '(') < url.Count(ch => ch == ')'))
                || (c == ']' && url.Count(ch => ch == '[') < url.Count(ch => ch == ']')))
            {
                url = url[..^1];
                continue;
            }
            break;
        }
        return url;
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
