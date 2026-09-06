namespace PodcastTranscription.Web.Domain;

public class Episode
{
    /// <summary>
    /// Stands in until a download reports the real title. URL ingest has nothing better to show
    /// in the library while the job is queued, and the pipeline replaces it once yt-dlp answers.
    /// </summary>
    public const string PendingTitle = "Fetching…";

    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Show { get; set; }
    public string? SourceUrl { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Duration of the source audio in seconds, as probed by ffprobe.</summary>
    public double? DurationSec { get; set; }

    /// <summary>Original audio as uploaded or downloaded, relative to the media root.</summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>16 kHz mono WAV derived from <see cref="AudioPath"/>. Kept for re-transcription and future diarization.</summary>
    public string? PreparedAudioPath { get; set; }

    /// <summary>SHA-256 of the source audio, for "have I already done this one" across renames and re-downloads.</summary>
    public string AudioSha256 { get; set; } = string.Empty;

    public int? FeedId { get; set; }
    public Feed? Feed { get; set; }

    /// <summary>
    /// The feed's own identifier for this item — its &lt;guid&gt;, or the enclosure URL when the
    /// feed omits one. How a poll tells an item it has already seen from a new one, before
    /// anything has been downloaded and there is a hash to compare.
    /// </summary>
    public string? FeedItemGuid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Transcript> Transcripts { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
}
