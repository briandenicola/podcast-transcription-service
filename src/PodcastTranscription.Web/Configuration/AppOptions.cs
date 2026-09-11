namespace PodcastTranscription.Web.Configuration;

/// <summary>
/// App-wide settings that do not belong to any one feature. Currently just the public URL,
/// which only exists so a Pushover notification can link back to the episode it is about — the
/// app has no other reason to know its own address, since every request already carries it.
/// </summary>
public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// The address this app is reachable at from wherever Pushover notifications are read, e.g.
    /// <c>https://podcasts.example.com</c>. Left unset, notifications carry no link — a relative
    /// path is useless in a push notification, and guessing a host from the request that
    /// triggered the job (usually a background worker, not a request at all) is not possible.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
