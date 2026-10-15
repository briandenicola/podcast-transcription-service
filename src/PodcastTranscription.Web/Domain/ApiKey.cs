namespace PodcastTranscription.Web.Domain;

public class ApiKey
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    // The configured admin has no database account. Keep its name so changing the configured
    // username does not transfer old keys to a different administrator.
    public string? ConfiguredUsername { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public bool CanSubmit { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
