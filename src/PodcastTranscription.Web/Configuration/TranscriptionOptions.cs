namespace PodcastTranscription.Web.Configuration;

/// <summary>How the worker chunks episodes, retries failures, and groups words.</summary>
public class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    /// <summary>
    /// One by default. A second concurrent request to a single whisper-server just queues
    /// inside it and muddies the timing data. Raise this only when there are several backends.
    /// </summary>
    public int WorkerCount { get; set; } = 1;

    /// <summary>Target chunk length. Tune against the realtime factor the jobs page reports.</summary>
    public int ChunkSeconds { get; set; } = 600;

    /// <summary>
    /// How far from the target boundary to look for a silence to cut on. Beyond this the cut
    /// is made at the target regardless, mid-word if it comes to that.
    /// </summary>
    public int SilenceSearchWindowSeconds { get; set; } = 90;

    /// <summary>Anything quieter than this counts as silence, in dBFS.</summary>
    public int SilenceNoiseDb { get; set; } = -30;

    /// <summary>How long a quiet stretch must last to be a candidate cut point.</summary>
    public double SilenceMinDurationSeconds { get; set; } = 0.5;

    /// <summary>
    /// A trailing chunk shorter than this fraction of <see cref="ChunkSeconds"/> is absorbed
    /// into the one before it, rather than left as a stub.
    /// </summary>
    public double MinTailFraction { get; set; } = 0.25;

    /// <summary>
    /// Ask whisper for one word per segment. Word timing is the one thing that is genuinely
    /// painful to retrofit, so it is on by default.
    /// </summary>
    public bool WordTimestamps { get; set; } = true;

    /// <summary>A pause longer than this starts a new display segment.</summary>
    public int WordGapMs { get; set; } = 700;

    /// <summary>A display segment is closed once it reaches roughly this many characters.</summary>
    public int MaxSegmentChars { get; set; } = 200;

    /// <summary>Give up after this many attempts and leave the job Failed.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>First retry delay; doubles each attempt.</summary>
    public int RetryBaseSeconds { get; set; } = 30;

    /// <summary>How long a worker sleeps when it finds no work.</summary>
    public int PollSeconds { get; set; } = 5;
}
