using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;

namespace PodcastTranscription.Web.Services.Ingest;

/// <summary>
/// Checks subscribed feeds on a timer and takes anything new. Deliberately separate from the
/// transcription worker: polling is quick and network-bound, transcription is long and
/// GPU-bound, and neither should be able to hold the other up.
/// </summary>
public class FeedPoller(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestOptions> options,
    ILogger<FeedPoller> log) : BackgroundService
{
    private readonly IngestOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PollFeeds)
        {
            log.LogInformation("Feed polling is disabled; feeds can still be polled by hand");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.InitialPollDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.PollIntervalMinutes));
        log.LogInformation("Feed poller starting; every {Interval}", interval);

        using var timer = new PeriodicTimer(interval);

        do
        {
            await PollAllAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        log.LogInformation("Feed poller stopped");
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var feeds = scope.ServiceProvider.GetRequiredService<FeedService>();

            var feedIds = await db.Feeds.Select(f => f.Id).ToListAsync(ct);

            foreach (var feedId in feedIds)
            {
                ct.ThrowIfCancellationRequested();

                // PollAsync records its own failures against the feed, so one unreachable feed
                // does not stop the others being checked.
                await feeds.PollAsync(feedId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Feed poll round failed");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
