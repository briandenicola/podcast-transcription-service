using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Tests;

public class FeedParserTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>The Build Log</title>
            <item>
              <title>Ep. 12 — Pricing</title>
              <guid isPermaLink="false">tag:example.com,2026:12</guid>
              <pubDate>Tue, 03 Mar 2026 09:00:00 +0000</pubDate>
              <itunes:duration>1:02:03</itunes:duration>
              <enclosure url="https://example.com/audio/12.mp3" length="42" type="audio/mpeg"/>
            </item>
            <item>
              <title>Ep. 11 — Kubernetes</title>
              <pubDate>Tue, 24 Feb 2026 09:00:00 GMT</pubDate>
              <itunes:duration>2705</itunes:duration>
              <enclosure url="https://example.com/audio/11.mp3" type="audio/mpeg"/>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void The_channel_title_becomes_the_feed_title() =>
        Assert.Equal("The Build Log", FeedParser.Parse(Feed).Title);

    [Fact]
    public void Items_carry_their_audio_url_and_metadata()
    {
        var item = FeedParser.Parse(Feed).Items[0];

        Assert.Equal("Ep. 12 — Pricing", item.Title);
        Assert.Equal("https://example.com/audio/12.mp3", item.AudioUrl);
        Assert.Equal("tag:example.com,2026:12", item.Guid);
        Assert.Equal(3723, item.DurationSec);
        Assert.Equal(new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero), item.PublishedAt);
    }

    [Fact]
    public void An_item_without_a_guid_falls_back_to_the_enclosure_url()
    {
        // Without this, a feed omitting <guid> would re-add its episodes on every poll.
        var item = FeedParser.Parse(Feed).Items[1];

        Assert.Equal("https://example.com/audio/11.mp3", item.Guid);
    }

    [Fact]
    public void Items_with_no_enclosure_are_skipped()
    {
        // A trailer or a text-only post has nothing to transcribe.
        var parsed = FeedParser.Parse("""
            <rss version="2.0"><channel>
              <title>Mixed</title>
              <item><title>No audio here</title></item>
              <item><title>Real</title><enclosure url="https://example.com/a.mp3"/></item>
            </channel></rss>
            """);

        Assert.Single(parsed.Items);
        Assert.Equal("Real", parsed.Items[0].Title);
    }

    [Fact]
    public void A_feed_with_no_channel_is_rejected() =>
        Assert.Throws<IngestException>(() => FeedParser.Parse("<html><body>not a feed</body></html>"));

    [Fact]
    public void Malformed_xml_is_rejected_with_a_readable_message()
    {
        var ex = Assert.Throws<IngestException>(() => FeedParser.Parse("<rss><channel><title>Broken"));

        Assert.Contains("does not parse as XML", ex.Message);
    }

    /// <summary>
    /// Feeds come from wherever a user pointed us. A DTD is the classic way to turn XML parsing
    /// into resource exhaustion or a file read, so the parser must refuse rather than expand it.
    /// </summary>
    [Fact]
    public void A_document_declaring_a_dtd_is_refused()
    {
        var billionLaughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
            ]>
            <rss version="2.0"><channel><title>&lol2;</title></channel></rss>
            """;

        Assert.Throws<IngestException>(() => FeedParser.Parse(billionLaughs));
    }

    [Fact]
    public void An_external_entity_is_refused_rather_than_resolved()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <rss version="2.0"><channel><title>&xxe;</title></channel></rss>
            """;

        Assert.Throws<IngestException>(() => FeedParser.Parse(xxe));
    }

    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("42:10", 2530)]
    [InlineData("2705", 2705)]
    [InlineData("00:30", 30)]
    public void Itunes_duration_is_read_in_all_the_shapes_publishers_use(string input, double expected) =>
        Assert.Equal(expected, FeedParser.ParseDuration(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about an hour")]
    public void An_unreadable_duration_is_null_rather_than_wrong(string? input) =>
        Assert.Null(FeedParser.ParseDuration(input));

    [Theory]
    [InlineData("Tue, 03 Mar 2026 09:00:00 +0000")]
    [InlineData("Tue, 03 Mar 2026 09:00:00 GMT")]
    [InlineData("2026-03-03T09:00:00Z")]
    public void Publication_dates_are_read_in_the_formats_feeds_actually_use(string input) =>
        Assert.Equal(new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero), FeedParser.ParseDate(input));

    [Fact]
    public void An_unparseable_date_is_null_rather_than_today() =>
        Assert.Null(FeedParser.ParseDate("last Tuesday"));
}
