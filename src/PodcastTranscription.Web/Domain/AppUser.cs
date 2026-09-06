namespace PodcastTranscription.Web.Domain;

/// <summary>
/// A sign-in beyond the one in configuration.
///
/// The configured admin (<c>Auth:Username</c>) deliberately stays in configuration and is not
/// represented here: an account that lives only in the database is an account that disappears
/// when the database is restored from an old backup, and being locked out of your own library by
/// a restore is a worse failure than any this table solves. So these accounts are additional,
/// and the configured one is always there behind them.
/// </summary>
public class AppUser
{
    public int Id { get; set; }

    /// <summary>
    /// Matched case-insensitively at sign-in and unique in that form, so "Brian" and "brian"
    /// cannot both exist and then race to own the name.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Shown in the UI when set; the username stands in when it is not.</summary>
    public string? DisplayName { get; set; }

    /// <summary>PBKDF2, as produced by <see cref="Services.Security.PasswordHasher"/>. Never plaintext.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>
    /// Deactivating keeps the account and its history while refusing the sign-in, which is what
    /// you want for someone who has left: deleting them would only orphan the attribution.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null until the account is first used. The quickest way to spot one nobody wanted.</summary>
    public DateTimeOffset? LastSignInAt { get; set; }
}
