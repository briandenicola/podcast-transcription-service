using System.Security.Claims;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>
/// Managing accounts, as form posts like everything else that changes something.
///
/// Admin-only, and with two guards against the obvious self-inflicted wound: you cannot change
/// your own role, and you cannot delete or deactivate the account you are signed in as. The
/// configured admin is always there as a way back in, but discovering that by locking yourself
/// out mid-session is a bad way to learn it.
/// </summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapPost("/users/create", async (
            HttpRequest request, UserStore users, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);

            var result = await users.CreateAsync(
                form["username"].ToString(),
                form["displayName"].ToString(),
                form["password"].ToString(),
                ParseRole(form["role"]),
                ct);

            return Back(result.Message, result.Success);
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/users/{id:int}/update", async (
            int id, HttpRequest request, ClaimsPrincipal signedIn, UserStore users, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var user = await users.FindAsync(id, ct);

            if (user is null)
            {
                return Back("That account no longer exists.", success: false);
            }

            if (IsSelf(signedIn, user))
            {
                var role = ParseRole(form["role"]);
                var active = IsChecked(form["isActive"]);

                if (role != user.Role || !active)
                {
                    return Back(
                        "You cannot change your own role or deactivate yourself. Ask another admin, "
                        + "or sign in as the configured admin.", success: false);
                }
            }

            var result = await users.UpdateAsync(
                id, form["displayName"].ToString(), ParseRole(form["role"]), IsChecked(form["isActive"]), ct);

            return Back(result.Message, result.Success);
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/users/{id:int}/password", async (
            int id, HttpRequest request, UserStore users, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var result = await users.SetPasswordAsync(id, form["password"].ToString(), ct);

            return Back(result.Message, result.Success);
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/users/{id:int}/delete", async (
            int id, ClaimsPrincipal signedIn, UserStore users, CancellationToken ct) =>
        {
            var user = await users.FindAsync(id, ct);

            if (user is not null && IsSelf(signedIn, user))
            {
                return Back("You cannot delete the account you are signed in as.", success: false);
            }

            var result = await users.DeleteAsync(id, ct);
            return Back(result.Message, result.Success);
        }).RequireAuthorization(Roles.AdminPolicy);
    }

    private static bool IsSelf(ClaimsPrincipal signedIn, AppUser user) =>
        string.Equals(signedIn.Identity?.Name, user.Username, StringComparison.OrdinalIgnoreCase);

    /// <summary>An unchecked checkbox posts nothing at all, which is how "off" arrives.</summary>
    private static bool IsChecked(string? value) => value is "on" or "true";

    /// <summary>Anything unrecognised becomes the least privileged role rather than the most.</summary>
    internal static UserRole ParseRole(string? value) =>
        Enum.TryParse<UserRole>(value, ignoreCase: true, out var role) ? role : UserRole.Viewer;

    private static IResult Back(string message, bool success) =>
        Results.Redirect($"/users?{(success ? "notice" : "error")}={Uri.EscapeDataString(message)}");
}
