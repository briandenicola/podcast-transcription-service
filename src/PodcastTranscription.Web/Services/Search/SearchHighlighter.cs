using System.Net;
using System.Text;

namespace PodcastTranscription.Web.Services.Search;

/// <summary>
/// Renders an FTS5 snippet as safe HTML.
///
/// The snippet is transcript text — attacker-influenceable in the sense that anything at all can
/// be uploaded and transcribed — so it is HTML-encoded first, and only then are the sentinels
/// FTS5 wrapped the matches in turned into &lt;mark&gt; tags. Asking FTS5 for literal tags and
/// rendering the result unencoded would be an injection.
/// </summary>
public static class SearchHighlighter
{
    /// <summary>Control characters, so they cannot occur in the transcript text itself.</summary>
    public const string OpenSentinel = "\u0001";

    public const string CloseSentinel = "\u0002";

    public static string ToHtml(string snippet)
    {
        var encoded = WebUtility.HtmlEncode(snippet);

        return new StringBuilder(encoded)
            .Replace(OpenSentinel, "<mark>")
            .Replace(CloseSentinel, "</mark>")
            .ToString();
    }

    /// <summary>The snippet with the sentinels stripped, for places that want plain text.</summary>
    public static string ToPlainText(string snippet) =>
        snippet.Replace(OpenSentinel, string.Empty).Replace(CloseSentinel, string.Empty);
}
