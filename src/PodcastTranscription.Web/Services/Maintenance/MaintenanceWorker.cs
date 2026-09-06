using System.Globalization;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services.Maintenance;

/// <summary>
/// Runs retention and the database backup once a day, at a quiet hour. One worker for both
/// because they are the same kind of work: slow, disk-bound housekeeping that must not collide
/// with transcription.
/// </summary>
public class MaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MaintenanceOptions> options,
    ILogger<MaintenanceWorker> log) : BackgroundService
{
    private readonly MaintenanceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            log.LogInformation("Maintenance is disabled");
            return;
        }

        var runAt = ParseRunAt(_options.RunAt);
        log.LogInformation("Maintenance scheduled daily at {RunAt}", runAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNext(runAt);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<MaintenanceService>();

            await maintenance.PruneSourceAudioAsync(ct);

            if (_options.BackupDatabase)
            {
                await maintenance.BackupDatabaseAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Housekeeping failing must never take the app down with it.
            log.LogError(ex, "Maintenance run failed");
        }
    }

    /// <summary>Falls back to a sensible hour rather than throwing on a malformed setting.</summary>
    internal static TimeOnly ParseRunAt(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : new TimeOnly(3, 30);

    /// <summary>
    /// Time until the next occurrence of that local time. Uses local time so "03:30" means the
    /// small hours where the server actually is.
    /// </summary>
    internal static TimeSpan TimeUntilNext(TimeOnly runAt, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var next = current.Date.Add(runAt.ToTimeSpan());

        if (next <= current)
        {
            next = next.AddDays(1);
        }

        return next - current;
    }
}
