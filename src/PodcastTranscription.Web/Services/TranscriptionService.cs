using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// End-to-end transcription of a single episode, run inline by the caller.
///
/// M1 posts the whole prepared WAV in one request: no chunking, no progress until it returns.
/// M2 replaces the single POST here with silence-aware chunking driven by a background worker;
/// the surrounding job bookkeeping is meant to survive that change unaltered.
/// </summary>
public class TranscriptionService(
    AppDbContext db,
    MediaStore media,
    AudioProcessor audio,
    WhisperClient whisper,
    ILogger<TranscriptionService> log)
{
    public async Task<Transcript> TranscribeAsync(
        int episodeId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId, ct)
            ?? throw new InvalidOperationException($"Episode {episodeId} not found.");

        var job = new Job
        {
            EpisodeId = episode.Id,
            State = JobState.Preparing,
            Model = whisper.Model,
            Language = null,
            Attempts = 1,
            StartedAt = DateTimeOffset.UtcNow
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        try
        {
            progress?.Report("Preparing audio…");
            var wavRelative = await EnsurePreparedWavAsync(episode, ct);

            job.State = JobState.Transcribing;
            await db.SaveChangesAsync(ct);

            progress?.Report("Transcribing — whisper-server returns nothing until the whole file is done…");
            var wall = Stopwatch.StartNew();
            var (response, rawJson) = await whisper.TranscribeAsync(media.Resolve(wavRelative), ct: ct);
            wall.Stop();

            var audioSeconds = response.Duration ?? episode.DurationSec ?? 0;
            var realtimeFactor = wall.Elapsed.TotalSeconds > 0 && audioSeconds > 0
                ? audioSeconds / wall.Elapsed.TotalSeconds
                : (double?)null;

            var transcript = new Transcript
            {
                EpisodeId = episode.Id,
                Model = whisper.Model,
                Language = response.Language,
                RawJson = rawJson,
                RealtimeFactor = realtimeFactor,
                Segments = BuildSegments(response)
            };

            db.Transcripts.Add(transcript);

            if (episode.DurationSec is null && response.Duration is > 0)
            {
                episode.DurationSec = response.Duration;
            }

            job.State = JobState.Completed;
            job.Progress = 1.0;
            job.Language = response.Language;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Episode {EpisodeId} transcribed in {Elapsed} — {Segments} segments, realtime factor {Factor:N2}x",
                episode.Id, wall.Elapsed, transcript.Segments.Count, realtimeFactor ?? 0);

            progress?.Report($"Done — {transcript.Segments.Count} segments in {wall.Elapsed:mm\\:ss}.");
            return transcript;
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.LastError = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            log.LogError(ex, "Transcription failed for episode {EpisodeId}", episode.Id);
            progress?.Report($"Failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Produces the 16 kHz mono WAV if it is missing. The prepared file is kept after success:
    /// re-transcription and, later, diarization both read it rather than decoding again.
    /// </summary>
    private async Task<string> EnsurePreparedWavAsync(Episode episode, CancellationToken ct)
    {
        if (media.Exists(episode.PreparedAudioPath))
        {
            return episode.PreparedAudioPath!;
        }

        if (!media.Exists(episode.AudioPath))
        {
            throw new FileNotFoundException($"Source audio for episode {episode.Id} is missing.", episode.AudioPath);
        }

        var relative = media.PreparedPath(episode.Id);
        await audio.PrepareWavAsync(media.Resolve(episode.AudioPath), media.Resolve(relative), ct);

        episode.PreparedAudioPath = relative;
        await db.SaveChangesAsync(ct);
        return relative;
    }

    private static List<Segment> BuildSegments(WhisperResponse response)
    {
        var segments = new List<Segment>(response.Segments.Count);
        var ordinal = 0;

        foreach (var s in response.Segments)
        {
            var text = s.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            segments.Add(new Segment
            {
                Ordinal = ordinal++,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Text = text,
                AvgLogProb = s.AvgLogProb
            });
        }

        return segments;
    }
}
