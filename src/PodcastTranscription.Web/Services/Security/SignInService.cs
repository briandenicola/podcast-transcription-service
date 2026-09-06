using System.Security.Claims;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Security;

/// <summary>
/// Turns a username and password into a principal, from either of the two places an account can
/// live: the one in configuration, and the ones in the database.
///
/// The configured account is checked first and always wins its own name. It is the break-glass
/// account — it works when the database has been restored from a backup that predates every other
/// account, or has no accounts at all — so nothing stored in the database may shadow it.
/// </summary>
public class SignInService(AdminAuthenticator admin, UserStore users, ILogger<SignInService> log)
{
    public async Task<SignInOutcome> AuthenticateAsync(
        string? username, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return SignInOutcome.Rejected();
        }

        if (admin.ValidateCredentials(username, password))
        {
            return SignInOutcome.Accepted(admin.CreatePrincipal(), admin.Username, UserRole.Admin, null);
        }

        var user = await users.FindByNameAsync(username, ct);

        // Hash against a decoy when there is no such account, so a wrong name costs the same
        // PBKDF2 work as a wrong password. Verify() returns immediately on a null hash, so
        // without this the response time would enumerate which accounts exist.
        var passwordMatches = PasswordHasher.Verify(password, user?.PasswordHash ?? DecoyHash.Value);

        if (user is null || !passwordMatches)
        {
            return SignInOutcome.Rejected();
        }

        if (!user.IsActive)
        {
            log.LogWarning("Deactivated account {Username} tried to sign in", user.Username);
            return SignInOutcome.Rejected("That account has been deactivated.");
        }

        return SignInOutcome.Accepted(CreatePrincipal(user), user.Username, user.Role, user.Id);
    }

    public static ClaimsPrincipal CreatePrincipal(AppUser user) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, Roles.Name(user.Role)),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(DisplayNameClaim, user.DisplayName ?? user.Username)
            ],
            AdminAuthenticator.CookieScheme));

    public const string DisplayNameClaim = "display_name";

    /// <summary>
    /// A real hash of a value nobody knows, built once. Verifying against it costs the same as
    /// verifying a real account, which is the whole point.
    /// </summary>
    private static readonly Lazy<string> DecoyHash =
        new(() => PasswordHasher.Hash(Guid.NewGuid().ToString("N")));
}

/// <summary>
/// The result of an attempt. <see cref="Reason"/> is only set where saying more helps the person
/// rather than someone guessing — a deactivated account, say, which they cannot fix by trying
/// a different password.
/// </summary>
public record SignInOutcome(
    bool Succeeded, ClaimsPrincipal? Principal, string? Username, UserRole Role, int? UserId, string? Reason)
{
    public static SignInOutcome Accepted(ClaimsPrincipal principal, string username, UserRole role, int? userId) =>
        new(true, principal, username, role, userId, null);

    public static SignInOutcome Rejected(string? reason = null) =>
        new(false, null, null, UserRole.Viewer, null, reason);
}
