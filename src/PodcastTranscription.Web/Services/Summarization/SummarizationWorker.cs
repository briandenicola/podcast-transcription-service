using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// Drains manually-queued summarisation jobs (<see cref="JobKind.Summarization"/>) with a bounded
/// number of concurrent loops, the same way <see cref="TranscriptionWorker"/> drains transcription
/// jobs — same table, same claim-then-run shape, same cancel/retry endpoints and Jobs page. Kept
/// as a separate worker rather than folded into <see cref="TranscriptionWorker"/> because a
/// summarisation job skips straight to <see cref="JobState.Summarizing"/> (no download, no
/// chunk plan, no whisper health gate) and is capped by a different setting
/// (<see cref="OllamaOptions.MaxConcurrentSummaries"/>) than transcription's worker count.
/// </summary>
public class SummarizationWorker(
    IServiceScopeFactory scopeFactory,
    RunningJobs running,
    JobNotifier notifier,
    IOptions<OllamaOptions> options,
    ILogger<SummarizationWorker> log) : BackgroundService
{
    private readonly OllamaOptions _options = options.Value;

    /// <summary>How long the loop sleeps when it finds no work, same as transcription's default.</summary>
    private const int PollSeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverOrphanedJobsAsync(stoppingToken);

        var workers = Math.Max(1, _options.MaxConcurrentSummaries);
        log.LogInformation("Summarisation worker starting with {Count} loop(s)", workers);

        var loops = Enumerable.Range(0, workers).Select(i => LoopAsync(i, stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task RecoverOrphanedJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
            await queue.RecoverOrphanedJobsAsync(JobKind.Summarization, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Startup recovery failed");
        }
    }

    private async Task LoopAsync(int workerIndex, CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(PollSeconds);

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
                log.LogError(ex, "Summarisation worker {Worker} hit an unexpected error", workerIndex);
                await Task.Delay(idleDelay, CancellationToken.None);
            }
        }

        log.LogInformation("Summarisation worker {Worker} stopped", workerIndex);
    }

    /// <summary>
    /// Takes the oldest due summarisation job, moving it straight to Summarizing in the same
    /// statement that selects it — there is no Downloading/Preparing step for this kind of job.
    /// </summary>
    private async Task<int?> ClaimNextJobAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;

        var candidate = await db.Jobs
            .Where(j => j.Kind == JobKind.Summarization
                     && j.State == JobState.Queued
                     && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(ct);

        if (candidate == 0)
        {
            return null;
        }

        var claimed = await db.Jobs
            .Where(j => j.Id == candidate && j.Kind == JobKind.Summarization && j.State == JobState.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.State, JobState.Summarizing)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);

        return claimed == 1 ? candidate : null;
    }

    private async Task RunJobAsync(int jobId, CancellationToken stoppingToken)
    {
        // A real, cancellable token — same as transcription — so the Cancel button on the Jobs
        // page actually stops a summarisation in flight instead of only marking the row.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var _ = running.Track(jobId, cts);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var summaries = scope.ServiceProvider.GetRequiredService<SummaryService>();

        try
        {
            notifier.Notify(jobId);

            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cts.Token);
            if (job?.TranscriptId is not { } transcriptId)
            {
                await FinishAsync(jobId, JobState.Failed, "This job has no transcript to summarise.");
                return;
            }

            // Report runs synchronously and sequentially within the summariser's own await
            // chain — never concurrently — but it must not touch the same DbContext instance
            // this method is using: a bare `db.SaveChanges()` would also flush whatever else is
            // tracked against it. A dedicated scope and an ExecuteUpdate touching only the
            // progress columns removes that question rather than relying on timing.
            var progress = new SyncProgress<SummaryProgress>(p =>
            {
                var value = p.TotalPasses <= 0 ? 0 : Math.Clamp((double)p.Pass / p.TotalPasses, 0, 1);

                using var progressScope = scopeFactory.CreateScope();
                var progressDb = progressScope.ServiceProvider.GetRequiredService<AppDbContext>();
                progressDb.Jobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdate(setters => setters
                        .SetProperty(j => j.Progress, value)
                        .SetProperty(j => j.CompletedChunks, p.Pass)
                        .SetProperty(j => j.TotalChunks, p.TotalPasses));

                notifier.Notify(jobId);
            });

            var result = await summaries.SummarizeAsync(transcriptId, progress, cts.Token);

            if (!result.Success)
            {
                throw new Exception(result.Error ?? "Summarisation failed.");
            }

            job.State = JobState.Completed;
            job.Progress = 1;
            job.LastError = null;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            notifier.Notify(jobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("Job {JobId} interrupted by shutdown; it will restart on restart", jobId);
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

    /// <summary>Re-queues with exponential backoff until the attempt cap, mirroring the transcription worker.</summary>
    private async Task HandleFailureAsync(int jobId, Exception exception)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
        if (job is null)
        {
            return;
        }

        job.LastError = exception.Message;

        if (job.Attempts < _options.MaxAttempts)
        {
            var delay = TimeSpan.FromSeconds(_options.RetryBaseSeconds * Math.Pow(2, Math.Max(0, job.Attempts - 1)));
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

/// <summary>
/// An <see cref="IProgress{T}"/> that calls straight through on the calling thread, unlike
/// <see cref="Progress{T}"/> which posts to a captured <see cref="SynchronizationContext"/>. A
/// background worker has none, so <see cref="Progress{T}"/> would just run inline anyway — this
/// makes that guarantee explicit rather than incidental.
/// </summary>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
