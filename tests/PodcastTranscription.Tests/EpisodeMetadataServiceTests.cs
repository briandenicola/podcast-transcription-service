using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Tests;

public class EpisodeMetadataServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EpisodeMetadataServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Update_trims_and_saves_title_and_show()
    {
        await using var db = new AppDbContext(_options);
        var episode = new Episode { Title = "Old", Show = "Old show" };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var service = new EpisodeMetadataService(db, NullLogger<EpisodeMetadataService>.Instance);
        var result = await service.UpdateAsync(episode.Id, "  Better title  ", "  Better show  ");

        Assert.True(result.Updated);
        Assert.Equal("Better title", episode.Title);
        Assert.Equal("Better show", episode.Show);
    }

    [Fact]
    public async Task Blank_show_clears_it()
    {
        await using var db = new AppDbContext(_options);
        var episode = new Episode { Title = "Episode", Show = "Old show" };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var service = new EpisodeMetadataService(db, NullLogger<EpisodeMetadataService>.Instance);
        var result = await service.UpdateAsync(episode.Id, "Episode", "   ");

        Assert.True(result.Updated);
        Assert.Null(episode.Show);
    }

    [Fact]
    public async Task Blank_title_is_rejected_without_changing_the_episode()
    {
        await using var db = new AppDbContext(_options);
        var episode = new Episode { Title = "Keep me", Show = "Show" };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var service = new EpisodeMetadataService(db, NullLogger<EpisodeMetadataService>.Instance);
        var result = await service.UpdateAsync(episode.Id, "   ", "Changed");

        Assert.False(result.Updated);
        Assert.Equal("Give the episode a title.", result.Error);
        Assert.Equal("Keep me", episode.Title);
        Assert.Equal("Show", episode.Show);
    }

    [Fact]
    public async Task Missing_episode_is_reported()
    {
        await using var db = new AppDbContext(_options);
        var service = new EpisodeMetadataService(db, NullLogger<EpisodeMetadataService>.Instance);

        var result = await service.UpdateAsync(404, "Title", "Show");

        Assert.True(result.NotFound);
        Assert.False(result.Updated);
    }
}
