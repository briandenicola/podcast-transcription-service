using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Tests;

/// <summary>
/// Whether a Pushover notification actually goes out — gated on both the feed's opt-in and
/// credentials being configured — and that saving settings never blanks a field left empty.
/// </summary>
public class PushoverNotifierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public PushoverNotifierTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static PushoverNotifier Create(AppDbContext db, FakeHttpClientFactory factory) =>
        new(db, factory, NullLogger<PushoverNotifier>.Instance);

    private async Task<int> SeedEpisodeAsync(bool feedOptedIn)
    {
        await using var db = new AppDbContext(_dbOptions);

        var feed = new Feed { Title = "The Build Log", RssUrl = "https://example.com/feed.xml", NotifyPushover = feedOptedIn };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync();

        var episode = new Episode
        {
            Title = "Episode one",
            AudioPath = "a.mp3",
            AudioSha256 = new string('a', 64),
            FeedId = feed.Id
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        return episode.Id;
    }

    [Fact]
    public async Task No_notification_when_the_feed_has_not_opted_in()
    {
        await using var db = new AppDbContext(_dbOptions);
        db.PushoverSettings.Add(new PushoverSettings { AppToken = "token", UserKey = "user" });
        await db.SaveChangesAsync();

        var episodeId = await SeedEpisodeAsync(feedOptedIn: false);
        var factory = new FakeHttpClientFactory(() => "{}");

        await Create(db, factory).NotifyEpisodeAsync(episodeId, "Transcribed");

        Assert.Empty(factory.RequestedUrls);
    }

    [Fact]
    public async Task No_notification_when_credentials_are_not_configured()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episodeId = await SeedEpisodeAsync(feedOptedIn: true);
        var factory = new FakeHttpClientFactory(() => "{}");

        await Create(db, factory).NotifyEpisodeAsync(episodeId, "Transcribed");

        Assert.Empty(factory.RequestedUrls);
    }

    [Fact]
    public async Task Sends_when_the_feed_opted_in_and_credentials_are_set()
    {
        await using var db = new AppDbContext(_dbOptions);
        db.PushoverSettings.Add(new PushoverSettings { AppToken = "token", UserKey = "user" });
        await db.SaveChangesAsync();

        var episodeId = await SeedEpisodeAsync(feedOptedIn: true);
        var factory = new FakeHttpClientFactory(() => "{}");

        await Create(db, factory).NotifyEpisodeAsync(episodeId, "Transcribed");

        Assert.Single(factory.RequestedUrls);
        Assert.Contains("pushover.net", factory.RequestedUrls[0]);
    }

    [Fact]
    public async Task A_failed_send_does_not_throw()
    {
        await using var db = new AppDbContext(_dbOptions);
        db.PushoverSettings.Add(new PushoverSettings { AppToken = "token", UserKey = "user" });
        await db.SaveChangesAsync();

        var episodeId = await SeedEpisodeAsync(feedOptedIn: true);
        var factory = new FakeHttpClientFactory(() => "not found", HttpStatusCode.NotFound);

        // Must complete without throwing, even though Pushover rejected the request.
        await Create(db, factory).NotifyEpisodeAsync(episodeId, "Transcribed");

        Assert.Single(factory.RequestedUrls);
    }

    [Fact]
    public async Task Saving_a_blank_field_keeps_the_existing_value()
    {
        await using var db = new AppDbContext(_dbOptions);
        var factory = new FakeHttpClientFactory(() => "{}");
        var notifier = Create(db, factory);

        await notifier.SaveSettingsAsync("original-token", "original-user");
        await notifier.SaveSettingsAsync(appToken: null, userKey: "updated-user");

        await using var verify = new AppDbContext(_dbOptions);
        var settings = await verify.PushoverSettings.SingleAsync();

        Assert.Equal("original-token", settings.AppToken);
        Assert.Equal("updated-user", settings.UserKey);
    }

    [Fact]
    public async Task IsConfigured_is_false_until_both_fields_are_set()
    {
        await using var db = new AppDbContext(_dbOptions);
        var notifier = Create(db, new FakeHttpClientFactory(() => "{}"));

        Assert.False(await notifier.IsConfiguredAsync());

        await notifier.SaveSettingsAsync("token", null);
        Assert.False(await notifier.IsConfiguredAsync());

        await notifier.SaveSettingsAsync(null, "user");
        Assert.True(await notifier.IsConfiguredAsync());
    }
}
