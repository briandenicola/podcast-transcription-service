using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

public record DeletionResult(bool Deleted, string? Refusal = null)
{
    public static DeletionResult Refused(string reason) => new(false, reason);
    public static DeletionResult Ok() => new(true);
}

/// <summary>
/// Removing episodes, transcripts and jobs.
///
/// Segments are always deleted with an explicit statement rather than left to a foreign-key
/// cascade. The FTS index is kept in step by a trigger on Segments, and whether SQLite fires
/// triggers for rows removed by a cascade depends on a pragma — so relying on it would risk an
/// index that still returns hits for deleted episodes.
/// </summary>
public class DeletionService(AppDbContext db, MediaStore media, RunningJobs running, JobNotifier notifier, ILogger<DeletionService> log)
{
    /// <summary>
    /// Deletes a job's history row. A job that is still working must be cancelled first —
    /// deleting it underneath the worker would leave the worker writing to rows that are gone.
    /// </summary>
    public async Task<DeletionResult> DeleteJobAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return DeletionResult.Refused("That job no longer exists.");
        }

        if (running.IsRunning(jobId) || JobDisplay.IsActive(job.State))
        {
            return DeletionResult.Refused("Cancel the job before deleting it.");
        }

        db.Jobs.Remove(job);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Deleted job {JobId}", jobId);
        notifier.Notify(jobId);

        return DeletionResult.Ok();
    }

    /// <summary>
    /// Deletes one transcript and everything derived from it. The episode and its audio stay, so
    /// a bad run can be thrown away and the episode transcribed again.
    /// </summary>
    public async Task<DeletionResult> DeleteTranscriptAsync(int transcriptId, CancellationToken ct = default)
    {
        var transcript = await db.Transcripts.FirstOrDefaultAsync(t => t.Id == transcriptId, ct);
        if (transcript is null)
        {
            return DeletionResult.Refused("That transcript no longer exists.");
        }

        // A job still writing into this transcript would recreate rows as fast as they go.
        var writingJob = await db.Jobs
            .Where(j => j.TranscriptId == transcriptId)
            .Select(j => new { j.Id, j.State })
            .FirstOrDefaultAsync(ct);

        if (writingJob is not null && (running.IsRunning(writingJob.Id) || JobDisplay.IsActive(writingJob.State)))
        {
            return DeletionResult.Refused("A job is still writing to that transcript. Cancel it first.");
        }

        await DeleteTranscriptRowsAsync([transcriptId], ct);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Deleted transcript {TranscriptId}", transcriptId);
        return DeletionResult.Ok();
    }

    /// <summary>
    /// Deletes an episode, everything derived from it, and its audio. This is the irreversible
    /// one: the audio is the only record of what was said.
    /// </summary>
    public async Task<DeletionResult> DeleteEpisodeAsync(int episodeId, CancellationToken ct = default)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null)
        {
            return DeletionResult.Refused("That episode no longer exists.");
        }

        var activeJob = await db.Jobs
            .Where(j => j.EpisodeId == episodeId)
            .Select(j => new { j.Id, j.State })
            .ToListAsync(ct);

        if (activeJob.Any(j => running.IsRunning(j.Id) || JobDisplay.IsActive(j.State)))
        {
            return DeletionResult.Refused("A job for this episode is still running. Cancel it first.");
        }

        var transcriptIds = await db.Transcripts
            .Where(t => t.EpisodeId == episodeId)
            .Select(t => t.Id)
            .ToListAsync(ct);

        await DeleteTranscriptRowsAsync(transcriptIds, ct);

        // Remember the feed's guid for this episode so the next poll does not mistake "deleted"
        // for "never seen" and bring it straight back. Only feed-sourced episodes have a guid to
        // remember; manual imports have nothing for a future poll to re-discover anyway.
        if (episode.FeedId is int feedId && !string.IsNullOrEmpty(episode.FeedItemGuid))
        {
            var alreadyTombstoned = await db.DeletedFeedItems
                .AnyAsync(d => d.FeedId == feedId && d.FeedItemGuid == episode.FeedItemGuid, ct);

            if (!alreadyTombstoned)
            {
                db.DeletedFeedItems.Add(new DeletedFeedItem
                {
                    FeedId = feedId,
                    FeedItemGuid = episode.FeedItemGuid
                });
                await db.SaveChangesAsync(ct);
            }
        }

        await db.Jobs.Where(j => j.EpisodeId == episodeId).ExecuteDeleteAsync(ct);
        await db.Episodes.Where(e => e.Id == episodeId).ExecuteDeleteAsync(ct);

        // Files last: a database row with no audio is recoverable, audio with no row is litter.
        DeleteFile(episode.AudioPath);
        DeleteFile(episode.PreparedAudioPath);
        DeleteDirectory(Path.Combine("source", $"episode-{episodeId}"));
        DeleteDirectory(Path.Combine("chunks", $"job-{episodeId}"));

        log.LogInformation("Deleted episode {EpisodeId} '{Title}' and its audio", episodeId, episode.Title);
        return DeletionResult.Ok();
    }

    /// <summary>
    /// Deletes segments explicitly so the FTS triggers fire, then the rows that hang off them.
    /// </summary>
    private async Task DeleteTranscriptRowsAsync(IReadOnlyList<int> transcriptIds, CancellationToken ct)
    {
        if (transcriptIds.Count == 0)
        {
            return;
        }

        await db.Segments.Where(s => transcriptIds.Contains(s.TranscriptId)).ExecuteDeleteAsync(ct);
        await db.TranscriptChunks.Where(c => transcriptIds.Contains(c.TranscriptId)).ExecuteDeleteAsync(ct);
        await db.Summaries.Where(s => transcriptIds.Contains(s.TranscriptId)).ExecuteDeleteAsync(ct);

        // Jobs outlive the transcript they produced, so the reference is cleared rather than
        // taking the job history with it.
        await db.Jobs
            .Where(j => j.TranscriptId != null && transcriptIds.Contains(j.TranscriptId!.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.TranscriptId, (int?)null), ct);

        await db.Transcripts.Where(t => transcriptIds.Contains(t.Id)).ExecuteDeleteAsync(ct);
    }

    private void DeleteFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var path = media.Resolve(relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Could not delete {Path}", relativePath);
        }
    }

    private void DeleteDirectory(string relativePath)
    {
        try
        {
            var path = media.Resolve(relativePath);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Could not delete directory {Path}", relativePath);
        }
    }
}
