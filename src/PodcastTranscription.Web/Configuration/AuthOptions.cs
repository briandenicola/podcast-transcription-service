namespace PodcastTranscription.Web.Configuration;

/// <summary>
/// Single-admin cookie login, plus the API key for programmatic submission.
///
/// whisper-server has no authentication of its own and this app holds a media library, so auth
/// is on by default. "It's only on the LAN" ages badly.
/// </summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Turn off only when something in front of the app is already authenticating — a reverse
    /// proxy with its own login, or a private network you genuinely trust.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Username { get; set; } = "admin";

    /// <summary>
    /// Preferred. A PBKDF2 string as produced by the generator on the settings page, so the
    /// plaintext password never sits in a config file or an environment variable.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Plaintext fallback for a quick start. Hashed on load and never stored, but it is still
    /// visible to anything that can read the environment; the app logs a warning saying so.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Key for <c>POST /api/episodes</c>, sent as <c>X-API-Key</c>. The submission endpoint is
    /// disabled entirely when this is unset — an API with no key is not an API worth exposing.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>How long a login lasts.</summary>
    public int SessionDays { get; set; } = 30;
}
