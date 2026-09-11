namespace PodcastTranscription.Web.Domain;

/// <summary>
/// The single row of Pushover credentials the app sends notifications with.
///
/// Kept in the database rather than in <c>appsettings.json</c>/.env, unlike the rest of this
/// app's configuration: it is a credential an operator sets up interactively from Pushover's own
/// site, not something meant to live in an ops file, and editing it here takes effect immediately
/// with no restart. There is only ever one row (<see cref="Id"/> is always 1); every feed that
/// opts in shares it.
/// </summary>
public class PushoverSettings
{
    public int Id { get; set; } = 1;

    /// <summary>The application/API token issued by Pushover for this app.</summary>
    public string? AppToken { get; set; }

    /// <summary>The user or group key notifications are delivered to.</summary>
    public string? UserKey { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
