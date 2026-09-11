using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

public class SummaryServiceTests : IDisposable
{
    private const string Tags = """{"models":[{"name":"llama3.1:8b"}]}""";

    private static string Answer(string text) => JsonSerializer.Serialize(new
    {
        model = "llama3.1:8b",
        response = text,
        done = true,
        prompt_eval_count = 1000,
        eval_count = 200
    });

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SummaryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static OllamaOptions Enabled() => new()
    {
        Enabled = true,
        BaseUrl = "http://ollama.test:11434",
        Model = "llama3.1:8b",
        MaxWindowChars = 12000
    };

    private static SummaryService Create(AppDbContext db, HttpMessageHandler handler, OllamaOptions options)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        var client = new OllamaClient(http, Options.Create(options), NullLogger<OllamaClient>.Instance);
        var summarizer = new TranscriptSummarizer(
            client, Options.Create(options), NullLogger<TranscriptSummarizer>.Instance);

        return new SummaryService(
            db, summarizer, client, Options.Create(options),
            new PushoverNotifier(db, new FakeHttpClientFactory(() => "{}"), Options.Create(new AppOptions()), NullLogger<PushoverNotifier>.Instance),
            NullLogger<SummaryService>.Instance);
    }

    private static async Task<int> SeedTranscriptAsync(AppDbContext db, int segments = 4)
    {
        var episode = new Episode
        {
            Title = "Rates and Rents",
            Show = "Econ Hour",
            AudioPath = "source/e.mp3",
            AudioSha256 = new string('a', 64)
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var transcript = new Transcript { EpisodeId = episode.Id, Model = "large-v3-turbo-q5_0" };
        db.Transcripts.Add(transcript);
        await db.SaveChangesAsync();

        db.Segments.AddRange(Enumerable.Range(0, segments).Select(i => new Segment
        {
            TranscriptId = transcript.Id,
            Ordinal = i,
            StartMs = i * 5000,
            EndMs = (i * 5000) + 5000,
            Text = $"A sentence about interest rates, number {i}."
        }));
        await db.SaveChangesAsync();

        return transcript.Id;
    }

    [Fact]
    public async Task A_successful_run_stores_the_summary_against_the_transcript()
    {
        await using var db = new AppDbContext(_dbOptions);
        var transcriptId = await SeedTranscriptAsync(db);

        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", Answer("## Overview\nThey disagree about rates [00:00:05]."));

        var result = await Create(db, handler, Enabled()).SummarizeAsync(transcriptId);

        Assert.True(result.Success);
        Assert.Null(result.Error);

        var stored = await db.Summaries.SingleAsync(s => s.TranscriptId == transcriptId);
        Assert.Contains("They disagree about rates", stored.Content);
        Assert.Equal("llama3.1:8b", stored.Model);
        Assert.Equal(1, stored.WindowCount);
        Assert.Equal(1000, stored.PromptTokens);
    }

    [Fact]
    public async Task Re_summarising_replaces_the_row_rather_than_adding_a_second()
    {
        await using var db = new AppDbContext(_dbOptions);
        var transcriptId = await SeedTranscriptAsync(db);

        var handler = new RoutedHttpMessageHandler().Always("/api/tags", Tags);
        handler.Sequence("/api/generate", Answer("first summary"), Answer("second summary"));

        var service = Create(db, handler, Enabled());
        await service.SummarizeAsync(transcriptId);
        await service.SummarizeAsync(transcriptId);

        var stored = await db.Summaries.SingleAsync(s => s.TranscriptId == transcriptId);
        Assert.Equal("second summary", stored.Content);
    }

    [Fact]
    public async Task Summarising_is_refused_with_an_explanation_when_it_is_switched_off()
    {
        await using var db = new AppDbContext(_dbOptions);
        var transcriptId = await SeedTranscriptAsync(db);

        var handler = new RoutedHttpMessageHandler().Always("/api/tags", Tags);
        var options = Enabled();
        options.Enabled = false;

        var result = await Create(db, handler, options).SummarizeAsync(transcriptId);

        Assert.False(result.Success);
        Assert.Contains("Ollama__Enabled", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unpulled_model_is_reported_before_any_generating_is_attempted()
    {
        await using var db = new AppDbContext(_dbOptions);
        var transcriptId = await SeedTranscriptAsync(db);

        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", """{"models":[{"name":"mistral:7b"}]}""")
            .Always("/api/generate", Answer("never asked"));

        var result = await Create(db, handler, Enabled()).SummarizeAsync(transcriptId);

        Assert.False(result.Success);
        Assert.Contains("ollama pull llama3.1:8b", result.Error);

        // The point of checking first: on a long episode this would otherwise have been
        // discovered one failed window at a time.
        Assert.Equal(0, handler.CountFor("/api/generate"));
    }

    [Fact]
    public async Task A_failure_while_generating_comes_back_as_a_message_rather_than_an_exception()
    {
        await using var db = new AppDbContext(_dbOptions);
        var transcriptId = await SeedTranscriptAsync(db);

        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", """{"error":"out of memory"}""", System.Net.HttpStatusCode.InternalServerError);

        var result = await Create(db, handler, Enabled()).SummarizeAsync(transcriptId);

        Assert.False(result.Success);
        Assert.Contains("out of memory", result.Error);
        Assert.Empty(db.Summaries);
    }

    [Fact]
    public async Task A_transcript_with_no_segments_is_refused()
    {
        await using var db = new AppDbContext(_dbOptions);
        var transcriptId = await SeedTranscriptAsync(db, segments: 0);

        var handler = new RoutedHttpMessageHandler().Always("/api/tags", Tags);

        var result = await Create(db, handler, Enabled()).SummarizeAsync(transcriptId);

        Assert.False(result.Success);
        Assert.Contains("no segments", result.Error);
    }
}
