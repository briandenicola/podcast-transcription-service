using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Tests;

public class PodcastUrlResolverTests
{
    [Theory]
    [InlineData("https://podcasts.apple.com/us/podcast/pillow-talks/id1569466131")]
    [InlineData("https://podcasts.apple.com/gb/podcast/the-rest-is-history/id1537788786?i=1000650000000")]
    [InlineData("http://itunes.apple.com/us/podcast/x/id123")]
    public void An_apple_podcasts_link_is_recognised(string url) =>
        Assert.True(PodcastUrlResolver.IsApplePodcastsUrl(url));

    [Theory]
    [InlineData("https://example.com/feed.xml")]
    [InlineData("https://feeds.megaphone.fm/abc123")]
    [InlineData("https://notapple.com/podcast/id123")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_left_alone(string? url) =>
        Assert.False(PodcastUrlResolver.IsApplePodcastsUrl(url));

    /// <summary>The host check must not be fooled by a lookalike domain.</summary>
    [Theory]
    [InlineData("https://apple.com.evil.test/podcast/id1")]
    [InlineData("https://podcasts.apple.com.attacker.test/id1")]
    public void A_lookalike_domain_is_not_treated_as_apple(string url) =>
        Assert.False(PodcastUrlResolver.IsApplePodcastsUrl(url));

    [Theory]
    [InlineData("https://podcasts.apple.com/us/podcast/pillow-talks/id1569466131", "1569466131")]
    [InlineData("https://podcasts.apple.com/gb/podcast/x/id123?i=999", "123")]
    public void The_show_id_is_pulled_out_of_the_link(string url, string expected) =>
        Assert.Equal(expected, PodcastUrlResolver.ExtractAppleId(url));

    [Fact]
    public void A_link_with_no_show_id_yields_nothing() =>
        Assert.Null(PodcastUrlResolver.ExtractAppleId("https://podcasts.apple.com/us/browse"));

    [Fact]
    public void The_feed_url_is_read_from_the_lookup_response()
    {
        var json = """
        {"resultCount":1,"results":[{"collectionName":"Pillow Talks",
         "feedUrl":"https://feeds.megaphone.fm/pillowtalks","kind":"podcast"}]}
        """;

        Assert.Equal("https://feeds.megaphone.fm/pillowtalks", PodcastUrlResolver.ReadFeedUrl(json));
    }

    [Fact]
    public void The_first_result_carrying_a_feed_url_wins()
    {
        // Lookup sometimes returns the show plus individual episodes; only the show has a feed.
        var json = """
        {"resultCount":2,"results":[{"kind":"podcast-episode"},
         {"kind":"podcast","feedUrl":"https://example.com/rss"}]}
        """;

        Assert.Equal("https://example.com/rss", PodcastUrlResolver.ReadFeedUrl(json));
    }

    [Theory]
    [InlineData("""{"resultCount":0,"results":[]}""")]
    [InlineData("""{"results":[{"collectionName":"No feed here"}]}""")]
    [InlineData("""{"unexpected":"shape"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void A_response_with_no_feed_url_yields_nothing(string json) =>
        Assert.Null(PodcastUrlResolver.ReadFeedUrl(json));

    [Fact]
    public async Task A_plain_feed_url_passes_through_untouched()
    {
        var http = new FakeHttpClientFactory(() => "");
        var resolver = new PodcastUrlResolver(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PodcastUrlResolver>.Instance);

        Assert.Equal("https://example.com/feed.xml",
            await resolver.ResolveAsync("  https://example.com/feed.xml  "));

        // No lookup call was needed.
        Assert.Empty(http.RequestedUrls);
    }

    [Fact]
    public async Task An_apple_link_is_exchanged_for_the_real_feed()
    {
        var http = new FakeHttpClientFactory(() =>
            """{"resultCount":1,"results":[{"feedUrl":"https://feeds.megaphone.fm/pillowtalks"}]}""");
        var resolver = new PodcastUrlResolver(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<PodcastUrlResolver>.Instance);

        var resolved = await resolver.ResolveAsync("https://podcasts.apple.com/us/podcast/pillow-talks/id1569466131");

        Assert.Equal("https://feeds.megaphone.fm/pillowtalks", resolved);
        Assert.Contains("itunes.apple.com/lookup?id=1569466131", http.RequestedUrls.Single());
    }

    [Fact]
    public async Task An_apple_link_without_an_id_explains_what_to_do()
    {
        var resolver = new PodcastUrlResolver(new FakeHttpClientFactory(() => ""),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PodcastUrlResolver>.Instance);

        var ex = await Assert.ThrowsAsync<IngestException>(
            () => resolver.ResolveAsync("https://podcasts.apple.com/us/browse"));

        Assert.Contains("id1569466131", ex.Message);
    }

    [Fact]
    public async Task A_show_apple_has_no_feed_for_says_so()
    {
        var resolver = new PodcastUrlResolver(
            new FakeHttpClientFactory(() => """{"resultCount":0,"results":[]}"""),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PodcastUrlResolver>.Instance);

        var ex = await Assert.ThrowsAsync<IngestException>(
            () => resolver.ResolveAsync("https://podcasts.apple.com/us/podcast/x/id123"));

        Assert.Contains("does not publish an RSS URL", ex.Message);
    }
}
