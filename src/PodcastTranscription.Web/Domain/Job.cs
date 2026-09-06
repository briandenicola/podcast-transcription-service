namespace PodcastTranscription.Web.Domain;

public class Job
{
    public int Id { get; set; }

    public int EpisodeId { get; set; }
    public Episode? Episode { get; set; }

    public JobState State { get; set; } = JobState.Queued;
    public string Model { get; set; } = string.Empty;
    public string? Language { get; set; }

    /// <summary>
    /// The initial prompt handed to whisper, inherited from the feed unless overridden. Recorded
    /// per job because it changes the output: comparing two runs means knowing what biased each.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>0.0 to 1.0, updated as segments complete.</summary>
    public double Progress { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set when a failure is going to be retried; the worker ignores the job until then.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The transcript being built. Set once work starts, so a resumed job appends to it.</summary>
    public int? TranscriptId { get; set; }
    public Transcript? Transcript { get; set; }

    /// <summary>
    /// The chunk boundaries, as JSON. Persisted rather than recomputed so a resumed job cuts the
    /// audio in exactly the places the first attempt did.
    /// </summary>
    public string? ChunkPlanJson { get; set; }

    public int TotalChunks { get; set; }

    /// <summary>How many chunks have landed. Doubles as the resume point.</summary>
    public int CompletedChunks { get; set; }

    /// <summary>Audio seconds transcribed so far, for a realtime factor while the job is still running.</summary>
    public double ProcessedAudioSec { get; set; }

    /// <summary>
    /// Who asked for this, as a username rather than a foreign key. Attribution is a record of
    /// what happened, so it must not change when an account is renamed or removed — and the
    /// configured admin has no row to point at in the first place. Null for work the app started
    /// by itself: a feed poll, or a submission through the API.
    /// </summary>
    public string? QueuedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
