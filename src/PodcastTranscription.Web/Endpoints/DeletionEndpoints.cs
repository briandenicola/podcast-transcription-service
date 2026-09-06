using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>
/// Deletion as form posts.
///
/// Deleting used to be an @onclick handler, which needs a live Blazor circuit — so wherever the
/// circuit does not connect, the button silently did nothing. Destructive actions are the worst
/// possible place for that, and they are one-shot actions that never needed a circuit anyway.
///
/// All of them are admin-only. Deletion is the one thing that cannot be undone from inside the
/// app, so it is not something a member should reach by guessing a URL.
/// </summary>
public static class DeletionEndpoints
{
    public static void MapDeletionEndpoints(this WebApplication app)
    {
        app.MapPost("/delete/episode/{id:int}", async (int id, DeletionService deletion, CancellationToken ct) =>
        {
            var result = await deletion.DeleteEpisodeAsync(id, ct);

            return result.Deleted
                ? Results.Redirect("/")
                : Results.Redirect($"/episodes/{id}?error=" + Uri.EscapeDataString(result.Refusal!));
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/delete/transcript/{id:int}", async (
            int id, int episodeId, DeletionService deletion, CancellationToken ct) =>
        {
            var result = await deletion.DeleteTranscriptAsync(id, ct);

            return Results.Redirect(result.Deleted
                ? $"/episodes/{episodeId}"
                : $"/episodes/{episodeId}?error=" + Uri.EscapeDataString(result.Refusal!));
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/delete/job/{id:int}", async (int id, DeletionService deletion, CancellationToken ct) =>
        {
            var result = await deletion.DeleteJobAsync(id, ct);

            return Results.Redirect(result.Deleted
                ? "/jobs"
                : "/jobs?error=" + Uri.EscapeDataString(result.Refusal!));
        }).RequireAuthorization(Roles.AdminPolicy);
    }
}
