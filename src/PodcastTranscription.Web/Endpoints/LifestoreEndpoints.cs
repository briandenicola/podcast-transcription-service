using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Services.Lifestore;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>
/// The read-only feed the household life store reads in full on each poll: every episode, in id
/// order, with its latest complete transcript as passages and its summary. Pages are small because
/// transcripts are large. It changes nothing.
/// </summary>
public static class LifestoreEndpoints
{
    public const string Schema = "podcasttranscription.lifestore.records/1";
    public const int DefaultLimit = 10;
    public const int MaxLimit = 25;
    private const string TokenPrefix = "episode:";

    public static void MapLifestoreEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/lifestore")
            .RequireAuthorization(ApiKeyAuthenticationHandler.ReadPolicy)
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });

        api.MapGet("/records", async (string? page, int? limit, AppDbContext db, CancellationToken ct) =>
        {
            var after = 0;
            if (!string.IsNullOrEmpty(page)
                && !(page.StartsWith(TokenPrefix, StringComparison.Ordinal)
                     && int.TryParse(page.AsSpan(TokenPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out after)
                     && after > 0))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["page"] = ["The page token is not one this feed gave out. Start again without a page."]
                });
            }

            // Asking for more than a page holds is answered with a full page, not an error: the
            // reader follows next either way.
            var count = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            // One extra row says whether another page follows.
            var episodes = await db.Episodes.AsNoTracking()
                .Where(e => e.Id > after)
                .OrderBy(e => e.Id)
                .Take(count + 1)
                .Select(e => new
                {
                    e.Id,
                    e.Title,
                    e.Show,
                    Feed = e.Feed == null ? null : e.Feed.Title,
                    e.SourceUrl,
                    e.PublishedAt,
                    e.DurationSec,
                    e.CreatedAt,
                    Transcript = e.Transcripts.Where(t => t.IsComplete)
                        .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                        .Select(t => new
                        {
                            t.Model,
                            t.Language,
                            t.CompletedAt,
                            t.QualityWarning,
                            Summary = t.Summary == null ? null : new { t.Summary.Content, t.Summary.Model, t.Summary.CreatedAt },
                            Segments = t.Segments.OrderBy(s => s.Ordinal).Select(s => new { s.StartMs, s.EndMs, s.Text }).ToList()
                        })
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            string? next = null;
            if (episodes.Count > count)
            {
                episodes = episodes[..count];
                next = TokenPrefix + episodes[^1].Id.ToString(CultureInfo.InvariantCulture);
            }

            var records = episodes.Select(e =>
            {
                var t = e.Transcript;
                var updated = new[] { e.CreatedAt, t?.CompletedAt ?? e.CreatedAt, t?.Summary?.CreatedAt ?? e.CreatedAt }.Max();
                return new
                {
                    kind = "episode",
                    id = e.Id.ToString(CultureInfo.InvariantCulture),
                    updatedAt = updated,
                    data = new
                    {
                        e.Id,
                        e.Title,
                        e.Show,
                        e.Feed,
                        e.SourceUrl,
                        e.PublishedAt,
                        e.DurationSec,
                        e.CreatedAt,
                        Transcript = t is null ? null : new
                        {
                            t.Model,
                            t.Language,
                            t.CompletedAt,
                            t.QualityWarning,
                            Summary = t.Summary is null ? null : new { t.Summary.Content, t.Summary.Model },
                            Passages = PassageGrouper.Group(t.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)))
                        }
                    }
                };
            }).ToList();

            return Results.Ok(new { schema = Schema, records, next });
        });
    }
}
