namespace PodcastTranscription.Web.Domain;

public class Transcript
{
    public int Id { get; set; }

    public int EpisodeId { get; set; }
    public Episode? Episode { get; set; }

    public string Model { get; set; } = string.Empty;
    public string? Language { get; set; }

    /// <summary>
    /// The raw whisper responses, one per posted chunk. See <see cref="TranscriptChunk"/> for why
    /// they live there rather than in a single column here.
    /// </summary>
    public List<TranscriptChunk> Chunks { get; set; } = [];

    /// <summary>Audio seconds divided by wall-clock seconds. The number worth having when sizing models or hardware.</summary>
    public double? RealtimeFactor { get; set; }

    /// <summary>
    /// False while chunks are still landing. Segments are persisted as they complete, so a
    /// transcript is readable — and obviously partial — before the job finishes.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Set when the transcript looks degenerate — a repetition loop, most often. Null when it
    /// looks fine. Recorded rather than acted on: the transcript may still be mostly good, and
    /// discarding one on a heuristic would be worse than flagging it.
    /// </summary>
    public string? QualityWarning { get; set; }

    /// <summary>
    /// What a local model made of this transcript, when summarisation is configured and has run.
    /// Null is the normal state for an older transcript, or one on a host with no Ollama.
    /// </summary>
    public Summary? Summary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public List<Segment> Segments { get; set; } = [];
}
