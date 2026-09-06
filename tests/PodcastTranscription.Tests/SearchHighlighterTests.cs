using PodcastTranscription.Web.Services.Search;

namespace PodcastTranscription.Tests;

public class SearchHighlighterTests
{
    private static string Snippet(string text) => text
        .Replace("[", SearchHighlighter.OpenSentinel)
        .Replace("]", SearchHighlighter.CloseSentinel);

    [Fact]
    public void Sentinels_become_mark_tags() =>
        Assert.Equal("we discussed <mark>pricing</mark> at length",
            SearchHighlighter.ToHtml(Snippet("we discussed [pricing] at length")));

    [Fact]
    public void Transcript_text_is_encoded_before_the_marks_go_in()
    {
        // Anything can be uploaded and transcribed, so the text itself is never trusted.
        var html = SearchHighlighter.ToHtml(Snippet("a <script>alert(1)</script> [tag]"));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<mark>tag</mark>", html);
    }

    [Fact]
    public void Ampersands_and_quotes_are_encoded()
    {
        var html = SearchHighlighter.ToHtml(Snippet("Tom & Jerry said \"[hi]\""));

        Assert.Contains("&amp;", html);
        Assert.Contains("<mark>hi</mark>", html);
    }

    [Fact]
    public void Plain_text_strips_the_sentinels() =>
        Assert.Equal("we discussed pricing",
            SearchHighlighter.ToPlainText(Snippet("we discussed [pricing]")));

    [Fact]
    public void Text_with_no_match_is_returned_encoded_and_unmarked() =>
        Assert.Equal("nothing to mark here", SearchHighlighter.ToHtml("nothing to mark here"));
}
