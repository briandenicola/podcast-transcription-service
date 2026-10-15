using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Tests;

public partial class ApiKeyTests
{
    private static readonly DateTimeOffset ReadTestDate = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task SeedTranscript(int episodeId, int day, bool complete, string? summary, params string[] lines)
    {
        await WithStore(async (_, db) =>
        {
            db.Transcripts.Add(new Transcript
            {
                EpisodeId = episodeId,
                CreatedAt = ReadTestDate.AddDays(day),
                IsComplete = complete,
                Model = $"transcription-{day}",
                Summary = summary is null ? null : new Summary { Content = summary, Model = $"summary-{day}" },
                Segments = lines.Select((text, ordinal) => new Segment
                {
                    Ordinal = ordinal,
                    StartMs = ordinal * 10_000,
                    EndMs = ordinal * 10_000 + 9_000,
                    Text = text
                }).Reverse().ToList()
            });
            await db.SaveChangesAsync();
            return true;
        });
    }

    private Task<HttpResponseMessage> GetReadRoute(string route, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (key is not null)
        {
            request.Headers.Add("X-API-Key", key);
        }
        return _client.SendAsync(request);
    }

    private async Task<JsonElement> ReadJson(string route)
    {
        var response = await GetReadRoute(route, await CreateKey(_viewerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("/api/episodes")]
    [InlineData("/api/episodes/1/transcript")]
    [InlineData("/api/search?q=pricing")]
    public async Task New_read_routes_require_a_key_and_accept_read_only_and_shared_keys(string route)
    {
        await SeedTranscript(1, 0, true, "Summary", "Pricing is the topic.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetReadRoute(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetReadRoute(route, "invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetReadRoute(route, "legacy-shared-key")).StatusCode);
        var key = await CreateKey(_viewerId);
        Assert.Equal(HttpStatusCode.OK, (await GetReadRoute(route, key)).StatusCode);
        var viewer = await Principal(_viewerId);
        await WithStore(async (keys, db) =>
            await keys.RevokeAsync(viewer, (await db.ApiKeys.SingleAsync()).Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetReadRoute(route, key)).StatusCode);
    }

    private Task<bool> SeedManyEpisodes() => WithStore(async (_, db) =>
    {
        db.Episodes.AddRange(Enumerable.Range(1, 35).Select(day => new Episode
        {
            Title = $"Episode {day}",
            Show = day % 2 == 0 ? "Show A" : "Show AB",
            PublishedAt = ReadTestDate.AddDays(day)
        }));
        await db.SaveChangesAsync();
        return true;
    });

    [Fact]
    public async Task Recent_episodes_default_to_30_items()
    {
        await SeedManyEpisodes();
        var body = await ReadJson("/api/episodes");
        Assert.Equal(30, body.GetArrayLength());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100)]
    public async Task Recent_episodes_respect_the_requested_limit(int limit)
    {
        await SeedManyEpisodes();
        Assert.Equal(Math.Min(limit, 36), (await ReadJson($"/api/episodes?limit={limit}")).GetArrayLength());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("abc")]
    public async Task Recent_episodes_reject_invalid_limits(string limit)
    {
        var response = await GetReadRoute($"/api/episodes?limit={limit}", await CreateKey(_viewerId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recent_episodes_filter_by_exact_show_and_empty_show_means_all()
    {
        await SeedManyEpisodes();
        var filtered = await ReadJson("/api/episodes?limit=100&show=Show%20A");
        Assert.Equal(17, filtered.GetArrayLength());
        Assert.All(filtered.EnumerateArray(), e => Assert.Equal("Show A", e.GetProperty("show").GetString()));
        Assert.Equal(36, (await ReadJson("/api/episodes?limit=100&show=")).GetArrayLength());
        Assert.Equal(0, (await ReadJson("/api/episodes?show=missing")).GetArrayLength());
        Assert.Equal(0, (await ReadJson("/api/episodes?show=%27%20OR%201%3D1--")).GetArrayLength());
    }

    [Fact]
    public async Task Recent_episodes_order_by_publication_with_creation_fallback_and_stable_ties()
    {
        await WithStore(async (_, db) =>
        {
            db.Episodes.AddRange(
                new Episode { Title = "Old publication", PublishedAt = ReadTestDate, CreatedAt = ReadTestDate.AddDays(20) },
                new Episode { Title = "Creation fallback", CreatedAt = ReadTestDate.AddDays(1) },
                new Episode { Title = "Tie first", PublishedAt = ReadTestDate.AddDays(2) },
                new Episode { Title = "Tie second", PublishedAt = ReadTestDate.AddDays(2) });
            await db.SaveChangesAsync();
            return true;
        });
        var recent = await ReadJson("/api/episodes?limit=4");
        Assert.Equal(new[] { "Tie second", "Tie first", "Creation fallback", "Old publication" },
            recent.EnumerateArray().Select(e => e.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Recent_episodes_include_latest_job_and_300_character_completed_summary_preview()
    {
        await WithStore(async (_, db) =>
        {
            var episode = await db.Episodes.SingleAsync();
            episode.Show = "The show";
            episode.PublishedAt = ReadTestDate;
            episode.DurationSec = 123.5;
            db.Jobs.AddRange(
                new Job { EpisodeId = 1, State = JobState.Failed, Progress = 0.1, CreatedAt = ReadTestDate },
                new Job { EpisodeId = 1, State = JobState.Transcribing, Progress = 0.75, CreatedAt = ReadTestDate });
            await db.SaveChangesAsync();
            return true;
        });
        await SeedTranscript(1, 0, true, "Old summary", "Old transcript");
        await SeedTranscript(1, 1, true, new string('x', 350), "Current transcript");
        await SeedTranscript(1, 2, false, "Partial summary", "Partial transcript");
        var episodeJson = (await ReadJson("/api/episodes"))[0];
        Assert.Equal(new[] { "id", "title", "show", "publishedAt", "durationSec", "latestJob", "summaryPreview" },
            episodeJson.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, episodeJson.GetProperty("id").GetInt32());
        Assert.Equal("Existing episode", episodeJson.GetProperty("title").GetString());
        Assert.Equal("The show", episodeJson.GetProperty("show").GetString());
        Assert.Equal(ReadTestDate, episodeJson.GetProperty("publishedAt").GetDateTimeOffset());
        Assert.Equal(123.5, episodeJson.GetProperty("durationSec").GetDouble());
        Assert.Equal(new string('x', 300), episodeJson.GetProperty("summaryPreview").GetString());
        Assert.Equal("Transcribing", episodeJson.GetProperty("latestJob").GetProperty("state").GetString());
        Assert.Equal(0.75, episodeJson.GetProperty("latestJob").GetProperty("progress").GetDouble());
    }

    [Fact]
    public async Task Summary_previews_are_truncated_at_300_characters()
    {
        await SeedTranscript(1, 0, true, new string('x', 350), "Text");
        Assert.Equal(new string('x', 300),
            (await ReadJson("/api/episodes"))[0].GetProperty("summaryPreview").GetString());
    }

    [Fact]
    public async Task Missing_jobs_and_summaries_are_null_and_short_previews_are_not_padded()
    {
        var episode = (await ReadJson("/api/episodes"))[0];
        Assert.Equal(JsonValueKind.Null, episode.GetProperty("latestJob").ValueKind);
        Assert.Equal(JsonValueKind.Null, episode.GetProperty("summaryPreview").ValueKind);
        await SeedTranscript(1, 0, true, "Short summary", "Text");
        Assert.Equal("Short summary", (await ReadJson("/api/episodes"))[0].GetProperty("summaryPreview").GetString());
    }

    [Fact]
    public async Task Transcript_returns_latest_completed_segments_in_order_and_its_summary_markdown_and_model()
    {
        await SeedTranscript(1, 0, true, "Old summary", "Old text");
        await SeedTranscript(1, 1, true, "# Summary\n\n**Markdown**", "First line", "Second line");
        await SeedTranscript(1, 2, false, "Partial summary", "Partial text");
        var body = await ReadJson("/api/episodes/1/transcript");
        Assert.Equal(new[] { "segments", "summary" }, body.EnumerateObject().Select(p => p.Name));
        var segments = body.GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());
        Assert.Equal(new[] { "startMs", "endMs", "text" }, segments[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("First line", segments[0].GetProperty("text").GetString());
        Assert.Equal(0, segments[0].GetProperty("startMs").GetInt32());
        Assert.Equal(9000, segments[0].GetProperty("endMs").GetInt32());
        Assert.Equal("Second line", segments[1].GetProperty("text").GetString());
        Assert.Equal(10000, segments[1].GetProperty("startMs").GetInt32());
        Assert.Equal(19000, segments[1].GetProperty("endMs").GetInt32());
        Assert.Equal("# Summary\n\n**Markdown**", body.GetProperty("summary").GetProperty("content").GetString());
        Assert.Equal("summary-1", body.GetProperty("summary").GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task Missing_episodes_or_completed_transcripts_return_404(int id)
    {
        await SeedTranscript(1, 0, false, null, "Partial text");
        Assert.Equal(HttpStatusCode.NotFound,
            (await GetReadRoute($"/api/episodes/{id}/transcript", await CreateKey(_viewerId))).StatusCode);
    }

    [Fact]
    public async Task Latest_transcript_without_a_summary_does_not_reuse_an_older_summary()
    {
        await SeedTranscript(1, 0, true, "Outdated", "Old");
        await SeedTranscript(1, 1, true, null);
        var body = await ReadJson("/api/episodes/1/transcript");
        Assert.Equal(0, body.GetProperty("segments").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ReadJson("/api/episodes"))[0].GetProperty("summaryPreview").ValueKind);
    }

    [Fact]
    public async Task Search_returns_plain_excerpts_and_playback_timestamps_in_the_requested_shape()
    {
        await SeedTranscript(1, 0, true, null, "Opening line.", "Pricing <sample> matters.");
        var body = await ReadJson("/api/search?q=pricing");
        Assert.Equal(1, body.GetArrayLength());
        var hit = body[0];
        Assert.Equal(new[] { "episodeId", "title", "startMs", "excerpt" }, hit.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, hit.GetProperty("episodeId").GetInt32());
        Assert.Equal("Existing episode", hit.GetProperty("title").GetString());
        Assert.Equal(10000, hit.GetProperty("startMs").GetInt32());
        Assert.Equal("Pricing <sample> matters.", hit.GetProperty("excerpt").GetString());
        Assert.Equal(0, (await ReadJson("/api/search?q=nonexistent")).GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=%20%20")]
    [InlineData("?q=***")]
    public async Task Search_rejects_missing_blank_or_meaningless_queries(string query)
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await GetReadRoute("/api/search" + query, await CreateKey(_viewerId))).StatusCode);
    }

    [Fact]
    public async Task Search_limits_hits_to_100_and_keeps_excerpts_short()
    {
        await SeedTranscript(1, 0, true, null,
            Enumerable.Range(0, 105).Select(_ => string.Join(" ", Enumerable.Repeat("pricing topic", 80))).ToArray());
        var body = await ReadJson("/api/search?q=pricing");
        Assert.Equal(100, body.GetArrayLength());
        Assert.All(body.EnumerateArray(), hit => Assert.True(hit.GetProperty("excerpt").GetString()!.Length < 200));
    }

    [Fact]
    public async Task Search_database_failures_are_not_reported_as_empty_successful_results()
    {
        await WithStore(async (_, db) =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE SegmentSearch;");
            return true;
        });
        Assert.Equal(HttpStatusCode.InternalServerError,
            (await GetReadRoute("/api/search?q=pricing", await CreateKey(_viewerId))).StatusCode);
    }
}
