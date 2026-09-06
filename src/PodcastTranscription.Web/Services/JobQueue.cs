using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// The write side of the job queue: what the UI calls to add, cancel or retry work. The table
/// is the queue — an in-memory channel would lose everything on a container restart.
/// </summary>
public class JobQueue(
    AppDbContext db,
    WhisperClient whisper,
    RunningJobs running,
    JobNotifier notifier,
    IOptions<TranscriptionOptions> options,
    ILogger<JobQueue> log)
{
    private readonly TranscriptionOptions _options = options.Value;

    /// <summary>
    /// Queues an episode for transcription. Settings not given explicitly are inherited from the
    /// episode's feed — podcasts are consistent, so a show is configured once and its episodes
    /// follow — and fall back to the global configuration after that.
    /// </summary>
    public async Task<Job> EnqueueAsync(
        int episodeId, string? language = null, string? prompt = null, CancellationToken ct = default)
    {
        var feed = await db.Episodes
            .Where(e => e.Id == episodeId && e.FeedId != null)
            .Select(e => e.Feed)
            .FirstOrDefaultAsync(ct);

        var job = new Job
        {
            EpisodeId = episodeId,
            State = JobState.Queued,
            Model = feed?.DefaultModel ?? whisper.Model,
            Language = language ?? feed?.DefaultLanguage,
            Prompt = prompt ?? feed?.DefaultPrompt
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Queued job {JobId} for episode {EpisodeId}", job.Id, episodeId);
        notifier.Notify(job.Id);
        return job;
    }

    /// <summary>
    /// Cancels a job whether it is running or merely queued. A running job is signalled and
    /// marks itself cancelled when it unwinds; a queued one is marked here and never claimed.
    /// </summary>
    public async Task<bool> CancelAsync(int jobId, CancellationToken ct = default)
    {
        if (running.Cancel(jobId))
        {
            log.LogInformation("Signalled running job {JobId} to cancel", jobId);
            return true;
        }

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
        {
            return false;
        }

        job.State = JobState.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        notifier.Notify(jobId);
        return true;
    }

    /// <summary>
    /// Puts a failed or cancelled job back on the queue. Attempts are reset so an operator retry
    /// is not immediately capped by the automatic ones that already ran, and the chunk plan is
    /// kept so the work resumes where it stopped.
    /// </summary>
    public async Task<bool> RetryAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.State is not (JobState.Failed or JobState.Cancelled))
        {
            return false;
        }

        job.State = JobState.Queued;
        job.Attempts = 0;
        job.NextAttemptAt = null;
        job.LastError = null;
        job.CompletedAt = null;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Re-queued job {JobId} from chunk {Chunk}", jobId, job.CompletedChunks);
        notifier.Notify(jobId);
        return true;
    }

    /// <summary>
    /// Resets jobs left mid-flight by a crash or a container restart. Nothing can still be
    /// running at startup, so anything in a working state is orphaned by definition. Their
    /// chunk plans and completed chunks survive, so they resume rather than start over.
    /// </summary>
    public async Task<int> RecoverOrphanedJobsAsync(CancellationToken ct = default)
    {
        var orphaned = await db.Jobs
            .Where(j => j.State == JobState.Downloading
                     || j.State == JobState.Preparing
                     || j.State == JobState.Transcribing)
            .ToListAsync(ct);

        foreach (var job in orphaned)
        {
            job.State = JobState.Queued;
            job.NextAttemptAt = null;
            job.LastError = "Interrupted by a restart; resumed from the last completed chunk.";
        }

        if (orphaned.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogWarning("Recovered {Count} job(s) orphaned by a restart", orphaned.Count);
        }

        return orphaned.Count;
    }

    /// <summary>Backoff before the next automatic attempt: base, doubling per attempt.</summary>
    public TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromSeconds(_options.RetryBaseSeconds * Math.Pow(2, Math.Max(0, attempts - 1)));
}
