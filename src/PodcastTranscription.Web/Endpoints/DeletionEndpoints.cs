using Microsoft.AspNetCore.Antiforgery;
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
        app.MapPost("/delete/episode/{id:int}/execute", async (
            int id, HttpRequest request, IAntiforgery antiforgery,
            DeletionService deletion, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(request.HttpContext);
            var form = await request.ReadFormAsync(ct);
            var returnUrl = SafeLibraryReturnUrl(form["returnUrl"].ToString());
            var result = await deletion.DeleteEpisodeAsync(id, ct);

            return result.Deleted
                ? Results.Redirect(returnUrl ?? "/")
                : Results.Redirect(AddMessage(returnUrl ?? $"/episodes/{id}", "error", result.Refusal!));
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/delete/episodes/execute", async (
            HttpRequest request, IAntiforgery antiforgery,
            DeletionService deletion, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(request.HttpContext);
            var form = await request.ReadFormAsync(ct);
            var returnUrl = SafeLibraryReturnUrl(form["returnUrl"].ToString()) ?? "/";
            var ids = form["episodeIds"]
                .Select(value => int.TryParse(value, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .Take(100)
                .ToList();

            var deleted = 0;
            var refusals = new List<string>();
            foreach (var id in ids)
            {
                var result = await deletion.DeleteEpisodeAsync(id, ct);
                if (result.Deleted)
                {
                    deleted++;
                }
                else if (result.Refusal is { } refusal)
                {
                    refusals.Add(refusal);
                }
            }

            var message = refusals.Count == 0
                ? $"Deleted {deleted} episode(s)."
                : $"Deleted {deleted} episode(s); {refusals.Count} could not be deleted. {refusals[0]}";
            return Results.Redirect(AddMessage(
                returnUrl,
                refusals.Count == 0 ? "notice" : "error",
                message));
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/delete/transcript/{id:int}/execute", async (
            int id, int episodeId, DeletionService deletion, CancellationToken ct) =>
        {
            var result = await deletion.DeleteTranscriptAsync(id, ct);

            return Results.Redirect(result.Deleted
                ? $"/episodes/{episodeId}"
                : $"/episodes/{episodeId}?error=" + Uri.EscapeDataString(result.Refusal!));
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/delete/job/{id:int}/execute", async (int id, DeletionService deletion, CancellationToken ct) =>
        {
            var result = await deletion.DeleteJobAsync(id, ct);

            return Results.Redirect(result.Deleted
                ? "/jobs"
                : "/jobs?error=" + Uri.EscapeDataString(result.Refusal!));
        }).RequireAuthorization(Roles.AdminPolicy);
    }

    private static string? SafeLibraryReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && Uri.TryCreate(returnUrl, UriKind.Relative, out _)
            ? returnUrl
            : null;

    private static string AddMessage(string path, string key, string message)
    {
        var separator = path.Contains('?') ? "&" : "?";
        return $"{path}{separator}{key}={Uri.EscapeDataString(message)}";
    }
}
