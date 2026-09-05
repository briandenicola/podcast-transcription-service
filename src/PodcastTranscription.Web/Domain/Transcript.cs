namespace PodcastTranscription.Web.Domain;

public class Transcript
{
    public int Id { get; set; }

    public int EpisodeId { get; set; }
    public Episode? Episode { get; set; }

    public string Model { get; set; } = string.Empty;
    public string? Language { get; set; }

    /// <summary>The whisper response, unmodified. Lets us re-derive segments or change export formats without re-running inference.</summary>
    public string? RawJson { get; set; }

    /// <summary>Audio seconds divided by wall-clock seconds. The number worth having when sizing models or hardware.</summary>
    public double? RealtimeFactor { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Segment> Segments { get; set; } = [];
}
