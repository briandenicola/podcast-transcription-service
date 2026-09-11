using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

/// <summary>
/// Manual summarisation is a real, queued Job row now, claimed and run by
/// <see cref="SummarizationWorker"/> the same way <see cref="TranscriptionWorker"/> claims and
/// runs transcription jobs — same table, same cancel/retry/Jobs-page treatment. What matters
/// here is that a queued job really does finish in the background, that progress lands on the
/// job row while it runs, that a duplicate request is rejected, and that a failure retries with
/// backoff before giving up.
/// </summary>
public class SummarizationWorkerTests : IDisposable
{
    private const string Tags = """{"models":[{"name":"llama3.1:8b"}]}""";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SummarizationWorkerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();
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

    /// <summary>
    /// The provider plus the worker that has to be running for anything queued to actually be
    /// processed — the production app gets that from the hosted service registry, which nothing
    /// here spins up.
    /// </summary>
    private sealed class Harness(ServiceProvider services, SummarizationWorker worker) : IAsyncDisposable
    {
        public ServiceProvider Services => services;

        public async ValueTask DisposeAsync()
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    private async Task<Harness> BuildServicesAsync(HttpMessageHandler handler, OllamaOptions options)
    {
        var whisperOptions = new WhisperOptions { BaseUrl = "http://whisper.test:8080", Model = "large-v3-turbo-q5_0" };

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Options.Create(whisperOptions));
        services.AddSingleton(Options.Create(new TranscriptionOptions()));
        services.AddScoped(_ => new AppDbContext(_dbOptions));
        services.AddScoped(_ => new WhisperClient(
            new HttpClient { BaseAddress = new Uri("http://whisper.test:8080/") },
            Options.Create(whisperOptions), NullLogger<WhisperClient>.Instance));
        services.AddScoped(_ => new OllamaClient(
            new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") },
            Options.Create(options), NullLogger<OllamaClient>.Instance));
        services.AddScoped<TranscriptSummarizer>();
        services.AddScoped<SummaryService>();
        services.AddScoped<JobQueue>();
        services.AddSingleton<RunningJobs>();
        services.AddSingleton<JobNotifier>();
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(() => "{}"));
        services.AddSingleton(Options.Create(new AppOptions()));
        services.AddScoped<PushoverNotifier>();

        var provider = services.BuildServiceProvider();

        var worker = new SummarizationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<RunningJobs>(),
            provider.GetRequiredService<JobNotifier>(),
            Options.Create(options),
            NullLogger<SummarizationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        return new Harness(provider, worker);
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

    private async Task<Job> WaitUntilTerminalAsync(int jobId)
    {
        for (var i = 0; i < 400; i++)
        {
            await using var db = new AppDbContext(_dbOptions);
            var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
            if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Job {jobId} never reached a terminal state.");
    }

    private static OllamaOptions Enabled() => new()
    {
        Enabled = true,
        BaseUrl = "http://ollama.test:11434",
        Model = "llama3.1:8b",
        MaxWindowChars = 12000,
        RetryBaseSeconds = 0
    };

    [Fact]
    public async Task A_queued_summarisation_job_finishes_in_the_background()
    {
        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", Answer("## Overview\nDone."));

        await using var harness = await BuildServicesAsync(handler, Enabled());
        var (transcriptId, episodeId) = await SeedAsync();

        using var scope = harness.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var job = await queue.EnqueueSummarizationAsync(episodeId, transcriptId, "llama3.1:8b");

        Assert.NotNull(job);
        Assert.Equal(JobKind.Summarization, job!.Kind);

        var finished = await WaitUntilTerminalAsync(job.Id);

        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(1, finished.Progress);
        Assert.Null(finished.LastError);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Contains("Done.", (await verify.Summaries.SingleAsync()).Content);
    }

    [Fact]
    public async Task A_second_request_for_the_same_transcript_is_rejected_while_one_is_in_flight()
    {
        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", Tags)
            .Always("/api/generate", Answer("summary"));

        await using var harness = await BuildServicesAsync(handler, Enabled());
        var (transcriptId, episodeId) = await SeedAsync();

        using var scope = harness.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        var first = await queue.EnqueueSummarizationAsync(episodeId, transcriptId, "llama3.1:8b");
        var second = await queue.EnqueueSummarizationAsync(episodeId, transcriptId, "llama3.1:8b");

        Assert.NotNull(first);
        Assert.Null(second);

        await WaitUntilTerminalAsync(first!.Id);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Single(verify.Summaries);
        Assert.Equal(1, await verify.Jobs.CountAsync(j => j.Kind == JobKind.Summarization));
    }

    [Fact]
    public async Task Progress_lands_on_the_job_row_while_it_runs()
    {
        var releasePass = new TaskCompletionSource();
        var passesStarted = 0;

        var handler = new BlockingGenerateHandler(Tags, () =>
        {
            // Let the first two passes (of three, for a transcript this size at 500 chars a
            // window) run straight through, and hold the third open so the test can read the
            // job mid-flight with room made for a completed pass.
            var count = Interlocked.Increment(ref passesStarted);
            return count >= 3 ? releasePass.Task : Task.CompletedTask;
        }, Answer("notes"));

        var options = Enabled();
        options.MaxWindowChars = 500;

        await using var harness = await BuildServicesAsync(handler, options);

        var segments = Enumerable.Range(0, 40).Select(i => new Segment
        {
            Ordinal = i,
            StartMs = i * 5000,
            EndMs = (i * 5000) + 4000,
            Text = $"Sentence number {i} with enough words in it to take up a bit of room."
        }).ToList();

        await using (var seed = new AppDbContext(_dbOptions))
        {
            var episode = new Episode { Title = "Long", AudioPath = "a.mp3", AudioSha256 = new string('b', 64) };
            seed.Episodes.Add(episode);
            await seed.SaveChangesAsync();

            var transcript = new Transcript { EpisodeId = episode.Id, Model = "large-v3" };
            seed.Transcripts.Add(transcript);
            await seed.SaveChangesAsync();

            foreach (var segment in segments)
            {
                segment.TranscriptId = transcript.Id;
            }

            seed.Segments.AddRange(segments);
            await seed.SaveChangesAsync();

            using var scope = harness.Services.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
            var job = await queue.EnqueueSummarizationAsync(episode.Id, transcript.Id, "llama3.1:8b");
            Assert.NotNull(job);

            Job? mid = null;
            for (var i = 0; i < 200 && mid is null; i++)
            {
                await Task.Delay(25);
                await using var poll = new AppDbContext(_dbOptions);
                var candidate = await poll.Jobs.FirstAsync(j => j.Id == job!.Id);
                if (candidate.TotalChunks > 0 && candidate.CompletedChunks > 0)
                {
                    mid = candidate;
                }
            }

            Assert.NotNull(mid);
            Assert.True(mid!.TotalChunks > 1);
            Assert.InRange(mid.Progress, 0, 1);
            Assert.Equal(JobState.Summarizing, mid.State);

            releasePass.SetResult();
            await WaitUntilTerminalAsync(job!.Id);
        }
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_it_rather_than_waiting_it_out()
    {
        var gate = new TaskCompletionSource();

        var handler = new BlockingGenerateHandler(Tags, () => gate.Task, Answer("summary"));

        await using var harness = await BuildServicesAsync(handler, Enabled());
        var (transcriptId, episodeId) = await SeedAsync();

        using var scope = harness.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var job = await queue.EnqueueSummarizationAsync(episodeId, transcriptId, "llama3.1:8b");
        Assert.NotNull(job);

        // Wait for the worker to actually claim it and be blocked inside the generate call,
        // rather than cancelling a job still sitting in Queued.
        RunningJobs running = harness.Services.GetRequiredService<RunningJobs>();
        for (var i = 0; i < 200 && !running.IsRunning(job!.Id); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(running.IsRunning(job!.Id));

        var cancelled = await queue.CancelAsync(job.Id);
        Assert.True(cancelled);

        var finished = await WaitUntilTerminalAsync(job.Id);

        Assert.Equal(JobState.Cancelled, finished.State);

        gate.SetResult();
    }

    [Fact]
    public async Task A_failure_retries_with_backoff_then_gives_up()
    {
        var handler = new RoutedHttpMessageHandler()
            .Always("/api/tags", """{"models":[{"name":"something-else"}]}""");

        var options = Enabled();
        options.MaxAttempts = 2;
        options.RetryBaseSeconds = 0;

        await using var harness = await BuildServicesAsync(handler, options);
        var (transcriptId, episodeId) = await SeedAsync();

        using var scope = harness.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var job = await queue.EnqueueSummarizationAsync(episodeId, transcriptId, "llama3.1:8b");

        var finished = await WaitUntilTerminalAsync(job!.Id);

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Equal(2, finished.Attempts);

        // The page shows this instead of a progress bar that never moves.
        Assert.Contains("ollama pull llama3.1:8b", finished.LastError);
    }

    /// <summary>
    /// Answers <c>/api/tags</c> immediately and holds <c>/api/generate</c> open until
    /// <paramref name="gate"/> completes, so a test can prove a pass never starts until the
    /// worker is actually ready for it.
    /// </summary>
    private sealed class BlockingGenerateHandler(string tags, Func<Task> gate, string generateBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/tags")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tags, System.Text.Encoding.UTF8, "application/json")
                };
            }

            await gate();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(generateBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
