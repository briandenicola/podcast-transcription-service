using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

/// <summary>
/// The runner is what lets summarising survive without a Blazor circuit: the POST starts it and
/// returns, and the page reads progress back over ordinary HTTP. What matters here is that it
/// really does run in the background, that progress is visible while it does, and that a second
/// press does not start a second run.
/// </summary>
public class SummaryRunnerTests : IDisposable
{
    private const string Tags = """{"models":[{"name":"llama3.1:8b"}]}""";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SummaryRunnerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static string Answer(string text) => System.Text.Json.JsonSerializer.Serialize(new
    {
        model = "llama3.1:8b",
        response = text,
        done = true,
        prompt_eval_count = 100,
        eval_count = 20
    });

    private ServiceProvider BuildServices(HttpMessageHandler handler, OllamaOptions options)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddScoped(_ => new AppDbContext(_dbOptions));
        services.AddScoped(_ => new OllamaClient(
            new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") },
            Options.Create(options), NullLogger<OllamaClient>.Instance));
        services.AddScoped<TranscriptSummarizer>();
        services.AddScoped<SummaryService>();
        services.AddSingleton<SummaryRunner>();
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(() => "{}"));
        services.AddScoped<PodcastTranscription.Web.Services.PushoverNotifier>();

        return services.BuildServiceProvider();
    }

    private async Task<(int TranscriptId, int EpisodeId)> SeedAsync(int segments = 4)
    {
        await using var db = new AppDbContext(_dbOptions);

        var episode = new Episode { Title = "Test", AudioPath = "a.mp3", AudioSha256 = new string('a', 64) };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var transcript = new Transcript { EpisodeId = episode.Id, Model = "large-v3" };
        db.Transcripts.Add(transcript);
        await db.SaveChangesAsync();

        db.Segments.AddRange(Enumerable.Range(0, segments).Select(i => new Segment
        {
            TranscriptId = transcript.Id,
            Ordinal = i,
            StartMs = i * 1000,
            EndMs = (i * 1000) + 900,
            Text = $"A sentence, number {i}."
        }));
        await db.SaveChangesAsync();

        return (transcript.Id, episode.Id);
    }

    private static async Task WaitForFinish(SummaryRunner runner, int transcriptId)
    {
        for (var i = 0; i < 200 && runner.For(transcriptId) is not { Finished: true }; i++)
        {
            await Task.Delay(25);
        }
    }

    private static OllamaOptions Enabled() => new()
    {
        Enabled = true,
        BaseUrl = "http://ollama.test:11434",
        Model = "llama3.1:8b",
        MaxWindowChars = 12000
    };

    [Fact]
    public async Task Start_returns_immediately_and_finishes_in_the_background()
    {
        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", Answer("## Overview\nDone."));

        using var services = BuildServices(handler, Enabled());
        var runner = services.GetRequiredService<SummaryRunner>();
        var (transcriptId, episodeId) = await SeedAsync();

        Assert.True(runner.Start(transcriptId, episodeId));

        await WaitForFinish(runner, transcriptId);

        var run = runner.For(transcriptId)!;
        Assert.True(run.Finished);
        Assert.True(run.Succeeded);
        Assert.Null(run.Error);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Contains("Done.", (await verify.Summaries.SingleAsync()).Content);
    }

    [Fact]
    public async Task A_second_press_does_not_start_a_second_run()
    {
        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", Answer("summary"));

        using var services = BuildServices(handler, Enabled());
        var runner = services.GetRequiredService<SummaryRunner>();
        var (transcriptId, episodeId) = await SeedAsync();

        Assert.True(runner.Start(transcriptId, episodeId));

        // A double-click, or the browser re-posting on refresh.
        var second = runner.Start(transcriptId, episodeId);

        await WaitForFinish(runner, transcriptId);

        Assert.False(second);
        await using var verify = new AppDbContext(_dbOptions);
        Assert.Single(verify.Summaries);
    }

    [Fact]
    public async Task Progress_is_readable_while_the_run_is_going()
    {
        var reported = new List<SummaryProgress>();
        var options = Enabled();
        options.MaxWindowChars = 500;

        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", Answer("notes"));

        using var services = BuildServices(handler, options);
        var summarizer = services.GetRequiredService<TranscriptSummarizer>();

        var segments = Enumerable.Range(0, 40).Select(i => new Segment
        {
            Ordinal = i,
            StartMs = i * 5000,
            EndMs = (i * 5000) + 4000,
            Text = $"Sentence number {i} with enough words in it to take up a bit of room."
        }).ToList();

        await summarizer.SummarizeAsync(
            "Long", null, segments, new Progress<SummaryProgress>(reported.Add));

        // Progress is delivered through Progress<T>, which posts to the thread pool, so give the
        // callbacks a moment to land before reading them.
        await Task.Delay(200);

        Assert.NotEmpty(reported);
        Assert.Contains(reported, p => p.Stage.StartsWith("Reading part"));
        Assert.Contains(reported, p => p.Stage == "Writing the summary");
        Assert.All(reported, p => Assert.InRange(p.Percent, 0, 100));
    }

    [Fact]
    public async Task A_failure_is_recorded_on_the_run_rather_than_thrown_into_nothing()
    {
        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", """{"models":[{"name":"something-else"}]}""");

        using var services = BuildServices(handler, Enabled());
        var runner = services.GetRequiredService<SummaryRunner>();
        var (transcriptId, episodeId) = await SeedAsync();

        runner.Start(transcriptId, episodeId);
        await WaitForFinish(runner, transcriptId);

        var run = runner.For(transcriptId)!;
        Assert.True(run.Finished);
        Assert.False(run.Succeeded);

        // The page shows this instead of a progress bar that never moves.
        Assert.Contains("ollama pull llama3.1:8b", run.Error);
    }

    [Fact]
    public void An_untouched_transcript_has_no_run()
    {
        var handler = new RoutedHttpMessageHandler().Always("/api/tags", Tags);
        using var services = BuildServices(handler, Enabled());

        Assert.Null(services.GetRequiredService<SummaryRunner>().For(999));
    }
}
