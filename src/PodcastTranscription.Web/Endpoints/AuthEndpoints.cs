using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Signing out needs an HttpContext, which a Blazor circuit does not have, so it is a
        // plain endpoint the layout links to.
        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(AdminAuthenticator.CookieScheme);
            return Results.Redirect("/login");
        });
    }
}
