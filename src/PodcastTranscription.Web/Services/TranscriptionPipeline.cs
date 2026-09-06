using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Chunking;
using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Runs one job to completion: prepare, plan, then post chunks one at a time, persisting
/// segments as each lands.
///
/// whisper-server returns nothing until a whole request is done, so a 90-minute episode posted
/// whole is one blocking call with no progress and nothing to show for it if the container
/// stops. Chunking turns that into a sequence of ~10-minute calls, which is what makes progress
/// reporting, partial transcripts and resume possible at all.
/// </summary>
public class TranscriptionPipeline(
    AppDbContext db,
    MediaStore media,
    AudioProcessor audio,
    WhisperClient whisper,
    YtDlpClient ytDlp,
    IOptions<TranscriptionOptions> options,
    JobNotifier notifier,
    ILogger<TranscriptionPipeline> log)
{
    private readonly TranscriptionOptions _options = options.Value;

    public async Task RunAsync(int jobId, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == job.EpisodeId, ct)
            ?? throw new InvalidOperationException($"Episode {job.EpisodeId} not found.");

        var wallClock = Stopwatch.StartNew();
        var audioSecondsThisRun = 0.0;

        await EnsureSourceAudioAsync(job, episode, ct);

        await SetStateAsync(job, JobState.Preparing, ct);
        var wavRelative = await EnsurePreparedWavAsync(episode, ct);
        var wavPath = media.Resolve(wavRelative);

        var chunks = await EnsureChunkPlanAsync(job, wavPath, ct);
        var transcript = await EnsureTranscriptAsync(job, episode, ct);

        await SetStateAsync(job, JobState.Transcribing, ct);

        var ordinal = await db.Segments
            .Where(s => s.TranscriptId == transcript.Id)
            .Select(s => (int?)s.Ordinal)
            .MaxAsync(ct) is { } max ? max + 1 : 0;

        // CompletedChunks is the resume point: a job that died halfway picks up at the next
        // chunk rather than re-transcribing the whole episode.
        if (job.CompletedChunks > 0)
        {
            log.LogInformation("Resuming job {JobId} at chunk {Index} of {Total}",
                job.Id, job.CompletedChunks, chunks.Count);
        }

        for (var i = job.CompletedChunks; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = chunks[i];
            var chunkPath = media.Resolve(Path.Combine("chunks", $"job-{job.Id}", $"chunk-{chunk.Index:D4}.wav"));

            try
            {
                await audio.ExtractChunkAsync(wavPath, chunkPath, chunk.StartMs, chunk.EndMs, ct);

                var request = new WhisperRequest
                {
                    Language = job.Language,
                    Prompt = job.Prompt,
                    MaxLen = _options.WordTimestamps ? 1 : null,
                    SplitOnWord = _options.WordTimestamps
                };

                var (response, rawJson) = await whisper.TranscribeAsync(chunkPath, request, ct);

                var segments = BuildSegments(response, chunk.StartMs, ref ordinal);

                transcript.Language ??= response.Language;
                db.Segments.AddRange(segments.Select(s =>
                {
                    s.TranscriptId = transcript.Id;
                    return s;
                }));

                db.TranscriptChunks.Add(new TranscriptChunk
                {
                    TranscriptId = transcript.Id,
                    Index = chunk.Index,
                    StartMs = chunk.StartMs,
                    EndMs = chunk.EndMs,
                    RawJson = rawJson
                });

                audioSecondsThisRun += chunk.DurationMs / 1000.0;

                job.CompletedChunks = i + 1;
                job.Progress = (double)job.CompletedChunks / chunks.Count;
                job.ProcessedAudioSec += chunk.DurationMs / 1000.0;

                // Deliberately not cancellable. The inference for this chunk is already done and
                // paid for; dropping it because a cancel arrived while the response was in flight
                // would mean re-transcribing it on resume. Cancellation is honoured at the top of
                // the next iteration instead.
                await db.SaveChangesAsync(CancellationToken.None);
                notifier.Notify(job.Id);

                log.LogInformation("Job {JobId} chunk {Index}/{Total} done — {Segments} segments",
                    job.Id, chunk.Index + 1, chunks.Count, segments.Count);
            }
            finally
            {
                // Chunks are cheap to recut and would otherwise pile up beside the library.
                TryDelete(chunkPath);
            }
        }

        transcript.IsComplete = true;
        transcript.CompletedAt = DateTimeOffset.UtcNow;
        transcript.RealtimeFactor = wallClock.Elapsed.TotalSeconds > 0 && audioSecondsThisRun > 0
            ? audioSecondsThisRun / wallClock.Elapsed.TotalSeconds
            : null;

        job.State = JobState.Completed;
        job.Progress = 1.0;
        job.Language = transcript.Language;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LastError = null;

        await db.SaveChangesAsync(ct);
        notifier.Notify(job.Id);

        TryDeleteDirectory(media.Resolve(Path.Combine("chunks", $"job-{job.Id}")));

        log.LogInformation(
            "Job {JobId} completed in {Elapsed} — {Chunks} chunks, realtime factor {Factor:N2}x",
            job.Id, wallClock.Elapsed, chunks.Count, transcript.RealtimeFactor ?? 0);
    }

    /// <summary>
    /// Fetches the audio when the episode only has a URL — how feed items and URL ingest arrive.
    /// Downloading inside the job rather than at ingest time means it queues, retries and reports
    /// progress like everything else, instead of blocking whoever pasted the link.
    /// </summary>
    private async Task EnsureSourceAudioAsync(Job job, Episode episode, CancellationToken ct)
    {
        if (media.Exists(episode.AudioPath))
        {
            return;
        }

        // The prepared WAV is all transcription actually needs. Retention may prune source audio
        // while keeping it, and re-transcribing then should not pull the episode down again.
        if (media.Exists(episode.PreparedAudioPath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(episode.SourceUrl))
        {
            throw new TerminalJobException(
                $"Episode {episode.Id} has neither audio on disk nor a source URL to fetch it from.");
        }

        await SetStateAsync(job, JobState.Downloading, ct);

        var downloadDirectory = media.Resolve(Path.Combine("source", $"episode-{episode.Id}"));
        var download = await ytDlp.DownloadAudioAsync(episode.SourceUrl, downloadDirectory, ct);

        var relative = Path.GetRelativePath(media.Root, download.FilePath);
        var sha = await AudioProcessor.ComputeSha256Async(download.FilePath, ct);

        // The same audio can arrive twice: a feed that reissues an item with a new guid, or a URL
        // pasted after the file was already uploaded. Cheaper to notice now than to transcribe it
        // a second time.
        var duplicate = await db.Episodes
            .Where(e => e.Id != episode.Id && e.AudioSha256 == sha)
            .Select(e => new { e.Id, e.Title })
            .FirstOrDefaultAsync(ct);

        if (duplicate is not null)
        {
            // The bytes are identical to a copy already in the library, so this one is redundant.
            TryDelete(download.FilePath);
            throw new TerminalJobException(
                $"Byte-identical to episode {duplicate.Id} ('{duplicate.Title}'), which is already in the library.");
        }

        episode.AudioPath = relative;
        episode.AudioSha256 = sha;
        episode.DurationSec ??= download.DurationSec;
        episode.PublishedAt ??= download.PublishedAt;

        // Feeds title an item better than yt-dlp does, so only replace the ingest placeholder.
        if ((string.IsNullOrWhiteSpace(episode.Title) || episode.Title == Episode.PendingTitle)
            && !string.IsNullOrWhiteSpace(download.Title))
        {
            episode.Title = download.Title!;
        }

        await db.SaveChangesAsync(ct);

        log.LogInformation("Downloaded episode {EpisodeId} from {Url}", episode.Id, episode.SourceUrl);
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

        // ffprobe on the file we actually have beats whatever the feed or yt-dlp claimed; those
        // are frequently rounded, and occasionally just wrong.
        if (await audio.ProbeDurationAsync(media.Resolve(relative), ct) is { } probed and > 0)
        {
            episode.DurationSec = probed;
        }

        await db.SaveChangesAsync(ct);
        return relative;
    }

    /// <summary>
    /// Reuses the stored plan when there is one, so a resumed job cuts in exactly the same
    /// places — different boundaries would duplicate or drop audio across the seam.
    /// </summary>
    private async Task<List<AudioChunk>> EnsureChunkPlanAsync(Job job, string wavPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(job.ChunkPlanJson))
        {
            var stored = JsonSerializer.Deserialize<List<AudioChunk>>(job.ChunkPlanJson);
            if (stored is { Count: > 0 })
            {
                return stored;
            }
        }

        var durationSec = await audio.ProbeDurationAsync(wavPath, ct)
            ?? throw new AudioProcessingException($"Could not determine the duration of '{Path.GetFileName(wavPath)}'.");

        var silences = await audio.DetectSilenceAsync(
            wavPath, _options.SilenceNoiseDb, _options.SilenceMinDurationSeconds, ct);

        var chunks = ChunkPlanner.Plan((int)Math.Round(durationSec * 1000), silences, _options);
        if (chunks.Count == 0)
        {
            throw new AudioProcessingException($"'{Path.GetFileName(wavPath)}' contains no audio to transcribe.");
        }

        job.ChunkPlanJson = JsonSerializer.Serialize(chunks);
        job.TotalChunks = chunks.Count;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Job {JobId} planned as {Count} chunks over {Duration:N0}s of audio",
            job.Id, chunks.Count, durationSec);

        return chunks;
    }

    private async Task<Transcript> EnsureTranscriptAsync(Job job, Episode episode, CancellationToken ct)
    {
        if (job.TranscriptId is { } id)
        {
            var existing = await db.Transcripts.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var transcript = new Transcript
        {
            EpisodeId = episode.Id,
            Model = job.Model,
            Language = null
        };

        db.Transcripts.Add(transcript);
        await db.SaveChangesAsync(ct);

        job.TranscriptId = transcript.Id;
        await db.SaveChangesAsync(ct);

        return transcript;
    }

    /// <summary>
    /// Turns a chunk's response into display segments, offsetting every timestamp by the chunk's
    /// position in the episode.
    /// </summary>
    private List<Segment> BuildSegments(WhisperResponse response, int offsetMs, ref int ordinal)
    {
        var segments = new List<Segment>();

        if (_options.WordTimestamps)
        {
            // With max_len=1 and split_on_word each "segment" is a single word, so the natural
            // sentence grouping has to be rebuilt from the word stream.
            var words = response.Segments
                .Select(s => new TranscribedWord
                {
                    StartMs = offsetMs + s.StartMs,
                    EndMs = offsetMs + s.EndMs,
                    Text = s.Text.Trim(),
                    AvgLogProb = s.AvgLogProb,
                    Probability = s.AvgLogProb is { } logProb ? Math.Clamp(Math.Exp(logProb), 0, 1) : null
                })
                .Where(w => w.Text.Length > 0)
                .ToList();

            foreach (var grouped in WordGrouper.Group(words, _options.WordGapMs, _options.MaxSegmentChars))
            {
                segments.Add(new Segment
                {
                    Ordinal = ordinal++,
                    StartMs = grouped.StartMs,
                    EndMs = grouped.EndMs,
                    Text = grouped.Text,
                    AvgLogProb = grouped.AvgLogProb,
                    WordsJson = JsonSerializer.Serialize(grouped.Words)
                });
            }

            return segments;
        }

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
                StartMs = offsetMs + s.StartMs,
                EndMs = offsetMs + s.EndMs,
                Text = text,
                AvgLogProb = s.AvgLogProb
            });
        }

        return segments;
    }

    private async Task SetStateAsync(Job job, JobState state, CancellationToken ct)
    {
        job.State = state;
        await db.SaveChangesAsync(ct);
        notifier.Notify(job.Id);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            log.LogDebug(ex, "Could not delete chunk {Path}", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            log.LogDebug(ex, "Could not delete chunk directory {Path}", path);
        }
    }
}
