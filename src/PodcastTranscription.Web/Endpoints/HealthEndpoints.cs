using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Web.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Anonymous on purpose: this is what the container healthcheck and any external monitor
        // call, and neither of those can log in. It reports reachability, never configuration.
        app.MapGet("/healthz", async (WhisperClient whisper, AppDbContext db, CancellationToken ct) =>
        {
            var whisperReachable = await whisper.IsReachableAsync(ct);

            bool databaseReachable;
            int? queueDepth = null;
            try
            {
                queueDepth = await db.Jobs.CountAsync(j => j.State == JobState.Queued, ct);
                databaseReachable = true;
            }
            catch (Exception)
            {
                databaseReachable = false;
            }

            var healthy = whisperReachable && databaseReachable;

            return Results.Json(new
            {
                status = healthy ? "healthy" : "degraded",
                whisper = new { endpoint = whisper.Endpoint, model = whisper.Model, reachable = whisperReachable },
                database = new { reachable = databaseReachable, queued = queueDepth }
            },
            statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
    }
}
