using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Drains the job table. One loop by default: a second concurrent request to a single
/// whisper-server only queues inside it and makes the realtime numbers meaningless. Worker count
/// is configurable so several whisper backends can be fed later.
/// </summary>
public class TranscriptionWorker(
    IServiceScopeFactory scopeFactory,
    RunningJobs running,
    JobNotifier notifier,
    IOptions<TranscriptionOptions> options,
    ILogger<TranscriptionWorker> log) : BackgroundService
{
    private readonly TranscriptionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverOrphanedJobsAsync(stoppingToken);

        var workers = Math.Max(1, _options.WorkerCount);
        log.LogInformation("Transcription worker starting with {Count} loop(s)", workers);

        var loops = Enumerable.Range(0, workers).Select(i => LoopAsync(i, stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task RecoverOrphanedJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
            await queue.RecoverOrphanedJobsAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Startup recovery failed");
        }
    }

    private async Task LoopAsync(int workerIndex, CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await ClaimNextJobAsync(stoppingToken);
                if (jobId is null)
                {
                    await Task.Delay(idleDelay, stoppingToken);
                    continue;
                }

                await RunJobAsync(jobId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad job kill the loop.
                log.LogError(ex, "Worker {Worker} hit an unexpected error", workerIndex);
                await Task.Delay(idleDelay, CancellationToken.None);
            }
        }

        log.LogInformation("Worker {Worker} stopped", workerIndex);
    }

    /// <summary>
    /// Takes the oldest job that is due, moving it out of Queued in the same statement that
    /// selects it. The State check in the UPDATE is what stops two loops claiming one job.
    /// </summary>
    private async Task<int?> ClaimNextJobAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;

        var candidate = await db.Jobs
            .Where(j => j.State == JobState.Queued && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(ct);

        if (candidate == 0)
        {
            return null;
        }

        var claimed = await db.Jobs
            .Where(j => j.Id == candidate && j.State == JobState.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.State, JobState.Preparing)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);

        return claimed == 1 ? candidate : null;
    }

    private async Task RunJobAsync(int jobId, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var _ = running.Track(jobId, cts);

        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<TranscriptionPipeline>();

        try
        {
            notifier.Notify(jobId);
            await pipeline.RunAsync(jobId, cts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down, not a user cancel. Leave the job in a working state so startup
            // recovery re-queues it and it resumes from the chunk it reached.
            log.LogInformation("Job {JobId} interrupted by shutdown; it will resume on restart", jobId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(jobId, JobState.Cancelled, error: null);
            log.LogInformation("Job {JobId} cancelled", jobId);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(jobId, ex);
        }
    }

    /// <summary>
    /// Re-queues with exponential backoff until the attempt cap, then leaves the job Failed with
    /// the error the UI shows. Completed chunks are kept either way, so a retry resumes.
    /// </summary>
    private async Task HandleFailureAsync(int jobId, Exception exception)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
        if (job is null)
        {
            return;
        }

        job.LastError = exception.Message;

        if (job.Attempts < _options.MaxAttempts)
        {
            var delay = queue.BackoffFor(job.Attempts);
            job.State = JobState.Queued;
            job.NextAttemptAt = DateTimeOffset.UtcNow.Add(delay);

            log.LogWarning(exception, "Job {JobId} failed on attempt {Attempt}; retrying in {Delay}",
                jobId, job.Attempts, delay);
        }
        else
        {
            job.State = JobState.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;

            log.LogError(exception, "Job {JobId} failed after {Attempts} attempts", jobId, job.Attempts);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        notifier.Notify(jobId);
    }

    private async Task FinishAsync(int jobId, JobState state, string? error)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
        if (job is null)
        {
            return;
        }

        job.State = state;
        job.LastError = error;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        notifier.Notify(jobId);
    }
}
