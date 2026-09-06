using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Security;

/// <summary>
/// Reading and writing the accounts that live in the database. The configured admin is not one of
/// these — see <see cref="AppUser"/> — so nothing here can lock anybody out of the app.
/// </summary>
public class UserStore(AppDbContext db, ILogger<UserStore> log)
{
    public async Task<List<AppUser>> ListAsync(CancellationToken ct = default) =>
        await db.Users.OrderBy(u => u.Username).ToListAsync(ct);

    public async Task<AppUser?> FindAsync(int id, CancellationToken ct = default) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>
    /// Looks a name up as typed and as lower case. SQLite's LIKE is case-insensitive for ASCII,
    /// but relying on that would make the behaviour depend on a collation rather than on
    /// something written down, so the comparison is explicit.
    /// </summary>
    public async Task<AppUser?> FindByNameAsync(string? username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var normalised = Normalise(username);
        return await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == normalised, ct);
    }

    public static string Normalise(string username) => username.Trim().ToLowerInvariant();

    public async Task<UserResult> CreateAsync(
        string username, string? displayName, string password, UserRole role, CancellationToken ct = default)
    {
        var name = username?.Trim() ?? string.Empty;

        if (Validate(name, password) is { } problem)
        {
            return UserResult.Failed(problem);
        }

        if (await FindByNameAsync(name, ct) is not null)
        {
            return UserResult.Failed($"There is already an account called '{name}'.");
        }

        var user = new AppUser
        {
            Username = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = role
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Created account {Username} as {Role}", user.Username, user.Role);
        return UserResult.Succeeded(user, $"Created {user.Username}.");
    }

    public async Task<UserResult> UpdateAsync(
        int id, string? displayName, UserRole role, bool isActive, CancellationToken ct = default)
    {
        var user = await FindAsync(id, ct);
        if (user is null)
        {
            return UserResult.Failed("That account no longer exists.");
        }

        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        user.Role = role;
        user.IsActive = isActive;

        await db.SaveChangesAsync(ct);

        log.LogInformation("Updated account {Username}: {Role}, active {IsActive}",
            user.Username, user.Role, user.IsActive);

        return UserResult.Succeeded(user, $"Updated {user.Username}.");
    }

    public async Task<UserResult> SetPasswordAsync(int id, string password, CancellationToken ct = default)
    {
        var user = await FindAsync(id, ct);
        if (user is null)
        {
            return UserResult.Failed("That account no longer exists.");
        }

        if (PasswordProblem(password) is { } problem)
        {
            return UserResult.Failed(problem);
        }

        user.PasswordHash = PasswordHasher.Hash(password);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Password changed for {Username}", user.Username);
        return UserResult.Succeeded(user, $"Password changed for {user.Username}.");
    }

    public async Task<UserResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var user = await FindAsync(id, ct);
        if (user is null)
        {
            return UserResult.Failed("That account no longer exists.");
        }

        // The rows this account left behind record a username, not a foreign key, so its
        // history survives it — which is the point of attributing by name.
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Deleted account {Username}", user.Username);
        return UserResult.Succeeded(user, $"Deleted {user.Username}.");
    }

    public async Task RecordSignInAsync(int id, CancellationToken ct = default)
    {
        var user = await FindAsync(id, ct);
        if (user is null)
        {
            return;
        }

        user.LastSignInAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Short enough to be honest about, long enough to be worth requiring.</summary>
    public const int MinimumPasswordLength = 8;

    internal static string? Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "A username is required.";
        }

        if (username.Length > 64)
        {
            return "That username is too long.";
        }

        // Anything else ends up in a URL, a log line or a claim, and each of those has its own
        // opinion about spaces and punctuation.
        if (!username.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return "A username can contain letters, digits, dots, dashes and underscores.";
        }

        return PasswordProblem(password);
    }

    internal static string? PasswordProblem(string password) =>
        string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength
            ? $"A password of at least {MinimumPasswordLength} characters is required."
            : null;
}

public record UserResult(bool Success, AppUser? User, string Message)
{
    public static UserResult Succeeded(AppUser user, string message) => new(true, user, message);
    public static UserResult Failed(string message) => new(false, null, message);
}
