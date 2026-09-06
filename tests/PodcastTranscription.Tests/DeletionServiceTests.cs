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
using PodcastTranscription.Web.Services.Search;

namespace PodcastTranscription.Tests;

/// <summary>
/// Deletion against a migrated database, so the FTS triggers are the real ones. The index living
/// in a separate table is the thing most likely to rot: nothing in C# would notice search still
/// returning hits for an episode that is gone.
/// </summary>
public class DeletionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly string _mediaRoot = Path.Combine(Path.GetTempPath(), $"pts-del-{Guid.NewGuid():N}");
    private readonly RunningJobs _running = new();

    public DeletionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();

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

    private DeletionService CreateService(AppDbContext db) =>
        new(db,
            new MediaStore(Options.Create(new StorageOptions { MediaPath = _mediaRoot }), new FakeHost(_mediaRoot)),
            _running, new JobNotifier(), NullLogger<DeletionService>.Instance);

    private SearchService CreateSearch(AppDbContext db) => new(db, NullLogger<SearchService>.Instance);

    private void WriteMedia(string relative)
    {
        var path = Path.Combine(_mediaRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[512]);
    }

    private bool MediaExists(string relative) => File.Exists(Path.Combine(_mediaRoot, relative));

    /// <summary>An episode with audio, a completed job, a transcript and indexed segments.</summary>
    private async Task<(Episode Episode, Transcript Transcript, Job Job)> SeedAsync(
        AppDbContext db, JobState jobState = JobState.Completed, string text = "a mention of pricing")
    {
        var episode = new Episode
        {
            Title = "Episode",
            AudioPath = $"source/{Guid.NewGuid():N}.mp3",
            PreparedAudioPath = $"prepared/{Guid.NewGuid():N}.wav",
            AudioSha256 = Guid.NewGuid().ToString("N")
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        WriteMedia(episode.AudioPath);
        WriteMedia(episode.PreparedAudioPath!);

        var transcript = new Transcript { EpisodeId = episode.Id, Model = "large-v3-turbo", IsComplete = true };
        db.Transcripts.Add(transcript);
        await db.SaveChangesAsync();

        db.Segments.Add(new Segment { TranscriptId = transcript.Id, Ordinal = 0, StartMs = 0, EndMs = 5000, Text = text });
        db.TranscriptChunks.Add(new TranscriptChunk
        {
            TranscriptId = transcript.Id, Index = 0, StartMs = 0, EndMs = 5000, RawJson = "{}"
        });

        var job = new Job
        {
            EpisodeId = episode.Id, State = jobState, Model = "large-v3-turbo", TranscriptId = transcript.Id
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        return (episode, transcript, job);
    }

    [Fact]
    public async Task Deleting_an_episode_removes_everything_derived_from_it()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, _, _) = await SeedAsync(db);

        var result = await CreateService(db).DeleteEpisodeAsync(episode.Id);

        Assert.True(result.Deleted);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Empty(verify.Episodes);
        Assert.Empty(verify.Transcripts);
        Assert.Empty(verify.Segments);
        Assert.Empty(verify.TranscriptChunks);
        Assert.Empty(verify.Jobs);
    }

    /// <summary>
    /// The index is a separate table kept in step by triggers. If a cascade removed the segments
    /// without firing them, search would keep returning a deleted episode.
    /// </summary>
    [Fact]
    public async Task Deleting_an_episode_removes_it_from_the_search_index()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, _, _) = await SeedAsync(db, text: "we should talk about pricing today");

        Assert.Equal(1, (await CreateSearch(db).SearchAsync("pricing")).TotalHits);

        await CreateService(db).DeleteEpisodeAsync(episode.Id);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Equal(0, (await CreateSearch(verify).SearchAsync("pricing")).TotalHits);
    }

    [Fact]
    public async Task Deleting_an_episode_removes_its_audio()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, _, _) = await SeedAsync(db);

        await CreateService(db).DeleteEpisodeAsync(episode.Id);

        Assert.False(MediaExists(episode.AudioPath));
        Assert.False(MediaExists(episode.PreparedAudioPath!));
    }

    [Fact]
    public async Task An_episode_with_a_running_job_is_refused()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, _, _) = await SeedAsync(db, JobState.Transcribing);

        var result = await CreateService(db).DeleteEpisodeAsync(episode.Id);

        Assert.False(result.Deleted);
        Assert.Contains("Cancel it first", result.Refusal);

        // Nothing was half-removed on the way to refusing.
        await using var verify = new AppDbContext(_dbOptions);
        Assert.Single(verify.Episodes);
        Assert.Single(verify.Segments);
        Assert.True(MediaExists(episode.AudioPath));
    }

    [Fact]
    public async Task Deleting_a_transcript_keeps_the_episode_and_its_audio()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, transcript, _) = await SeedAsync(db);

        var result = await CreateService(db).DeleteTranscriptAsync(transcript.Id);

        Assert.True(result.Deleted);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Single(verify.Episodes);
        Assert.Empty(verify.Transcripts);
        Assert.Empty(verify.Segments);

        // The point of deleting only the transcript: the episode can be transcribed again.
        Assert.True(MediaExists(episode.AudioPath));
        Assert.True(MediaExists(episode.PreparedAudioPath!));
    }

    [Fact]
    public async Task Deleting_a_transcript_keeps_the_job_that_produced_it()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, transcript, job) = await SeedAsync(db);

        await CreateService(db).DeleteTranscriptAsync(transcript.Id);

        await using var verify = new AppDbContext(_dbOptions);
        var stored = await verify.Jobs.SingleAsync();

        Assert.Equal(job.Id, stored.Id);
        Assert.Null(stored.TranscriptId);
    }

    [Fact]
    public async Task Deleting_a_transcript_removes_only_its_own_segments()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, first, _) = await SeedAsync(db, text: "the first run");

        var second = new Transcript { EpisodeId = episode.Id, Model = "large-v3", IsComplete = true };
        db.Transcripts.Add(second);
        await db.SaveChangesAsync();
        db.Segments.Add(new Segment { TranscriptId = second.Id, Ordinal = 0, StartMs = 0, EndMs = 1, Text = "the second run" });
        await db.SaveChangesAsync();

        await CreateService(db).DeleteTranscriptAsync(first.Id);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Single(verify.Transcripts);
        Assert.Equal("the second run", (await verify.Segments.SingleAsync()).Text);
        Assert.Equal(0, (await CreateSearch(verify).SearchAsync("\"first\"")).TotalHits);
        Assert.Equal(1, (await CreateSearch(verify).SearchAsync("\"second\"")).TotalHits);
    }

    [Fact]
    public async Task A_transcript_a_running_job_is_writing_to_is_refused()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, transcript, _) = await SeedAsync(db, JobState.Transcribing);

        var result = await CreateService(db).DeleteTranscriptAsync(transcript.Id);

        Assert.False(result.Deleted);
        Assert.Contains("Cancel it first", result.Refusal);
    }

    [Fact]
    public async Task A_finished_job_can_be_deleted_without_touching_the_transcript()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, transcript, job) = await SeedAsync(db);

        var result = await CreateService(db).DeleteJobAsync(job.Id);

        Assert.True(result.Deleted);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Empty(verify.Jobs);
        Assert.Single(verify.Transcripts);
        Assert.Single(verify.Segments);
        Assert.Equal(transcript.Id, (await verify.Transcripts.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Downloading)]
    [InlineData(JobState.Preparing)]
    [InlineData(JobState.Transcribing)]
    public async Task An_unfinished_job_is_refused(JobState state)
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, _, job) = await SeedAsync(db, state);

        var result = await CreateService(db).DeleteJobAsync(job.Id);

        Assert.False(result.Deleted);
        Assert.Contains("Cancel the job", result.Refusal);
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public async Task A_settled_job_can_be_deleted(JobState state)
    {
        await using var db = new AppDbContext(_dbOptions);
        var (_, _, job) = await SeedAsync(db, state);

        Assert.True((await CreateService(db).DeleteJobAsync(job.Id)).Deleted);
    }

    [Fact]
    public async Task Deleting_something_that_is_already_gone_says_so()
    {
        await using var db = new AppDbContext(_dbOptions);
        var service = CreateService(db);

        Assert.Contains("no longer exists", (await service.DeleteEpisodeAsync(999)).Refusal);
        Assert.Contains("no longer exists", (await service.DeleteTranscriptAsync(999)).Refusal);
        Assert.Contains("no longer exists", (await service.DeleteJobAsync(999)).Refusal);
    }

    [Fact]
    public async Task An_episode_whose_audio_is_already_gone_still_deletes()
    {
        await using var db = new AppDbContext(_dbOptions);
        var (episode, _, _) = await SeedAsync(db);

        // Retention may already have pruned the source.
        File.Delete(Path.Combine(_mediaRoot, episode.AudioPath));

        Assert.True((await CreateService(db).DeleteEpisodeAsync(episode.Id)).Deleted);

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Empty(verify.Episodes);
    }

    private sealed class FakeHost(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
