using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Ingest;
using PodcastTranscription.Web.Services.Maintenance;
using PodcastTranscription.Web.Services.Security;
using PodcastTranscription.Web.Services.Summarization;
using System.Security.Claims;

namespace PodcastTranscription.Web.Endpoints;

/// <summary>
/// Every one-shot action in the UI, as an ordinary form post.
///
/// The same reason uploads and deletes already live here: an <c>@onclick</c> handler needs a
/// live Blazor circuit, and where the websocket does not connect the page renders perfectly and
/// every button silently does nothing. That is indistinguishable from a broken app, and it has
/// now been the cause of three separate rounds of "the button does nothing".
///
/// So nothing that *changes something* depends on the circuit any more. Each of these does the
/// work and redirects back to the page that asked, carrying its outcome in the query string —
/// which also means the result survives a refresh, and every one of them works with JavaScript
/// switched off entirely.
///
/// Each one also states the role it needs. Hiding a button from a viewer is a courtesy; the
/// endpoint refusing them is the actual rule, and it holds for anything that reaches the URL
/// directly.
/// </summary>
public static class ActionEndpoints
{
    public static void MapActionEndpoints(this WebApplication app)
    {
        MapSettings(app);
        MapEpisodes(app);
        MapJobs(app);
        MapFeeds(app);
    }

    /// <summary>
    /// The signed-in name, for attribution. Never null on these endpoints — they all sit behind
    /// a policy — but defensive anyway, since a null here would be recorded as "nobody did this".
    /// </summary>
    private static string? SignedInName(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? user.Identity.Name : null;

    /// <summary>Redirects back with a message for the page to show.</summary>
    private static IResult Back(string path, string? message = null, bool failed = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Results.Redirect(path);
        }

        var separator = path.Contains('?') ? "&" : "?";
        var key = failed ? "error" : "notice";

        return Results.Redirect($"{path}{separator}{key}={Uri.EscapeDataString(message)}");
    }

    // ------------------------------------------------------------------- settings --

    private static void MapSettings(WebApplication app)
    {
        app.MapPost("/settings/check-whisper", async (WhisperClient whisper, CancellationToken ct) =>
        {
            var health = await whisper.CheckHealthAsync(ct);

            return Back("/settings", health switch
            {
                { Ready: true } => $"{whisper.Endpoint} answered.",
                { ModelLoading: true } => $"{whisper.Endpoint} is still loading its model.",
                _ => $"{whisper.Endpoint} did not answer."
            }, failed: !health.Reachable);
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/settings/check-ollama", async (OllamaClient ollama, CancellationToken ct) =>
        {
            var health = await ollama.CheckHealthAsync(ct);

            return Back("/settings", health switch
            {
                { Ready: true } => $"{ollama.Endpoint} answered and has {ollama.Model}.",
                { Reachable: true } => $"{ollama.Endpoint} answered, but {health.Status}.",
                _ when !ollama.Enabled =>
                    "Summaries are off. Set Ollama__Enabled=true (OLLAMA_ENABLED in .env) and restart.",
                _ => $"{ollama.Endpoint} did not answer."
            }, failed: !health.Ready);
        }).RequireAuthorization(Roles.AdminPolicy);

        // The hash comes back in the query string rather than being stored: it is derived from a
        // password the operator has just typed, and it is going straight into their .env file.
        app.MapPost("/settings/hash", async (HttpRequest request, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var password = form["password"].ToString();

            if (string.IsNullOrWhiteSpace(password))
            {
                return Back("/settings", "Type a password to hash first.", failed: true);
            }

            return Results.Redirect("/settings?hash=" + Uri.EscapeDataString(PasswordHasher.Hash(password)));
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/settings/retention", async (MaintenanceService maintenance) =>
        {
            try
            {
                var result = await maintenance.PruneSourceAudioAsync();

                return Back("/settings", result.EpisodesPruned == 0
                    ? "Nothing eligible to prune."
                    : $"Pruned {result.EpisodesPruned} source file(s), freeing {TimeFormat.Size(result.BytesFreed)}.");
            }
            catch (Exception ex)
            {
                return Back("/settings", ex.Message, failed: true);
            }
        }).RequireAuthorization(Roles.AdminPolicy);

        app.MapPost("/settings/backup", async (MaintenanceService maintenance) =>
        {
            try
            {
                var result = await maintenance.BackupDatabaseAsync();

                return Back("/settings",
                    $"Backed up to {Path.GetFileName(result.Path)} ({TimeFormat.Size(result.Bytes)}).");
            }
            catch (Exception ex)
            {
                return Back("/settings", ex.Message, failed: true);
            }
        }).RequireAuthorization(Roles.AdminPolicy);
    }

    // ------------------------------------------------------------------- episodes --

    private static void MapEpisodes(WebApplication app)
    {
        app.MapPost("/episodes/{id:int}/metadata", async (
            int id, HttpRequest request, IAntiforgery antiforgery,
            EpisodeMetadataService metadata, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(request.HttpContext);
            var form = await request.ReadFormAsync(ct);
            var result = await metadata.UpdateAsync(
                id,
                form["title"].ToString(),
                form["show"].ToString(),
                ct);

            if (result.NotFound)
            {
                return Results.NotFound();
            }

            return result.Updated
                ? Back($"/episodes/{id}", "Episode details saved.")
                : Back($"/episodes/{id}?metadata=edit", result.Error, failed: true);
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/episodes/{id:int}/transcribe", async (
            int id, HttpRequest request, ClaimsPrincipal user, JobQueue queue, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var model = form["model"].ToString();

            await queue.EnqueueAsync(
                id,
                model: string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                queuedBy: SignedInName(user),
                ct: ct);

            return Back($"/episodes/{id}", "Queued for transcription.");
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/episodes/{id:int}/summarize", async (
            int id,
            HttpRequest request,
            AppDbContext db,
            SummaryRunner runner,
            SummaryService summaries,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);

            if (!int.TryParse(form["transcriptId"], out var transcriptId))
            {
                return Back($"/episodes/{id}", "No transcript to summarise.", failed: true);
            }

            if (!summaries.Enabled)
            {
                return Back($"/episodes/{id}",
                    "Summaries are off. Set OLLAMA_ENABLED=true in .env and restart.", failed: true);
            }

            var belongs = await db.Transcripts.AnyAsync(t => t.Id == transcriptId && t.EpisodeId == id, ct);
            if (!belongs)
            {
                return Results.NotFound();
            }

            // False means one is already in flight, which a double-click or a re-post makes
            // likely. Redirecting to the same place either way lands on the progress bar.
            runner.Start(transcriptId, id);

            return Results.Redirect($"/episodes/{id}?transcript={transcriptId}");
        }).RequireAuthorization(Roles.MemberPolicy);

        // Polled by the progress bar while a summary is being written. JSON rather than a page,
        // because it is read several times a minute and nothing about it needs rendering.
        app.MapGet("/episodes/{id:int}/summary-status", (int id, int transcriptId, SummaryRunner runner) =>
        {
            var run = runner.For(transcriptId);

            return run is null
                ? Results.Json(new { running = false, known = false })
                : Results.Json(new
                {
                    running = !run.Finished,
                    known = true,
                    stage = run.Stage,
                    pass = run.Pass,
                    totalPasses = run.TotalPasses,
                    percent = run.Percent,
                    elapsedSeconds = (int)run.Elapsed.TotalSeconds,
                    succeeded = run.Succeeded,
                    error = run.Error
                });
        });

        app.MapPost("/episodes/{id:int}/jobs/{jobId:int}/cancel", async (
            int id, int jobId, JobQueue queue, CancellationToken ct) =>
        {
            await queue.CancelAsync(jobId, ct);
            return Back($"/episodes/{id}", "Cancelling.");
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/episodes/{id:int}/jobs/{jobId:int}/retry", async (
            int id, int jobId, JobQueue queue, CancellationToken ct) =>
        {
            await queue.RetryAsync(jobId, ct);
            return Back($"/episodes/{id}", "Re-queued.");
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/episodes/{id:int}/segments/{segmentId:int}", async (
            int id, int segmentId, HttpRequest request, ClaimsPrincipal user,
            AppDbContext db, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var text = form["text"].ToString().Trim();

            var segment = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (segment is null)
            {
                return Results.NotFound();
            }

            if (text.Length > 0 && text != segment.Text)
            {
                segment.Text = text;
                segment.IsEdited = true;
                segment.EditedBy = SignedInName(user);
                segment.EditedAt = DateTimeOffset.UtcNow;

                // The word timings no longer describe this text, and keeping them would put the
                // playback highlight on words that are not there any more.
                segment.WordsJson = null;

                // The FTS triggers pick this up inside SQLite, so search reflects the correction
                // with nothing extra to remember here.
                await db.SaveChangesAsync(ct);
            }

            var transcriptId = segment.TranscriptId;
            // Straight back to the line just corrected, still in editing mode.
            return Results.Redirect($"/episodes/{id}?transcript={transcriptId}&edit=1#segment-{segmentId}");
        }).RequireAuthorization(Roles.MemberPolicy);
    }

    // ----------------------------------------------------------------------- jobs --

    private static void MapJobs(WebApplication app)
    {
        app.MapPost("/jobs/{id:int}/cancel", async (int id, JobQueue queue, CancellationToken ct) =>
        {
            await queue.CancelAsync(id, ct);
            return Back("/jobs", "Cancelling.");
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/jobs/{id:int}/retry", async (int id, JobQueue queue, CancellationToken ct) =>
        {
            await queue.RetryAsync(id, ct);
            return Back("/jobs", "Re-queued.");
        }).RequireAuthorization(Roles.MemberPolicy);
    }

    // ---------------------------------------------------------------------- feeds --

    private static void MapFeeds(WebApplication app)
    {
        app.MapPost("/feeds/subscribe", async (HttpRequest request, FeedService feeds, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var url = form["rssUrl"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(url))
            {
                return Back("/feeds", "Paste a feed URL first.", failed: true);
            }

            try
            {
                var feed = await feeds.SubscribeAsync(url, ct: ct);
                return Back($"/feeds/{feed.Id}", $"Subscribed to {feed.Title}.");
            }
            catch (Exception ex)
            {
                return Back("/feeds", ex.Message, failed: true);
            }
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/feeds/{id:int}/poll", async (int id, FeedService feeds, CancellationToken ct) =>
        {
            try
            {
                var result = await feeds.PollAsync(id, ct);

                return result.Error is { } error
                    ? Back($"/feeds/{id}", error, failed: true)
                    : Back($"/feeds/{id}", result.Discovered == 0
                        ? "No new episodes."
                        : $"Found {result.Discovered} new episode(s), queued {result.Queued}.");
            }
            catch (Exception ex)
            {
                return Back($"/feeds/{id}", ex.Message, failed: true);
            }
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/feeds/{id:int}/defaults", async (
            int id, HttpRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);

            var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (feed is null)
            {
                return Results.NotFound();
            }

            feed.AutoTranscribe = form["autoTranscribe"].ToString() is "on" or "true";
            feed.DefaultModel = Blank(form["model"]);
            feed.DefaultLanguage = Blank(form["language"]);
            feed.DefaultPrompt = Blank(form["prompt"]);

            await db.SaveChangesAsync(ct);

            return Back($"/feeds/{id}", "Saved.");
        }).RequireAuthorization(Roles.MemberPolicy);

        app.MapPost("/feeds/{id:int}/backfill", async (
            int id, HttpRequest request, FeedService feeds, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            _ = int.TryParse(form["count"], out var count);

            try
            {
                var queued = await feeds.BackfillAsync(id, count <= 0 ? 5 : count, ct);
                return Back($"/feeds/{id}", $"Queued {queued} episode(s).");
            }
            catch (Exception ex)
            {
                return Back($"/feeds/{id}", ex.Message, failed: true);
            }
        }).RequireAuthorization(Roles.MemberPolicy);

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
