using System.Net;
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

namespace PodcastTranscription.Tests;

/// <summary>
/// Exercises the persistence half of the pipeline against a real SQLite database and a stubbed
/// whisper-server. The prepared WAV is planted on disk so ffmpeg never has to run.
/// </summary>
public class TranscriptionServiceTests : IDisposable
{
    private const string VerboseJson = """
    {
      "task": "transcribe",
      "language": "en",
      "duration": 90.0,
      "text": " One. Two.",
      "segments": [
        { "id": 0, "start": 0.0, "end": 1.5, "text": " One.", "avg_logprob": -0.1 },
        { "id": 1, "start": 1.5, "end": 3.0, "text": "  ",    "avg_logprob": -0.9 },
        { "id": 2, "start": 3.0, "end": 4.5, "text": " Two.", "avg_logprob": -0.2 }
      ]
    }
    """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly string _mediaRoot = Path.Combine(Path.GetTempPath(), $"pts-tests-{Guid.NewGuid():N}");

    public TranscriptionServiceTests()
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

    private MediaStore CreateMediaStore() =>
        new(Options.Create(new StorageOptions { MediaPath = _mediaRoot }), new FakeHostEnvironment(_mediaRoot));

    private TranscriptionService CreateService(AppDbContext db, StubHttpMessageHandler handler)
    {
        var options = new WhisperOptions { BaseUrl = "http://whisper.test:8080", Model = "large-v3-turbo-q5_0" };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://whisper.test:8080/") };
        var whisper = new WhisperClient(http, Options.Create(options), NullLogger<WhisperClient>.Instance);
        var audio = new AudioProcessor(Options.Create(new MediaToolOptions()), NullLogger<AudioProcessor>.Instance);

        return new TranscriptionService(db, CreateMediaStore(), audio, whisper, NullLogger<TranscriptionService>.Instance);
    }

    /// <summary>Plants an episode whose prepared WAV already exists, so no ffmpeg call is needed.</summary>
    private async Task<Episode> SeedEpisodeAsync(AppDbContext db)
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

        return episode;
    }

    [Fact]
    public async Task TranscribeAsync_persists_the_transcript_its_segments_and_the_raw_response()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedEpisodeAsync(db);
        var service = CreateService(db, new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson));

        var transcript = await service.TranscribeAsync(episode.Id);

        Assert.Equal("large-v3-turbo-q5_0", transcript.Model);
        Assert.Equal("en", transcript.Language);
        Assert.Equal(VerboseJson, transcript.RawJson);

        // The whitespace-only segment is dropped, and ordinals stay contiguous across the gap.
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal([0, 1], transcript.Segments.Select(s => s.Ordinal));
        Assert.Equal(["One.", "Two."], transcript.Segments.Select(s => s.Text));
        Assert.Equal(3000, transcript.Segments[1].StartMs);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Equal(1, await verify.Transcripts.CountAsync());
        Assert.Equal(2, await verify.Segments.CountAsync());
    }

    [Fact]
    public async Task TranscribeAsync_completes_the_job_and_records_the_realtime_factor()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedEpisodeAsync(db);
        var service = CreateService(db, new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson));

        var transcript = await service.TranscribeAsync(episode.Id);

        await using var verify = new AppDbContext(_dbOptions);
        var job = await verify.Jobs.SingleAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(1.0, job.Progress);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LastError);

        // 90 seconds of audio against a stub that answers instantly: far above realtime.
        Assert.True(transcript.RealtimeFactor > 1);

        // Duration was unknown on the episode until whisper reported it.
        Assert.Equal(90.0, (await verify.Episodes.SingleAsync()).DurationSec);
    }

    [Fact]
    public async Task TranscribeAsync_marks_the_job_failed_and_keeps_the_server_error()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedEpisodeAsync(db);
        var service = CreateService(db, new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "no model"));

        await Assert.ThrowsAsync<WhisperException>(() => service.TranscribeAsync(episode.Id));

        await using var verify = new AppDbContext(_dbOptions);
        var job = await verify.Jobs.SingleAsync();
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no model", job.LastError);
        Assert.Empty(verify.Transcripts);
    }

    [Fact]
    public async Task TranscribeAsync_keeps_earlier_transcripts_when_run_again()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedEpisodeAsync(db);
        var service = CreateService(db, new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson));

        await service.TranscribeAsync(episode.Id);
        await service.TranscribeAsync(episode.Id);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Equal(2, await verify.Transcripts.CountAsync());
    }

    private sealed class FakeHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
