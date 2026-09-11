using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Chunking;
using PodcastTranscription.Web.Services.Ingest;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

/// <summary>
/// The pipeline against a real SQLite database, a stubbed whisper-server and a stand-in for
/// ffmpeg. Covers the parts that are painful to verify by hand: timestamp offsetting across
/// chunk boundaries, partial persistence, and resuming mid-episode.
/// </summary>
public class TranscriptionPipelineTests : IDisposable
{
    /// <summary>Sentence-per-segment, as whisper answers without word timestamps.</summary>
    private static string SentenceResponse(string text, double startSec, double endSec) => $$"""
    {
      "task": "transcribe",
      "language": "en",
      "duration": {{endSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
      "text": "{{text}}",
      "segments": [
        { "id": 0, "start": {{startSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "end": {{endSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "text": " {{text}}", "avg_logprob": -0.25 }
      ]
    }
    """;

    /// <summary>One word per segment, as max_len=1 &amp; split_on_word=true answers.</summary>
    private const string WordResponse = """
    {
      "task": "transcribe",
      "language": "en",
      "duration": 3.0,
      "text": " Hello there. General",
      "segments": [
        { "id": 0, "start": 0.0, "end": 0.4, "text": " Hello",   "avg_logprob": -0.1 },
        { "id": 1, "start": 0.4, "end": 0.9, "text": " there.",  "avg_logprob": -0.3 },
        { "id": 2, "start": 1.0, "end": 1.6, "text": " General", "avg_logprob": -0.2 }
      ]
    }
    """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly string _mediaRoot = Path.Combine(Path.GetTempPath(), $"pts-pipeline-{Guid.NewGuid():N}");

    public TranscriptionPipelineTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();

        Directory.CreateDirectory(_mediaRoot);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_mediaRoot))
        {
            Directory.Delete(_mediaRoot, recursive: true);
        }
    }

    /// <summary>
    /// Summarisation off by default, as it is on a host with no Ollama configured. Pass
    /// <paramref name="ollamaOptions"/> and a handler to exercise the other path.
    /// </summary>
    private static SummaryService CreateSummaries(
        AppDbContext db, OllamaOptions? ollamaOptions = null, HttpMessageHandler? ollamaHandler = null)
    {
        var options = ollamaOptions ?? new OllamaOptions { Enabled = false };
        var http = new HttpClient(ollamaHandler ?? new RoutedHttpMessageHandler())
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/")
        };

        var client = new OllamaClient(http, Options.Create(options), NullLogger<OllamaClient>.Instance);
        var summarizer = new TranscriptSummarizer(
            client, Options.Create(options), NullLogger<TranscriptSummarizer>.Instance);

        return new SummaryService(
            db, summarizer, client, Options.Create(options), CreatePushover(db), NullLogger<SummaryService>.Instance);
    }

    private static PushoverNotifier CreatePushover(AppDbContext db) =>
        new(db, new FakeHttpClientFactory(() => "{}"), Options.Create(new AppOptions()), NullLogger<PushoverNotifier>.Instance);

    private TranscriptionPipeline CreatePipeline(
        AppDbContext db, HttpMessageHandler handler, AudioProcessor audio, TranscriptionOptions options,
        YtDlpClient? downloader = null, SummaryService? summaries = null)
    {
        var whisperOptions = new WhisperOptions { BaseUrl = "http://whisper.test:8080", Model = "large-v3-turbo-q5_0" };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://whisper.test:8080/") };
        var whisper = new WhisperClient(http, Options.Create(whisperOptions), NullLogger<WhisperClient>.Instance);
        var media = new MediaStore(
            Options.Create(new StorageOptions { MediaPath = _mediaRoot }), new FakeHostEnvironment(_mediaRoot));

        var ytDlp = downloader ?? new YtDlpClient(
            Options.Create(new MediaToolOptions()),
            Options.Create(new IngestOptions()),
            NullLogger<YtDlpClient>.Instance);

        return new TranscriptionPipeline(
            db, media, audio, whisper, ytDlp, summaries ?? CreateSummaries(db), Options.Create(options),
            new JobNotifier(), CreatePushover(db), NullLogger<TranscriptionPipeline>.Instance);
    }

    private async Task<(Episode Episode, Job Job)> SeedAsync(AppDbContext db)
    {
        var episode = new Episode
        {
            Title = "Test episode",
            AudioPath = "source/test.mp3",
            AudioSha256 = new string('a', 64),
            PreparedAudioPath = "prepared/test.wav"
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var wav = Path.Combine(_mediaRoot, "prepared", "test.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        await File.WriteAllBytesAsync(wav, new byte[64]);

        var job = new Job { EpisodeId = episode.Id, State = JobState.Queued, Model = "large-v3-turbo-q5_0" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        return (episode, job);
    }

    private static TranscriptionOptions Settings(bool wordTimestamps = false) => new()
    {
        ChunkSeconds = 600,
        SilenceSearchWindowSeconds = 90,
        WordTimestamps = wordTimestamps,
        WordGapMs = 700,
        MaxSegmentChars = 200
    };

    [Fact]
    public async Task A_long_episode_is_posted_as_chunks_rather_than_one_request()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var audio = new FakeAudioProcessor(durationSeconds: 1500); // 25 minutes
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, SentenceResponse("First", 0, 60)),
            (HttpStatusCode.OK, SentenceResponse("Second", 0, 60)),
            (HttpStatusCode.OK, SentenceResponse("Third", 0, 60)));

        await CreatePipeline(db, handler, audio, Settings()).RunAsync(job.Id, CancellationToken.None);

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal([(0, 600_000), (600_000, 1_200_000), (1_200_000, 1_500_000)], audio.ExtractedChunks);
    }

    [Fact]
    public async Task Each_chunks_timestamps_are_offset_by_where_the_chunk_starts()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        // Every chunk reports 10-20 s in its own frame; only the offset distinguishes them.
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, SentenceResponse("First", 10, 20)),
            (HttpStatusCode.OK, SentenceResponse("Second", 10, 20)));

        await CreatePipeline(db, handler, new FakeAudioProcessor(1200), Settings()).RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);
        var segments = await verify.Segments.OrderBy(s => s.Ordinal).ToListAsync();

        Assert.Equal(2, segments.Count);
        Assert.Equal(10_000, segments[0].StartMs);
        Assert.Equal(610_000, segments[1].StartMs);
        Assert.Equal(620_000, segments[1].EndMs);
        Assert.Equal([0, 1], segments.Select(s => s.Ordinal));
    }

    [Fact]
    public async Task The_chunk_plan_and_raw_responses_are_persisted()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Hello", 0, 30)));

        await CreatePipeline(db, handler, new FakeAudioProcessor(1200), Settings()).RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);
        var stored = await verify.Jobs.SingleAsync();

        var plan = JsonSerializer.Deserialize<List<AudioChunk>>(stored.ChunkPlanJson!)!;
        Assert.Equal(2, plan.Count);
        Assert.Equal(2, stored.TotalChunks);
        Assert.Equal(2, stored.CompletedChunks);

        var chunks = await verify.TranscriptChunks.OrderBy(c => c.Index).ToListAsync();
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Contains("avg_logprob", c.RawJson));
        Assert.Equal(600_000, chunks[1].StartMs);
    }

    [Fact]
    public async Task Progress_and_the_completed_job_are_recorded()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Hello", 0, 30)));

        await CreatePipeline(db, handler, new FakeAudioProcessor(1200), Settings()).RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);
        var stored = await verify.Jobs.SingleAsync();

        Assert.Equal(JobState.Completed, stored.State);
        Assert.Equal(1.0, stored.Progress);
        Assert.Equal(1200, stored.ProcessedAudioSec);
        Assert.NotNull(stored.CompletedAt);
        Assert.NotNull(stored.TranscriptId);

        var transcript = await verify.Transcripts.SingleAsync();
        Assert.True(transcript.IsComplete);
        Assert.Equal("en", transcript.Language);
        Assert.NotNull(transcript.RealtimeFactor);
    }

    [Fact]
    public async Task A_failed_chunk_leaves_the_earlier_ones_persisted()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, SentenceResponse("First", 0, 60)),
            (HttpStatusCode.ServiceUnavailable, "model not loaded"));

        var pipeline = CreatePipeline(db, handler, new FakeAudioProcessor(1200), Settings());

        await Assert.ThrowsAsync<WhisperException>(() => pipeline.RunAsync(job.Id, CancellationToken.None));

        await using var verify = new AppDbContext(_dbOptions);
        var stored = await verify.Jobs.SingleAsync();

        // The first chunk's work survives, and CompletedChunks is where a retry picks up.
        Assert.Equal(1, stored.CompletedChunks);
        Assert.Equal(0.5, stored.Progress);
        Assert.Equal(1, await verify.Segments.CountAsync());
        Assert.False((await verify.Transcripts.SingleAsync()).IsComplete);
    }

    [Fact]
    public async Task A_resumed_job_only_posts_the_chunks_it_had_not_reached()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        // First attempt: chunk one succeeds, chunk two fails.
        var failing = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, SentenceResponse("First", 0, 60)),
            (HttpStatusCode.ServiceUnavailable, "model not loaded"));
        var pipeline = CreatePipeline(db, failing, new FakeAudioProcessor(1200), Settings());
        await Assert.ThrowsAsync<WhisperException>(() => pipeline.RunAsync(job.Id, CancellationToken.None));

        // Second attempt, as the worker would run it after backoff.
        await using var resumeDb = new AppDbContext(_dbOptions);
        var audio = new FakeAudioProcessor(1200);
        var succeeding = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Second", 10, 20)));
        await CreatePipeline(resumeDb, succeeding, audio, Settings()).RunAsync(job.Id, CancellationToken.None);

        // Only the second chunk was cut and posted; the first was not transcribed twice.
        Assert.Equal(1, succeeding.RequestCount);
        Assert.Equal([(600_000, 1_200_000)], audio.ExtractedChunks);

        await using var verify = new AppDbContext(_dbOptions);
        var segments = await verify.Segments.OrderBy(s => s.Ordinal).ToListAsync();

        Assert.Equal(2, segments.Count);
        Assert.Equal([0, 1], segments.Select(s => s.Ordinal));
        Assert.Equal(610_000, segments[1].StartMs);
        Assert.Single(await verify.Transcripts.ToListAsync());
        Assert.Equal(JobState.Completed, (await verify.Jobs.SingleAsync()).State);
    }

    [Fact]
    public async Task Word_timestamps_are_requested_grouped_and_stored_with_the_segment()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, WordResponse));

        await CreatePipeline(db, handler, new FakeAudioProcessor(300), Settings(wordTimestamps: true))
            .RunAsync(job.Id, CancellationToken.None);

        Assert.Contains("name=max_len", handler.RequestBodies[0]);
        Assert.Contains("name=split_on_word", handler.RequestBodies[0]);

        await using var verify = new AppDbContext(_dbOptions);
        var segments = await verify.Segments.OrderBy(s => s.Ordinal).ToListAsync();

        // "Hello there." closes on the full stop; "General" is left open.
        Assert.Equal(["Hello there.", "General"], segments.Select(s => s.Text));

        var words = JsonSerializer.Deserialize<List<TranscribedWord>>(segments[0].WordsJson!)!;
        Assert.Equal(["Hello", "there."], words.Select(w => w.Text));
        Assert.Equal(0, words[0].StartMs);
        Assert.Equal(900, words[1].EndMs);

        // Probability comes from exponentiating the single word's avg_logprob.
        Assert.Equal(Math.Exp(-0.1), words[0].Probability!.Value, precision: 6);
    }

    [Fact]
    public async Task Word_timestamps_are_offset_across_chunk_boundaries_too()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, WordResponse),
            (HttpStatusCode.OK, WordResponse));

        await CreatePipeline(db, handler, new FakeAudioProcessor(1200), Settings(wordTimestamps: true))
            .RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);
        var segments = await verify.Segments.OrderBy(s => s.Ordinal).ToListAsync();

        var secondChunkWords = JsonSerializer.Deserialize<List<TranscribedWord>>(
            segments.First(s => s.StartMs >= 600_000).WordsJson!)!;

        Assert.Equal(600_000, secondChunkWords[0].StartMs);
        Assert.Equal(600_400, secondChunkWords[0].EndMs);
    }

    [Fact]
    public async Task Cancelling_mid_episode_keeps_what_had_already_landed()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        using var cts = new CancellationTokenSource();
        var handler = new CancellingHandler(cts, SentenceResponse("First", 0, 60));
        var pipeline = CreatePipeline(db, handler, new FakeAudioProcessor(1200), Settings());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(job.Id, cts.Token));

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Equal(1, await verify.Segments.CountAsync());
        Assert.Equal(1, (await verify.Jobs.SingleAsync()).CompletedChunks);
    }

    [Fact]
    public async Task An_episode_with_only_a_url_is_downloaded_before_it_is_prepared()
    {
        await using var db = new AppDbContext(_dbOptions);

        // How a feed item arrives: a URL, and nothing on disk yet.
        var episode = new Episode
        {
            Title = "Fetching…",
            SourceUrl = "https://example.com/audio/12.mp3",
            AudioPath = string.Empty,
            AudioSha256 = string.Empty
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var job = new Job { EpisodeId = episode.Id, State = JobState.Queued, Model = "large-v3-turbo-q5_0" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var downloader = new FakeYtDlpClient(title: "Ep. 12 — Pricing", durationSec: 300);
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Hello", 0, 30)));

        await CreatePipeline(db, handler, new FakeAudioProcessor(300), Settings(), downloader)
            .RunAsync(job.Id, CancellationToken.None);

        Assert.Equal(["https://example.com/audio/12.mp3"], downloader.RequestedUrls);

        await using var verify = new AppDbContext(_dbOptions);
        var stored = await verify.Episodes.SingleAsync();

        Assert.NotEqual(string.Empty, stored.AudioPath);
        Assert.Equal(64, stored.AudioSha256.Length);
        Assert.Equal("Ep. 12 — Pricing", stored.Title);
        Assert.Equal(JobState.Completed, (await verify.Jobs.SingleAsync()).State);
    }

    [Fact]
    public async Task A_title_edited_during_download_is_not_replaced_by_downloaded_metadata()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = new Episode
        {
            Title = Episode.PendingTitle,
            SourceUrl = "https://example.com/audio/edited.mp3"
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var job = new Job { EpisodeId = episode.Id, State = JobState.Queued, Model = "large-v3-turbo-q5_0" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var downloader = new FakeYtDlpClient(title: "Downloaded title", durationSec: 300)
        {
            BeforeComplete = async () =>
            {
                await using var editorDb = new AppDbContext(_dbOptions);
                var metadata = new EpisodeMetadataService(
                    editorDb, NullLogger<EpisodeMetadataService>.Instance);
                await metadata.UpdateAsync(episode.Id, "My corrected title", "My show");
            }
        };
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, SentenceResponse("Hello", 0, 30)));

        await CreatePipeline(db, handler, new FakeAudioProcessor(300), Settings(), downloader)
            .RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);
        var stored = await verify.Episodes.SingleAsync();
        Assert.Equal("My corrected title", stored.Title);
        Assert.Equal("My show", stored.Show);
    }

    [Fact]
    public async Task A_download_matching_audio_already_in_the_library_fails_without_retrying()
    {
        await using var db = new AppDbContext(_dbOptions);

        var content = new byte[] { 9, 8, 7, 6 };
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

        db.Episodes.Add(new Episode
        {
            Title = "Already here",
            AudioPath = "source/original.mp3",
            AudioSha256 = sha
        });
        await db.SaveChangesAsync();

        var arriving = new Episode
        {
            Title = "Fetching…",
            SourceUrl = "https://example.com/audio/same.mp3",
            AudioPath = string.Empty,
            AudioSha256 = string.Empty
        };
        db.Episodes.Add(arriving);
        await db.SaveChangesAsync();

        var job = new Job { EpisodeId = arriving.Id, State = JobState.Queued, Model = "large-v3-turbo-q5_0" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var downloader = new FakeYtDlpClient(content);
        var pipeline = CreatePipeline(db, new SequencedHttpMessageHandler(), new FakeAudioProcessor(300),
            Settings(), downloader);

        // Terminal: retrying would download the same bytes again to the same conclusion.
        var ex = await Assert.ThrowsAsync<TerminalJobException>(() => pipeline.RunAsync(job.Id, CancellationToken.None));
        Assert.Contains("Already here", ex.Message);

        // The redundant copy is not left on disk.
        Assert.False(Directory.Exists(Path.Combine(_mediaRoot, "source", $"episode-{arriving.Id}"))
            && Directory.EnumerateFiles(Path.Combine(_mediaRoot, "source", $"episode-{arriving.Id}")).Any());
    }

    [Fact]
    public async Task An_episode_with_neither_audio_nor_a_url_fails_terminally()
    {
        await using var db = new AppDbContext(_dbOptions);

        var episode = new Episode { Title = "Nothing to work with", AudioPath = string.Empty, AudioSha256 = string.Empty };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var job = new Job { EpisodeId = episode.Id, State = JobState.Queued, Model = "tiny" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var pipeline = CreatePipeline(db, new SequencedHttpMessageHandler(), new FakeAudioProcessor(300), Settings());

        await Assert.ThrowsAsync<TerminalJobException>(() => pipeline.RunAsync(job.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_jobs_prompt_and_language_are_sent_to_whisper()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        job.Language = "en";
        job.Prompt = "Kara Swisher, EBITDA";
        await db.SaveChangesAsync();

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Hello", 0, 30)));

        await CreatePipeline(db, handler, new FakeAudioProcessor(300), Settings()).RunAsync(job.Id, CancellationToken.None);

        Assert.Contains("Kara Swisher, EBITDA", handler.RequestBodies[0]);
        Assert.Contains("name=language", handler.RequestBodies[0]);
    }

    /// <summary>Answers the first request, then cancels so the loop stops before the second.</summary>
    private sealed class CancellingHandler(CancellationTokenSource cts, string body) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (++_calls > 1)
            {
                throw new InvalidOperationException("The pipeline should have stopped after the cancellation.");
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };

            cts.Cancel();
            return Task.FromResult(response);
        }
    }

    // ------------------------------------------------------------------ summaries --

    private const string OllamaTags = """{"models":[{"name":"llama3.1:8b"}]}""";

    private static OllamaOptions SummaryOptions() => new()
    {
        Enabled = true,
        AutoSummarize = true,
        BaseUrl = "http://ollama.test:11434",
        Model = "llama3.1:8b",
        MaxWindowChars = 12000
    };

    private static string OllamaAnswer(string text) => JsonSerializer.Serialize(new
    {
        model = "llama3.1:8b",
        response = text,
        done = true,
        prompt_eval_count = 900,
        eval_count = 180
    });

    [Fact]
    public async Task A_finished_job_is_summarised_before_it_is_marked_complete()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var ollama = new RoutedHttpMessageHandler()
            .Always("/api/tags", OllamaTags)
            .Always("/api/generate", OllamaAnswer("## Overview\nThey argue about rates [00:00:10]."));

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Rates", 0, 60)));

        await CreatePipeline(
                db, handler, new FakeAudioProcessor(300), Settings(),
                summaries: CreateSummaries(db, SummaryOptions(), ollama))
            .RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);
        var summary = await verify.Summaries.SingleAsync();

        Assert.Contains("They argue about rates", summary.Content);
        Assert.Equal("llama3.1:8b", summary.Model);
        Assert.Equal(JobState.Completed, (await verify.Jobs.SingleAsync()).State);
    }

    [Fact]
    public async Task An_unreachable_ollama_costs_the_summary_and_nothing_else()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        // Nothing answers, so even the health check fails. By this point the transcript is an
        // hour of GPU time already spent, and it has to survive a language model being down.
        var ollama = new RoutedHttpMessageHandler();

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Rates", 0, 60)));

        await CreatePipeline(
                db, handler, new FakeAudioProcessor(300), Settings(),
                summaries: CreateSummaries(db, SummaryOptions(), ollama))
            .RunAsync(job.Id, CancellationToken.None);

        await using var verify = new AppDbContext(_dbOptions);

        Assert.Empty(verify.Summaries);
        Assert.Equal(JobState.Completed, (await verify.Jobs.SingleAsync()).State);
        Assert.True((await verify.Transcripts.SingleAsync()).IsComplete);
        Assert.Single(verify.Segments);
    }

    [Fact]
    public async Task Nothing_is_asked_of_ollama_when_summaries_are_off()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var ollama = new RoutedHttpMessageHandler()
            .Always("/api/tags", OllamaTags)
            .Always("/api/generate", OllamaAnswer("never asked"));

        var options = SummaryOptions();
        options.Enabled = false;

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Rates", 0, 60)));

        await CreatePipeline(
                db, handler, new FakeAudioProcessor(300), Settings(),
                summaries: CreateSummaries(db, options, ollama))
            .RunAsync(job.Id, CancellationToken.None);

        Assert.Empty(ollama.Requests);
        Assert.Empty(db.Summaries);
    }

    [Fact]
    public async Task AutoSummarize_off_leaves_it_to_the_button_on_the_episode_page()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, job) = await SeedAsync(db);

        var ollama = new RoutedHttpMessageHandler()
            .Always("/api/tags", OllamaTags)
            .Always("/api/generate", OllamaAnswer("never asked"));

        var options = SummaryOptions();
        options.AutoSummarize = false;

        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, SentenceResponse("Rates", 0, 60)));

        await CreatePipeline(
                db, handler, new FakeAudioProcessor(300), Settings(),
                summaries: CreateSummaries(db, options, ollama))
            .RunAsync(job.Id, CancellationToken.None);

        Assert.Empty(ollama.Requests);
        Assert.Empty(db.Summaries);
    }

    private sealed class FakeHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
