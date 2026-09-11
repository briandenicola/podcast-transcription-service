using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Tests;

/// <summary>
/// Subscribing and polling against a real database and a canned feed.
///
/// The behaviour under test is the deliberate one: a poll takes new episodes only. Subscribing to
/// a show with years of back catalogue must record that history without queueing it, or one
/// subscription would swamp the queue.
/// </summary>
public class FeedServiceTests : IDisposable
{
    private const string FeedUrl = "https://example.com/feed.xml";

    private static string FeedWith(params (string Guid, string Title)[] items)
    {
        var entries = string.Join("\n", items.Select(i => $"""
              <item>
                <title>{i.Title}</title>
                <guid>{i.Guid}</guid>
                <pubDate>Tue, 03 Mar 2026 09:00:00 +0000</pubDate>
                <enclosure url="https://example.com/audio/{i.Guid}.mp3" type="audio/mpeg"/>
              </item>
            """));

        return $"""
            <rss version="2.0"><channel>
              <title>The Build Log</title>
            {entries}
            </channel></rss>
            """;
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public FeedServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private static JobQueue CreateQueue(AppDbContext db)
    {
        var whisperOptions = new WhisperOptions { BaseUrl = "http://whisper.test:8080", Model = "large-v3-turbo-q5_0" };
        var whisper = new WhisperClient(
            new HttpClient { BaseAddress = new Uri("http://whisper.test:8080/") },
            Options.Create(whisperOptions), NullLogger<WhisperClient>.Instance);

        return new JobQueue(db, whisper, new RunningJobs(), new JobNotifier(),
            Options.Create(new TranscriptionOptions()), NullLogger<JobQueue>.Instance);
    }

    private static FeedService CreateService(AppDbContext db, IHttpClientFactory http, IngestOptions? options = null) =>
        new(db, CreateQueue(db), http,
            new PodcastUrlResolver(http, NullLogger<PodcastUrlResolver>.Instance),
            Options.Create(options ?? new IngestOptions()),
            NullLogger<FeedService>.Instance);

    [Fact]
    public async Task Subscribing_records_the_back_catalogue_without_queueing_any_of_it()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("1", "Ep. 1"), ("2", "Ep. 2"), ("3", "Ep. 3")));

        var feed = await CreateService(db, http).SubscribeAsync(FeedUrl);

        Assert.Equal("The Build Log", feed.Title);
        Assert.Equal(3, await db.Episodes.CountAsync());

        // The whole point: history is known about, not queued.
        Assert.Equal(0, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task Polling_after_subscribing_queues_only_what_is_genuinely_new()
    {
        await using var db = new AppDbContext(_dbOptions);

        var feedXml = FeedWith(("1", "Ep. 1"), ("2", "Ep. 2"));
        var http = new FakeHttpClientFactory(() => feedXml);
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl);

        // The publisher releases another episode.
        feedXml = FeedWith(("1", "Ep. 1"), ("2", "Ep. 2"), ("3", "Ep. 3"));

        var outcome = await service.PollAsync(feed.Id);

        Assert.Equal(1, outcome.Discovered);
        Assert.Equal(1, outcome.Queued);
        Assert.Equal(3, await db.Episodes.CountAsync());

        var job = await db.Jobs.Include(j => j.Episode).SingleAsync();
        Assert.Equal("Ep. 3", job.Episode!.Title);
    }

    [Fact]
    public async Task Polling_an_unchanged_feed_adds_nothing()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("1", "Ep. 1"), ("2", "Ep. 2")));
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl);

        var first = await service.PollAsync(feed.Id);
        var second = await service.PollAsync(feed.Id);

        Assert.Equal(0, first.Discovered);
        Assert.Equal(0, second.Discovered);
        Assert.Equal(2, await db.Episodes.CountAsync());
    }

    [Fact]
    public async Task A_feed_with_auto_transcribe_off_records_without_queueing()
    {
        await using var db = new AppDbContext(_dbOptions);

        var feedXml = FeedWith(("1", "Ep. 1"));
        var http = new FakeHttpClientFactory(() => feedXml);
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl, autoTranscribe: false);
        feedXml = FeedWith(("1", "Ep. 1"), ("2", "Ep. 2"));

        var outcome = await service.PollAsync(feed.Id);

        Assert.Equal(1, outcome.Discovered);
        Assert.Equal(0, outcome.Queued);
        Assert.Equal(0, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task New_episodes_use_the_system_model_and_inherit_other_per_show_defaults()
    {
        await using var db = new AppDbContext(_dbOptions);

        var feedXml = FeedWith(("1", "Ep. 1"));
        var http = new FakeHttpClientFactory(() => feedXml);
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl);
        feed.DefaultModel = "large-v3";
        feed.DefaultLanguage = "en";
        feed.DefaultPrompt = "Kara Swisher, Scott Galloway, EBITDA";
        await db.SaveChangesAsync();

        feedXml = FeedWith(("1", "Ep. 1"), ("2", "Ep. 2"));
        await service.PollAsync(feed.Id);

        var job = await db.Jobs.SingleAsync();
        Assert.Equal("large-v3-turbo-q5_0", job.Model);
        Assert.Equal("en", job.Language);
        Assert.Equal("Kara Swisher, Scott Galloway, EBITDA", job.Prompt);
    }

    [Fact]
    public async Task Episodes_record_the_feed_the_show_name_and_the_audio_url()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("abc", "Ep. 1")));

        var feed = await CreateService(db, http).SubscribeAsync(FeedUrl);
        var episode = await db.Episodes.SingleAsync();

        Assert.Equal(feed.Id, episode.FeedId);
        Assert.Equal("The Build Log", episode.Show);
        Assert.Equal("abc", episode.FeedItemGuid);
        Assert.Equal("https://example.com/audio/abc.mp3", episode.SourceUrl);

        // Nothing has been downloaded yet, so there is no file and no hash.
        Assert.Equal(string.Empty, episode.AudioPath);
        Assert.Equal(string.Empty, episode.AudioSha256);
    }

    [Fact]
    public async Task Subscribing_to_a_feed_already_subscribed_returns_the_existing_one()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("1", "Ep. 1")));
        var service = CreateService(db, http);

        var first = await service.SubscribeAsync(FeedUrl);
        var second = await service.SubscribeAsync(FeedUrl);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Feeds.CountAsync());
    }

    [Fact]
    public async Task A_url_that_is_not_http_is_refused()
    {
        await using var db = new AppDbContext(_dbOptions);
        var service = CreateService(db, new FakeHttpClientFactory(() => ""));

        await Assert.ThrowsAsync<IngestException>(() => service.SubscribeAsync("file:///etc/passwd"));
    }

    [Fact]
    public async Task A_failing_poll_is_recorded_on_the_feed_rather_than_thrown()
    {
        await using var db = new AppDbContext(_dbOptions);

        var feedXml = FeedWith(("1", "Ep. 1"));
        var http = new FakeHttpClientFactory(() => feedXml);
        var service = CreateService(db, http);
        var feed = await service.SubscribeAsync(FeedUrl);

        // The publisher's server starts returning junk.
        feedXml = "<html>502 Bad Gateway</html>";

        var outcome = await service.PollAsync(feed.Id);

        Assert.NotNull(outcome.Error);
        Assert.Equal(0, outcome.Discovered);

        var stored = await db.Feeds.SingleAsync();
        Assert.NotNull(stored.LastError);
        Assert.NotNull(stored.LastPolledAt);
    }

    [Fact]
    public async Task A_successful_poll_clears_a_previous_error()
    {
        await using var db = new AppDbContext(_dbOptions);

        var feedXml = FeedWith(("1", "Ep. 1"));
        var http = new FakeHttpClientFactory(() => feedXml);
        var service = CreateService(db, http);
        var feed = await service.SubscribeAsync(FeedUrl);

        feedXml = "<html>502</html>";
        await service.PollAsync(feed.Id);
        Assert.NotNull((await db.Feeds.SingleAsync()).LastError);

        feedXml = FeedWith(("1", "Ep. 1"));
        await service.PollAsync(feed.Id);

        db.ChangeTracker.Clear();
        Assert.Null((await db.Feeds.SingleAsync()).LastError);
    }

    [Fact]
    public async Task Backfill_queues_recorded_episodes_that_have_never_been_transcribed()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() =>
            FeedWith(("1", "Ep. 1"), ("2", "Ep. 2"), ("3", "Ep. 3"), ("4", "Ep. 4")));
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl);
        Assert.Equal(0, await db.Jobs.CountAsync());

        var queued = await service.BackfillAsync(feed.Id, 2);

        Assert.Equal(2, queued);
        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task Backfill_does_not_queue_an_episode_twice()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("1", "Ep. 1"), ("2", "Ep. 2")));
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl);

        await service.BackfillAsync(feed.Id, 10);
        var second = await service.BackfillAsync(feed.Id, 10);

        Assert.Equal(0, second);
        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task One_poll_will_not_accept_more_items_than_its_cap()
    {
        await using var db = new AppDbContext(_dbOptions);
        var items = Enumerable.Range(1, 30).Select(i => (i.ToString(), $"Ep. {i}")).ToArray();
        var http = new FakeHttpClientFactory(() => FeedWith(items));

        var service = CreateService(db, http, new IngestOptions { MaxItemsPerPoll = 10 });
        await service.SubscribeAsync(FeedUrl);

        Assert.Equal(10, await db.Episodes.CountAsync());
    }

    [Fact]
    public async Task Subscribing_can_be_configured_to_queue_the_existing_catalogue()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("1", "Ep. 1"), ("2", "Ep. 2")));

        var service = CreateService(db, http, new IngestOptions { QueueExistingItemsOnSubscribe = true });
        await service.SubscribeAsync(FeedUrl);

        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task A_deleted_episode_does_not_come_back_on_the_next_poll()
    {
        await using var db = new AppDbContext(_dbOptions);
        var http = new FakeHttpClientFactory(() => FeedWith(("1", "Ep. 1"), ("2", "Ep. 2")));
        var service = CreateService(db, http);

        var feed = await service.SubscribeAsync(FeedUrl);
        var episode = await db.Episodes.SingleAsync(e => e.FeedItemGuid == "2");

        // Someone deletes the episode; the feed itself never changes.
        var deletion = new DeletionService(
            db, new MediaStore(Options.Create(new StorageOptions()), new FakeHost()),
            new RunningJobs(), new JobNotifier(), NullLogger<DeletionService>.Instance);
        await deletion.DeleteEpisodeAsync(episode.Id);

        var outcome = await service.PollAsync(feed.Id);

        Assert.Equal(0, outcome.Discovered);
        Assert.Equal(1, await db.Episodes.CountAsync());
        Assert.Null(await db.Episodes.FirstOrDefaultAsync(e => e.FeedItemGuid == "2"));
    }

    private sealed class FakeHost : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
