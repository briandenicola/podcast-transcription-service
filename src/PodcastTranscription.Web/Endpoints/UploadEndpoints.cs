using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Security;
using System.Security.Claims;
using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>
/// Ingest as ordinary form posts.
///
/// Blazor's InputFile streams the file over the SignalR circuit, which means a 2 GB episode is
/// chunked through a websocket, and nothing works at all if the circuit has not connected — the
/// page looks fine but the button does nothing. A plain multipart POST is faster for large files
/// and works with no JavaScript running at all.
///
/// Members and above: adding an episode is the work, not the configuration.
/// </summary>
public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this WebApplication app)
    {
        app.MapPost("/upload/file", async (
            HttpRequest request,
            ClaimsPrincipal user,
            EpisodeImporter importer,
            JobQueue queue,
            MediaStore media,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Upload");

            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected a multipart form.");
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];

            if (file is null || file.Length == 0)
            {
                return Results.Redirect("/upload?error=" + Uri.EscapeDataString("Choose an audio file first."));
            }

            if (file.Length > media.MaxUploadBytes)
            {
                return Results.Redirect("/upload?error=" + Uri.EscapeDataString(
                    $"That file is {TimeFormat.Size(file.Length)}; the limit is {TimeFormat.Size(media.MaxUploadBytes)}."));
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var result = await importer.ImportAsync(
                    stream, file.FileName, form["title"], form["show"], ct);

                if (!result.WasDuplicate)
                {
                    await queue.EnqueueAsync(result.Episode.Id, queuedBy: user.Identity?.Name, ct: ct);
                }

                log.LogInformation("Uploaded {FileName} as episode {EpisodeId}", file.FileName, result.Episode.Id);

                var suffix = result.WasDuplicate ? "?duplicate=1" : string.Empty;
                return Results.Redirect($"/episodes/{result.Episode.Id}{suffix}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Upload of {FileName} failed", file.FileName);
                return Results.Redirect("/upload?error=" + Uri.EscapeDataString(ex.Message));
            }
        }).RequireAuthorization(Roles.MemberPolicy);
        // The body-size ceiling is raised globally in Program.cs, from Storage:MaxUploadMb.

        app.MapPost("/upload/url", async (
            HttpRequest request,
            ClaimsPrincipal user,
            EpisodeImporter importer,
            JobQueue queue,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var url = form["url"].ToString();

            try
            {
                var episode = await importer.ImportFromUrlAsync(url, form["title"], form["show"], ct);
                await queue.EnqueueAsync(episode.Id, queuedBy: user.Identity?.Name, ct: ct);

                return Results.Redirect($"/episodes/{episode.Id}");
            }
            catch (IngestException ex)
            {
                return Results.Redirect("/upload?tab=url&error=" + Uri.EscapeDataString(ex.Message));
            }
        }).RequireAuthorization(Roles.MemberPolicy);
    }
}
