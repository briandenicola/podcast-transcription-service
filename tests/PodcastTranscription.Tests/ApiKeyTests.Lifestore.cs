using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Tests;

/// <summary>The life store feed: every episode, paged by id, with its transcript as passages.</summary>
public partial class ApiKeyTests
{
    private async Task<JsonElement> ReadFeed(string query = "")
        => await ReadJson("/api/lifestore/records" + query);

    [Fact]
    public async Task Feed_requires_a_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetReadRoute("/api/lifestore/records", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetReadRoute("/api/lifestore/records", "invalid")).StatusCode);
    }

    [Fact]
    public async Task Feed_lists_every_episode_with_its_latest_complete_transcript_and_summary()
    {
        await WithStore(async (_, db) =>
        {
            db.Feeds.Add(new Feed { Title = "Acquired", RssUrl = "https://example.com/rss" });
            await db.SaveChangesAsync();
            db.Episodes.Add(new Episode { Title = "Costco", Show = "Acquired", FeedId = db.Feeds.Single().Id, PublishedAt = ReadTestDate, DurationSec = 7200 });
            await db.SaveChangesAsync();
            return true;
        });
        await SeedTranscript(2, 0, true, "Old summary", "Old line.");
        await SeedTranscript(2, 1, true, "Costco sells memberships.", "Welcome to the show.", "Today: Costco.");
        await SeedTranscript(2, 2, false, null, "A partial re-run.");

        var body = await ReadFeed();

        Assert.Equal("podcasttranscription.lifestore.records/1", body.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("next").ValueKind);
        var records = body.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(["1", "2"], records.Select(r => r.GetProperty("id").GetString()));
        Assert.All(records, r => Assert.Equal("episode", r.GetProperty("kind").GetString()));

        // The seeded episode with no transcript is still listed.
        Assert.Equal(JsonValueKind.Null, records[0].GetProperty("data").GetProperty("transcript").ValueKind);

        var costco = records[1].GetProperty("data");
        Assert.Equal("Acquired", costco.GetProperty("feed").GetString());
        var transcript = costco.GetProperty("transcript");
        Assert.Equal("transcription-1", transcript.GetProperty("model").GetString());
        Assert.Equal("Costco sells memberships.", transcript.GetProperty("summary").GetProperty("content").GetString());
        var passage = Assert.Single(transcript.GetProperty("passages").EnumerateArray());
        Assert.Equal("Welcome to the show. Today: Costco.", passage.GetProperty("text").GetString());
        Assert.Equal(0, passage.GetProperty("startMs").GetInt32());
        Assert.Equal(19_000, passage.GetProperty("endMs").GetInt32());
    }

    [Fact]
    public async Task Feed_pages_follow_next_and_stop()
    {
        await SeedManyEpisodes();

        var seen = new List<string>();
        string? next = null;
        var pages = 0;
        do
        {
            var body = await ReadFeed(next is null ? "?limit=25" : $"?limit=25&page={next}");
            seen.AddRange(body.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetString()!));
            next = body.GetProperty("next").ValueKind == JsonValueKind.Null ? null : body.GetProperty("next").GetString();
            pages++;
        } while (next is not null);

        Assert.Equal(2, pages);
        Assert.Equal(Enumerable.Range(1, 36).Select(i => i.ToString()), seen);
    }

    [Theory]
    [InlineData("?limit=1000", 25)]
    [InlineData("?limit=0", 1)]
    [InlineData("", 10)]
    public async Task Feed_clamps_the_limit_to_what_a_page_holds(string query, int expected)
    {
        await SeedManyEpisodes();
        Assert.Equal(expected, (await ReadFeed(query)).GetProperty("records").GetArrayLength());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("episode:")]
    [InlineData("episode:0")]
    [InlineData("episode:-3")]
    [InlineData("coin:3")]
    public async Task Feed_refuses_a_page_token_it_did_not_give_out(string token)
    {
        var response = await GetReadRoute($"/api/lifestore/records?page={Uri.EscapeDataString(token)}", await CreateKey(_viewerId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Start again without a page", await response.Content.ReadAsStringAsync());
    }
}
