namespace PodcastTranscription.Web.Domain;

public class Feed
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RssUrl { get; set; } = string.Empty;
    public DateTimeOffset? LastPolledAt { get; set; }
    public bool AutoTranscribe { get; set; }

    // Per-show defaults, inherited by episodes (M4.5).
    public string? DefaultModel { get; set; }
    public string? DefaultLanguage { get; set; }
    public string? DefaultPrompt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Episode> Episodes { get; set; } = [];
}
