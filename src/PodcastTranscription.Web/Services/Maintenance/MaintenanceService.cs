using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;

namespace PodcastTranscription.Web.Services.Maintenance;

public record RetentionResult(int EpisodesPruned, long BytesFreed);

public record BackupResult(string Path, long Bytes, int Removed);

/// <summary>
/// Retention and backups. Separated from the worker that schedules them so both can be run on
/// demand from the settings page — a backup you cannot take now is not much of a backup.
/// </summary>
public class MaintenanceService(
    AppDbContext db,
    MediaStore media,
    IOptions<MaintenanceOptions> options,
    IOptions<StorageOptions> storage,
    IHostEnvironment environment,
    ILogger<MaintenanceService> log)
{
    private readonly MaintenanceOptions _options = options.Value;
    private readonly StorageOptions _storage = storage.Value;

    /// <summary>
    /// Deletes source audio for episodes that have a finished transcript and are past the grace
    /// period. Only ever touches the source: an episode with no complete transcript keeps
    /// everything, because the audio is the only copy of what was said.
    /// </summary>
    public async Task<RetentionResult> PruneSourceAudioAsync(CancellationToken ct = default)
    {
        if (!_options.DeleteSourceAfterTranscription)
        {
            return new RetentionResult(0, 0);
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, _options.DeleteSourceAfterDays));

        var candidates = await db.Episodes
            .Where(e => e.AudioPath != ""
                     && e.Transcripts.Any(t => t.IsComplete && t.CreatedAt < cutoff))
            .Select(e => new { e.Id, e.AudioPath, e.PreparedAudioPath })
            .ToListAsync(ct);

        var pruned = 0;
        long freed = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Never leave an episode with neither a source nor a prepared WAV: that is an episode
            // whose audio is simply gone.
            if (_options.KeepPreparedWav && !media.Exists(candidate.PreparedAudioPath))
            {
                log.LogDebug("Keeping source for episode {EpisodeId}: no prepared WAV to fall back on",
                    candidate.Id);
                continue;
            }

            var path = media.Resolve(candidate.AudioPath);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                freed += new FileInfo(path).Length;
                File.Delete(path);

                var episode = await db.Episodes.FirstAsync(e => e.Id == candidate.Id, ct);
                episode.AudioPath = string.Empty;
                pruned++;
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Could not delete source audio for episode {EpisodeId}", candidate.Id);
            }
        }

        if (pruned > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Retention pruned {Count} source file(s), freeing {Mb:N1} MB",
                pruned, freed / 1024.0 / 1024.0);
        }

        return new RetentionResult(pruned, freed);
    }

    /// <summary>
    /// Backs the database up with <c>VACUUM INTO</c>, which writes a consistent, already-compacted
    /// copy while the app keeps running — unlike copying the file, which can catch it mid-write.
    /// </summary>
    public async Task<BackupResult> BackupDatabaseAsync(CancellationToken ct = default)
    {
        var directory = Path.Combine(ResolveDataDirectory(), "backups");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"app-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

        // VACUUM INTO refuses to overwrite, so a stale file from a failed run must go first.
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var connection = db.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VACUUM INTO $path;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$path";
            parameter.Value = path;
            command.Parameters.Add(parameter);

            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
            }

            await command.ExecuteNonQueryAsync(ct);
        }

        var bytes = new FileInfo(path).Length;
        var removed = PruneOldBackups(directory);

        log.LogInformation("Database backed up to {Path} ({Mb:N1} MB); removed {Removed} old backup(s)",
            path, bytes / 1024.0 / 1024.0, removed);

        return new BackupResult(path, bytes, removed);
    }

    public List<FileInfo> ListBackups()
    {
        var directory = Path.Combine(ResolveDataDirectory(), "backups");

        return Directory.Exists(directory)
            ? new DirectoryInfo(directory).GetFiles("app-*.db").OrderByDescending(f => f.Name).ToList()
            : [];
    }

    /// <summary>Total bytes under the media root, for the settings page.</summary>
    public (long SourceBytes, long PreparedBytes) MeasureMediaUsage()
    {
        return (Measure("source"), Measure("prepared"));

        long Measure(string folder)
        {
            var path = Path.Combine(media.Root, folder);
            if (!Directory.Exists(path))
            {
                return 0;
            }

            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
    }

    private int PruneOldBackups(string directory)
    {
        var keep = Math.Max(1, _options.BackupsToKeep);

        var stale = new DirectoryInfo(directory)
            .GetFiles("app-*.db")
            .OrderByDescending(f => f.Name)
            .Skip(keep)
            .ToList();

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Could not delete old backup {Path}", file.FullName);
            }
        }

        return stale.Count;
    }

    private string ResolveDataDirectory() =>
        Path.IsPathRooted(_storage.DataPath)
            ? _storage.DataPath
            : Path.Combine(environment.ContentRootPath, _storage.DataPath);
}
