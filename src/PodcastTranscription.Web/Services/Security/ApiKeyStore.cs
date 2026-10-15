using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Security;

public class ApiKeyStore(AppDbContext db, AdminAuthenticator admin, ILogger<ApiKeyStore> log)
{
    public async Task<List<ApiKey>> ListAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var owner = await FindOwnerAsync(principal, ct);
        return owner is null ? [] : await OwnedKeys(owner).AsNoTracking()
            .OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
    }

    public async Task<ApiKeyCreation> CreateAsync(
        ClaimsPrincipal principal, string? name, bool canSubmit, CancellationToken ct = default)
    {
        var owner = await FindOwnerAsync(principal, ct);
        if (owner is null)
        {
            return new(null, "Your account is no longer active. Sign in again.");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            return new(null, "A key name of 1 to 100 characters is required.");
        }

        if (canSubmit && !CanSubmit(owner.Role))
        {
            return new(null, "Only Members and Admins can create submission keys.");
        }

        var secret = "pt_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            UserId = owner.UserId,
            ConfiguredUsername = owner.ConfiguredUsername,
            Name = name.Trim(),
            Prefix = secret[..11],
            KeyHash = Hash(secret),
            CanSubmit = canSubmit
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        log.LogInformation("API key {KeyId} created by {Username}, submit {CanSubmit}",
            key.Id, owner.Username, canSubmit);
        return new(secret, $"Created {key.Name}. Copy the key now; it will not be shown again.");
    }

    public async Task<bool> RevokeAsync(ClaimsPrincipal principal, int id, CancellationToken ct = default)
    {
        var owner = await FindOwnerAsync(principal, ct);
        if (owner is null)
        {
            return false;
        }

        var key = await OwnedKeys(owner).FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return false;
        }

        key.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        log.LogInformation("API key {KeyId} revoked by {Username}", key.Id, owner.Username);
        return true;
    }

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string secret, CancellationToken ct = default)
    {
        var hash = Hash(secret);
        var key = await db.ApiKeys.AsNoTracking().Include(k => k.User)
            .SingleOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (key is null)
        {
            return null;
        }

        Owner? owner = key.UserId is not null
            ? key.User is { IsActive: true } user
                ? new(user.Id, null, user.Username, user.Role) : null
            : admin.HasPassword && key.ConfiguredUsername == admin.Username
                ? new(null, admin.Username, admin.Username, UserRole.Admin) : null;
        if (owner is null)
        {
            return null;
        }

        // A narrow update cannot overwrite a concurrent revocation or account change.
        await db.ApiKeys.Where(k => k.Id == key.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow), ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, owner.Username),
            new(ClaimTypes.Role, Roles.Name(owner.Role)),
            new(ApiKeyAuthenticationHandler.PermissionClaim, "read")
        };
        if (key.CanSubmit && CanSubmit(owner.Role))
        {
            claims.Add(new(ApiKeyAuthenticationHandler.PermissionClaim, "submit"));
        }
        if (owner.UserId is { } userId)
        {
            claims.Add(new(ClaimTypes.NameIdentifier, userId.ToString()));
        }
        return new(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
    }

    public async Task<bool> CanSubmitAsync(ClaimsPrincipal principal, CancellationToken ct = default) =>
        await FindOwnerAsync(principal, ct) is { } owner && CanSubmit(owner.Role);

    private async Task<Owner?> FindOwnerAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.Identity is not { IsAuthenticated: true, AuthenticationType: AdminAuthenticator.CookieScheme })
        {
            return null;
        }

        var identifier = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (identifier is not null)
        {
            if (!int.TryParse(identifier, out var id))
            {
                return null;
            }
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
            return user is null ? null : new(user.Id, null, user.Username, user.Role);
        }

        return admin.HasPassword && principal.Identity.Name == admin.Username
            ? new(null, admin.Username, admin.Username, UserRole.Admin) : null;
    }

    private IQueryable<ApiKey> OwnedKeys(Owner owner) => owner.UserId is { } id
        ? db.ApiKeys.Where(k => k.UserId == id)
        : db.ApiKeys.Where(k => k.UserId == null && k.ConfiguredUsername == owner.ConfiguredUsername);

    private static bool CanSubmit(UserRole role) => role is UserRole.Member or UserRole.Admin;
    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private sealed record Owner(int? UserId, string? ConfiguredUsername, string Username, UserRole Role);
}

public record ApiKeyCreation(string? Secret, string Message);
