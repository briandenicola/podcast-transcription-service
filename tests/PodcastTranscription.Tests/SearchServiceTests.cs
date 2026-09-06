using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Search;

namespace PodcastTranscription.Tests;

/// <summary>
/// Search against a migrated database, so the FTS5 table and its triggers are the real ones.
/// The triggers are the part worth proving: they run inside SQLite, so nothing in application
/// code would notice if they stopped firing on EF's inserts.
/// </summary>
public class SearchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SearchServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private SearchService CreateService(AppDbContext db) => new(db, NullLogger<SearchService>.Instance);

    private async Task<Episode> SeedAsync(AppDbContext db, string title, string? show, params string[] lines)
    {
        var episode = new Episode
        {
            Title = title,
            Show = show,
            AudioPath = $"source/{Guid.NewGuid():N}.mp3",
            AudioSha256 = Guid.NewGuid().ToString("N")
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var transcript = new Transcript { EpisodeId = episode.Id, Model = "large-v3-turbo-q5_0", Language = "en" };
        db.Transcripts.Add(transcript);
        await db.SaveChangesAsync();

        for (var i = 0; i < lines.Length; i++)
        {
            db.Segments.Add(new Segment
            {
                TranscriptId = transcript.Id,
                Ordinal = i,
                StartMs = i * 10_000,
                EndMs = (i * 10_000) + 9_000,
                Text = lines[i]
            });
        }

        await db.SaveChangesAsync();
        return episode;
    }

    [Fact]
    public async Task Segments_inserted_through_ef_are_indexed_by_the_trigger()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Pricing episode", "Business", "We should talk about pricing today.");

        var results = await CreateService(db).SearchAsync("pricing");

        Assert.Equal(1, results.TotalHits);
        Assert.Equal("Pricing episode", results.Episodes[0].Title);
    }

    [Fact]
    public async Task A_hit_carries_the_timestamp_to_jump_to()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Timestamps", null, "First line.", "The word appears here.", "Third line.");

        var results = await CreateService(db).SearchAsync("appears");

        var hit = Assert.Single(results.Episodes[0].Hits);
        Assert.Equal(10_000, hit.StartMs);
    }

    [Fact]
    public async Task Matches_come_back_wrapped_in_the_sentinels()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Snippets", null, "We should talk about pricing today.");

        var results = await CreateService(db).SearchAsync("pricing");

        var html = SearchHighlighter.ToHtml(results.Episodes[0].Hits[0].Snippet);
        Assert.Contains("<mark>pricing</mark>", html);
    }

    [Fact]
    public async Task Results_are_grouped_by_episode()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Episode one", "Show A", "Pricing came up.", "Pricing again.");
        await SeedAsync(db, "Episode two", "Show B", "A single mention of pricing.");

        var results = await CreateService(db).SearchAsync("pricing");

        Assert.Equal(3, results.TotalHits);
        Assert.Equal(2, results.Episodes.Count);
        Assert.Equal(2, results.Episodes.Single(e => e.Title == "Episode one").Hits.Count);
    }

    [Fact]
    public async Task Hits_within_an_episode_are_in_playback_order()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Ordering", null, "Pricing first.", "Nothing here.", "Pricing last.");

        var results = await CreateService(db).SearchAsync("pricing");

        var starts = results.Episodes[0].Hits.Select(h => h.StartMs).ToList();
        Assert.Equal(starts.OrderBy(s => s), starts);
    }

    [Fact]
    public async Task The_porter_stemmer_matches_across_word_forms()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Stemming", null, "They were running late.");

        var results = await CreateService(db).SearchAsync("\"run\"");

        Assert.Equal(1, results.TotalHits);
    }

    [Fact]
    public async Task Editing_a_segment_updates_the_index()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Corrections", null, "We discussed kubernetes.");

        var segment = await db.Segments.SingleAsync();
        segment.Text = "We discussed observability.";
        segment.IsEdited = true;
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // The stale term is gone and the new one is findable.
        Assert.Equal(0, (await service.SearchAsync("kubernetes")).TotalHits);
        Assert.Equal(1, (await service.SearchAsync("observability")).TotalHits);
    }

    [Fact]
    public async Task Deleting_a_transcript_removes_its_segments_from_the_index()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Removal", null, "A mention of pricing.");

        db.Segments.RemoveRange(await db.Segments.ToListAsync());
        await db.SaveChangesAsync();

        Assert.Equal(0, (await CreateService(db).SearchAsync("pricing")).TotalHits);
    }

    [Fact]
    public async Task An_empty_query_searches_for_nothing()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Anything", null, "Some words.");

        Assert.Equal(0, (await CreateService(db).SearchAsync("   ")).TotalHits);
    }

    [Fact]
    public async Task A_phrase_only_matches_the_words_in_that_order()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "Phrases", null, "machine learning is the topic", "learning machine parts");

        var results = await CreateService(db).SearchAsync("\"machine learning\"");

        Assert.Equal(1, results.TotalHits);
        Assert.Equal(0, results.Episodes[0].Hits[0].StartMs);
    }

    [Fact]
    public async Task Shows_are_listed_for_the_library_filter()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, "One", "Show B", "Text.");
        await SeedAsync(db, "Two", "Show A", "Text.");
        await SeedAsync(db, "Three", null, "Text.");

        Assert.Equal(["Show A", "Show B"], await CreateService(db).GetShowsAsync());
    }
}
