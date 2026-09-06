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
using PodcastTranscription.Web.Services.Maintenance;

namespace PodcastTranscription.Tests;

/// <summary>
/// Retention deletes audio, so the tests are mostly about what it must refuse to delete.
/// </summary>
public class MaintenanceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pts-maint-{Guid.NewGuid():N}");

    public MaintenanceServiceTests()
    {
        Directory.CreateDirectory(_root);

        // A file-backed database, because VACUUM INTO has to write a real copy.
        _connection = new SqliteConnection($"Data Source={Path.Combine(_root, "app.db")}");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private MaintenanceService CreateService(AppDbContext db, MaintenanceOptions options)
    {
        var storage = new StorageOptions { DataPath = _root, MediaPath = Path.Combine(_root, "media") };
        var media = new MediaStore(Options.Create(storage), new FakeHost(_root));

        return new MaintenanceService(db, media, Options.Create(options), Options.Create(storage),
            new FakeHost(_root), NullLogger<MaintenanceService>.Instance);
    }

    /// <summary>An episode with a finished transcript, a source file and a prepared WAV.</summary>
    private async Task<Episode> SeedAsync(AppDbContext db, DateTimeOffset transcribedAt,
        bool complete = true, bool preparedWav = true)
    {
        var episode = new Episode
        {
            Title = "Episode",
            AudioPath = $"source/{Guid.NewGuid():N}.mp3",
            AudioSha256 = Guid.NewGuid().ToString("N"),
            PreparedAudioPath = preparedWav ? $"prepared/{Guid.NewGuid():N}.wav" : null
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        db.Transcripts.Add(new Transcript
        {
            EpisodeId = episode.Id,
            Model = "large-v3-turbo-q5_0",
            IsComplete = complete,
            CreatedAt = transcribedAt
        });
        await db.SaveChangesAsync();

        Write(episode.AudioPath, 4096);
        if (preparedWav)
        {
            Write(episode.PreparedAudioPath!, 8192);
        }

        return episode;
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, "media", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private bool Exists(string? relative) =>
        relative is not null && File.Exists(Path.Combine(_root, "media", relative));

    private static MaintenanceOptions Pruning(int afterDays = 30) => new()
    {
        DeleteSourceAfterTranscription = true,
        DeleteSourceAfterDays = afterDays,
        KeepPreparedWav = true
    };

    [Fact]
    public async Task Nothing_is_deleted_while_retention_is_off()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedAsync(db, DateTimeOffset.UtcNow.AddDays(-90));

        var result = await CreateService(db, new MaintenanceOptions()).PruneSourceAudioAsync();

        Assert.Equal(0, result.EpisodesPruned);
        Assert.True(Exists(episode.AudioPath));
    }

    [Fact]
    public async Task An_old_transcribed_episode_loses_its_source_but_keeps_the_prepared_wav()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedAsync(db, DateTimeOffset.UtcNow.AddDays(-90));

        var result = await CreateService(db, Pruning()).PruneSourceAudioAsync();

        Assert.Equal(1, result.EpisodesPruned);
        Assert.Equal(4096, result.BytesFreed);
        Assert.False(Exists(episode.AudioPath));

        // Diarization reads the prepared WAV, and regenerating it would need the file just deleted.
        Assert.True(Exists(episode.PreparedAudioPath));

        await using var verify = new AppDbContext(_dbOptions);
        Assert.Equal(string.Empty, (await verify.Episodes.SingleAsync()).AudioPath);
    }

    [Fact]
    public async Task An_episode_inside_the_grace_period_is_left_alone()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedAsync(db, DateTimeOffset.UtcNow.AddDays(-3));

        var result = await CreateService(db, Pruning(afterDays: 30)).PruneSourceAudioAsync();

        Assert.Equal(0, result.EpisodesPruned);
        Assert.True(Exists(episode.AudioPath));
    }

    [Fact]
    public async Task An_episode_with_no_transcript_keeps_its_audio()
    {
        await using var db = new AppDbContext(_dbOptions);

        var episode = new Episode
        {
            Title = "Never transcribed",
            AudioPath = "source/orphan.mp3",
            AudioSha256 = "x"
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();
        Write(episode.AudioPath, 2048);

        var result = await CreateService(db, Pruning()).PruneSourceAudioAsync();

        // The audio is the only record of what was said; without a transcript it must survive.
        Assert.Equal(0, result.EpisodesPruned);
        Assert.True(Exists(episode.AudioPath));
    }

    [Fact]
    public async Task An_episode_whose_transcript_is_still_partial_keeps_its_audio()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedAsync(db, DateTimeOffset.UtcNow.AddDays(-90), complete: false);

        var result = await CreateService(db, Pruning()).PruneSourceAudioAsync();

        Assert.Equal(0, result.EpisodesPruned);
        Assert.True(Exists(episode.AudioPath));
    }

    [Fact]
    public async Task Source_is_kept_when_there_is_no_prepared_wav_to_fall_back_on()
    {
        await using var db = new AppDbContext(_dbOptions);
        var episode = await SeedAsync(db, DateTimeOffset.UtcNow.AddDays(-90), preparedWav: false);

        var result = await CreateService(db, Pruning()).PruneSourceAudioAsync();

        // Deleting here would leave the episode with no audio at all.
        Assert.Equal(0, result.EpisodesPruned);
        Assert.True(Exists(episode.AudioPath));
    }

    [Fact]
    public async Task Backup_writes_a_database_that_opens_and_holds_the_data()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, DateTimeOffset.UtcNow);

        var result = await CreateService(db, new MaintenanceOptions()).BackupDatabaseAsync();

        Assert.True(File.Exists(result.Path));
        Assert.True(result.Bytes > 0);

        // A backup that cannot be opened is not a backup.
        await using var restored = new SqliteConnection($"Data Source={result.Path};Mode=ReadOnly");
        await restored.OpenAsync();

        await using var command = restored.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Episodes;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Only_the_configured_number_of_backups_is_kept()
    {
        await using var db = new AppDbContext(_dbOptions);
        var service = CreateService(db, new MaintenanceOptions { BackupsToKeep = 2 });

        // Names carry a timestamp, so pre-existing files stand in for older nightly runs.
        var directory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(directory);
        foreach (var day in new[] { "20260101-000000", "20260102-000000", "20260103-000000" })
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, $"app-{day}.db"), new byte[16]);
        }

        var result = await service.BackupDatabaseAsync();

        Assert.Equal(2, result.Removed);
        Assert.Equal(2, service.ListBackups().Count);
    }

    [Fact]
    public async Task Media_usage_is_measured_per_folder()
    {
        await using var db = new AppDbContext(_dbOptions);
        await SeedAsync(db, DateTimeOffset.UtcNow);

        var (source, prepared) = CreateService(db, new MaintenanceOptions()).MeasureMediaUsage();

        Assert.Equal(4096, source);
        Assert.Equal(8192, prepared);
    }

    private sealed class FakeHost(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
