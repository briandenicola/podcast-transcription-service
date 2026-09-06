using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Ingest;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>Body of <c>POST /api/episodes</c>.</summary>
public record SubmitEpisodeRequest(string Url, string? Title, string? Show, string? Language, string? Prompt);

public static class EpisodeApiEndpoints
{
    public static void MapEpisodeApiEndpoints(this WebApplication app)
    {
        // Authenticated by API key rather than the admin cookie, so cron jobs and scripts can
        // submit without holding a session. AllowAnonymous only bypasses the cookie policy —
        // the filter below still rejects anything without a valid key.
        var api = app.MapGroup("/api")
            .AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                var admin = context.HttpContext.RequestServices.GetRequiredService<AdminAuthenticator>();

                if (!admin.ApiEnabled)
                {
                    // No key configured means the API is off, not that any key will do.
                    return Results.Problem(
                        "The submission API is disabled. Set Auth:ApiKey to enable it.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var presented = context.HttpContext.Request.Headers[AdminAuthenticator.ApiKeyHeader].ToString();
                if (!admin.ValidateApiKey(presented))
                {
                    return Results.Problem("Missing or invalid API key.", statusCode: StatusCodes.Status401Unauthorized);
                }

                return await next(context);
            });

        api.MapPost("/episodes", async (
            SubmitEpisodeRequest request,
            EpisodeImporter importer,
            JobQueue queue,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("EpisodeApi");

            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["url"] = ["A url is required."]
                });
            }

            try
            {
                var episode = await importer.ImportFromUrlAsync(request.Url, request.Title, request.Show, ct);
                var job = await queue.EnqueueAsync(episode.Id, request.Language, request.Prompt, ct: ct);

                log.LogInformation("API submitted episode {EpisodeId} as job {JobId}", episode.Id, job.Id);

                return Results.Created($"/api/episodes/{episode.Id}", new
                {
                    episodeId = episode.Id,
                    jobId = job.Id,
                    status = job.State.ToString(),
                    url = $"/episodes/{episode.Id}"
                });
            }
            catch (IngestException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["url"] = [ex.Message]
                });
            }
        });

        // Enough to poll for completion without opening the UI.
        api.MapGet("/episodes/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var episode = await db.Episodes
                .AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new
                {
                    e.Id,
                    e.Title,
                    e.Show,
                    e.SourceUrl,
                    e.DurationSec,
                    e.CreatedAt,
                    Transcripts = e.Transcripts
                        .OrderByDescending(t => t.CreatedAt)
                        .Select(t => new { t.Id, t.Model, t.Language, t.IsComplete, t.CreatedAt, Segments = t.Segments.Count() })
                        .ToList(),
                    LatestJob = e.Jobs
                        .OrderByDescending(j => j.CreatedAt)
                        .Select(j => new { j.Id, State = j.State.ToString(), j.Progress, j.Attempts, j.LastError })
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

            return episode is null ? Results.NotFound() : Results.Ok(episode);
        });
    }
}
