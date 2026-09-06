namespace PodcastTranscription.Web.Domain;

/// <summary>
/// What a local model made of one transcript: what the episode argues, who argued it, and where.
///
/// One per transcript rather than one per episode. A transcript is the thing that was actually
/// read, so re-transcribing on a better model gets its own summary and the old one stays
/// attached to the old text — the same reason transcripts are one-to-many on the episode.
/// Re-summarising the same transcript replaces this row: unlike a transcript, it costs minutes
/// rather than hours to make again, and two summaries of identical text are not worth keeping.
/// </summary>
public class Summary
{
    public int Id { get; set; }

    public int TranscriptId { get; set; }
    public Transcript? Transcript { get; set; }

    /// <summary>The Ollama model tag that wrote it. Two runs are only comparable if you know this.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Markdown, as the model produced it. Rendered by <c>SummaryMarkdown</c> at display time.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// How many windows the transcript was cut into for the first pass. 1 means it fitted the
    /// context window whole; more means the summary is of notes, and the reduce step is where any
    /// lost detail went.
    /// </summary>
    public int WindowCount { get; set; }

    /// <summary>Tokens as Ollama counted them, summed across every pass. Null when it did not say.</summary>
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    /// <summary>Wall clock for the whole run, which is what tells you whether the model is too big for the box.</summary>
    public long DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
