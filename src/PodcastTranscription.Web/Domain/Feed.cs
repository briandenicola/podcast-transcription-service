namespace PodcastTranscription.Web.Domain;

public class Feed
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RssUrl { get; set; } = string.Empty;
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>Why the last poll failed, or null when it succeeded. Shown on the feeds page.</summary>
    public string? LastError { get; set; }

    /// <summary>Queue new episodes as they appear, rather than only recording them.</summary>
    public bool AutoTranscribe { get; set; } = true;

    /// <summary>
    /// Send a Pushover notification for this feed's episodes once they are transcribed and once
    /// they are summarised. Off by default: notifications need a Pushover app token and user key
    /// configured in <see cref="PushoverSettings"/> first, so a feed opting in before that is set
    /// up would just fail quietly.
    /// </summary>
    public bool NotifyPushover { get; set; }

    // Per-show defaults, inherited by episodes (M4.5).
    public string? DefaultModel { get; set; }
    public string? DefaultLanguage { get; set; }
    public string? DefaultPrompt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Episode> Episodes { get; set; } = [];
}
