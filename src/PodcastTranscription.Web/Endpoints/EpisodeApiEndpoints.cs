using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Ingest;
using PodcastTranscription.Web.Services.Search;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>Body of <c>POST /api/episodes</c>.</summary>
public record SubmitEpisodeRequest(string Url, string? Title, string? Show, string? Language, string? Prompt);

public static class EpisodeApiEndpoints
{
    public static void MapEpisodeApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api")
            .RequireAuthorization(ApiKeyAuthenticationHandler.ReadPolicy)
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });

        api.MapGet("/episodes", async (int? limit, string? show, AppDbContext db, CancellationToken ct) =>
        {
            var count = limit ?? 30;
            if (count is < 1 or > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["Limit must be between 1 and 100."]
                });
            }

            var episodes = db.Episodes.AsNoTracking();
            if (!string.IsNullOrEmpty(show))
            {
                episodes = episodes.Where(e => e.Show == show);
            }

            var recent = await episodes
                .OrderByDescending(e => e.PublishedAt ?? e.CreatedAt)
                .ThenByDescending(e => e.Id)
                .Take(count)
                .Select(e => new
                {
                    e.Id,
                    e.Title,
                    e.Show,
                    e.PublishedAt,
                    e.DurationSec,
                    LatestJob = e.Jobs.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
                        .Select(j => new { State = j.State.ToString(), j.Progress })
                        .FirstOrDefault(),
                    SummaryPreview = e.Transcripts.Where(t => t.IsComplete)
                        .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                        .Select(t => t.Summary == null ? null
                            : t.Summary.Content.Length > 300 ? t.Summary.Content.Substring(0, 300) : t.Summary.Content)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            return Results.Ok(recent);
        });

        api.MapGet("/episodes/{id:int}/transcript", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var transcript = await db.Transcripts.AsNoTracking()
                .Where(t => t.EpisodeId == id && t.IsComplete)
                .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                .Select(t => new
                {
                    Segments = t.Segments.OrderBy(s => s.Ordinal)
                        .Select(s => new { s.StartMs, s.EndMs, s.Text }).ToList(),
                    Summary = t.Summary == null ? null : new { t.Summary.Content, t.Summary.Model }
                })
                .FirstOrDefaultAsync(ct);

            return transcript is null ? Results.NotFound() : Results.Ok(transcript);
        });

        api.MapGet("/search", async (string? q, SearchService search, CancellationToken ct) =>
        {
            if (FtsQuery.Build(q) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["q"] = ["A search query containing letters or digits is required."]
                });
            }

            var results = await search.SearchAsync(q, limit: 100, ct: ct);
            return Results.Ok(results.Episodes.SelectMany(e => e.Hits)
                .OrderBy(h => h.Rank).ThenBy(h => h.EpisodeId).ThenBy(h => h.StartMs)
                .Select(h => new
                {
                    h.EpisodeId,
                    Title = h.EpisodeTitle,
                    h.StartMs,
                    Excerpt = h.Snippet.Replace("\u0001", "").Replace("\u0002", "")
                }).ToList());
        });

        api.MapPost("/episodes", async (
            SubmitEpisodeRequest request,
            ClaimsPrincipal principal,
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
                var job = await queue.EnqueueAsync(episode.Id, request.Language, request.Prompt,
                    queuedBy: principal.Identity?.Name, ct: ct);

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
        }).RequireAuthorization(ApiKeyAuthenticationHandler.SubmitPolicy);

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
