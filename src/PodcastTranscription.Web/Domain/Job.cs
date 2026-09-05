namespace PodcastTranscription.Web.Domain;

public class Job
{
    public int Id { get; set; }

    public int EpisodeId { get; set; }
    public Episode? Episode { get; set; }

    public JobState State { get; set; } = JobState.Queued;
    public string Model { get; set; } = string.Empty;
    public string? Language { get; set; }

    /// <summary>0.0 to 1.0, updated as segments complete.</summary>
    public double Progress { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
