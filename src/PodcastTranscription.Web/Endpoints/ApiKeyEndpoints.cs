using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

public static class ApiKeyEndpoints
{
    public const string ManagementPolicy = "ManageOwnApiKeys";

    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        app.MapPost("/api-keys/{id:int}/revoke", async (
            int id, HttpContext context, ClaimsPrincipal principal, ApiKeyStore keys,
            IAntiforgery antiforgery, ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException ex)
            {
                logs.CreateLogger("ApiKeys").LogWarning(ex, "Rejected API key revocation with an invalid antiforgery token");
                return Results.Problem("Invalid antiforgery token.", statusCode: StatusCodes.Status400BadRequest);
            }

            var revoked = await keys.RevokeAsync(principal, id, ct);
            return Results.Redirect($"/api-keys?revoked={revoked.ToString().ToLowerInvariant()}");
        }).RequireAuthorization(ManagementPolicy);
    }
}
