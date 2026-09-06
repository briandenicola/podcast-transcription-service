using System.Security.Claims;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services.Security;

/// <summary>
/// The single admin account, resolved from configuration once at startup.
///
/// Credentials come from config rather than the database deliberately: there is exactly one
/// account, and a database that holds a login is a database that can lock you out of your own
/// library if it is restored from an old backup.
/// </summary>
public class AdminAuthenticator
{
    public const string CookieScheme = "PodcastTranscription.Admin";
    public const string ApiKeyHeader = "X-API-Key";

    private readonly AuthOptions _options;
    private readonly string? _passwordHash;

    public AdminAuthenticator(IOptions<AuthOptions> options, ILogger<AdminAuthenticator> log)
    {
        _options = options.Value;

        if (!string.IsNullOrWhiteSpace(_options.PasswordHash))
        {
            _passwordHash = _options.PasswordHash.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(_options.Password))
        {
            // Hashed here so the plaintext is never compared directly, though it was still
            // readable in the environment on the way in.
            _passwordHash = PasswordHasher.Hash(_options.Password);

            log.LogWarning(
                "Auth:Password is set in plaintext. Generate a hash on the settings page and set "
                + "Auth:PasswordHash instead, so the password is not readable in the environment.");
        }
    }

    public bool Enabled => _options.Enabled;

    public string Username => _options.Username;

    /// <summary>True when a password is configured in either form.</summary>
    public bool HasPassword => _passwordHash is not null;

    /// <summary>False when no API key is configured, which disables the submission endpoint.</summary>
    public bool ApiEnabled => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public TimeSpan SessionLength => TimeSpan.FromDays(Math.Clamp(_options.SessionDays, 1, 365));

    public bool ValidateCredentials(string? username, string? password)
    {
        if (password is null || _passwordHash is null)
        {
            return false;
        }

        // Compare the username in constant time too: it is not a secret, but leaking whether the
        // name was right narrows a guess for no reason.
        var usernameMatches = PasswordHasher.SecretEquals(username, _options.Username);
        var passwordMatches = PasswordHasher.Verify(password, _passwordHash);

        return usernameMatches && passwordMatches;
    }

    public bool ValidateApiKey(string? presented) =>
        ApiEnabled && PasswordHasher.SecretEquals(presented, _options.ApiKey);

    public ClaimsPrincipal CreatePrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, _options.Username), new Claim(ClaimTypes.Role, "Admin")],
            CookieScheme));
}
