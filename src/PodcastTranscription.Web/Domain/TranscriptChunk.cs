namespace PodcastTranscription.Web.Domain;

/// <summary>
/// One posted chunk and the response it produced, kept verbatim. Chunking means there is no
/// single whisper response for an episode, so the raw bodies live here rather than on
/// <see cref="Transcript"/> — segments can still be re-derived, export formats changed, or
/// accuracy debugged without re-running inference, and a crashed job does not lose the raw
/// output of the chunks that already succeeded.
/// </summary>
public class TranscriptChunk
{
    public int Id { get; set; }

    public int TranscriptId { get; set; }
    public Transcript? Transcript { get; set; }

    public int Index { get; set; }

    /// <summary>Offset of this chunk within the episode. Every timestamp in RawJson is relative to it.</summary>
    public int StartMs { get; set; }
    public int EndMs { get; set; }

    public string? RawJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
