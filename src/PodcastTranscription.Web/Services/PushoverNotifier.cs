using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Pushes a Pushover notification when an episode a feed opted in on finishes transcription or
/// summarisation.
///
/// Modelled on the same trade the pipeline already makes for summaries: a notification is a
/// nice-to-have bolted onto work that already succeeded, so nothing here is allowed to throw.
/// Losing an hour of transcription because Pushover's API was unreachable would be a far worse
/// failure than a silent missed notification.
/// </summary>
public class PushoverNotifier(AppDbContext db, IHttpClientFactory httpClientFactory, ILogger<PushoverNotifier> log)
{
    private const string ApiUrl = "https://api.pushover.net/1/messages.json";

    /// <summary>
    /// Notifies for one episode's event, but only when its feed opted in and credentials are
    /// configured. Episodes with no feed (uploads, pasted URLs) have no opt-in to check and are
    /// silently skipped — there is nothing to have configured.
    ///
    /// Never throws. The pipeline calls this right after saving a finished transcript, and
    /// <see cref="Summarization.SummaryService"/> calls it right after saving a summary — in both
    /// places the actual work is already done and persisted, and a database hiccup or a bug here
    /// must not turn a completed job into a failed one.
    /// </summary>
    public async Task NotifyEpisodeAsync(int episodeId, string eventLabel, CancellationToken ct = default)
    {
        try
        {
            var episode = await db.Episodes.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == episodeId, ct);

            if (episode?.FeedId is not { } feedId)
            {
                return;
            }

            var feed = await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == feedId, ct);
            if (feed is not { NotifyPushover: true })
            {
                return;
            }

            var settings = await db.PushoverSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings is null || string.IsNullOrWhiteSpace(settings.AppToken) || string.IsNullOrWhiteSpace(settings.UserKey))
            {
                log.LogDebug("Skipping Pushover notification for episode {EpisodeId}: not configured", episodeId);
                return;
            }

            var (success, error) = await SendAsync(
                settings.AppToken, settings.UserKey, feed.Title, $"{eventLabel}: {episode.Title}", ct);

            if (!success)
            {
                log.LogWarning("Pushover notification for episode {EpisodeId} failed: {Error}", episodeId, error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not send a Pushover notification for episode {EpisodeId}", episodeId);
        }
    }

    /// <summary>Reads back whether both credentials are set, for the settings page — never the values themselves.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var settings = await db.PushoverSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return settings is not null
            && !string.IsNullOrWhiteSpace(settings.AppToken)
            && !string.IsNullOrWhiteSpace(settings.UserKey);
    }

    /// <summary>
    /// Upserts the singleton settings row. Blank fields leave the existing value alone, the same
    /// way the account password form works — so re-saving one field never blanks the other.
    /// </summary>
    public async Task SaveSettingsAsync(string? appToken, string? userKey, CancellationToken ct = default)
    {
        var settings = await db.PushoverSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new PushoverSettings();
            db.PushoverSettings.Add(settings);
        }

        if (!string.IsNullOrWhiteSpace(appToken))
        {
            settings.AppToken = appToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(userKey))
        {
            settings.UserKey = userKey.Trim();
        }

        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a real notification with the saved credentials, for the "send test" button.</summary>
    public async Task<(bool Success, string? Error)> SendTestAsync(CancellationToken ct = default)
    {
        var settings = await db.PushoverSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.AppToken) || string.IsNullOrWhiteSpace(settings.UserKey))
        {
            return (false, "Set the Pushover app token and user key first.");
        }

        return await SendAsync(settings.AppToken, settings.UserKey, "Podcast Transcription",
            "This is a test notification.", ct);
    }

    private async Task<(bool Success, string? Error)> SendAsync(
        string appToken, string userKey, string title, string message, CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient(nameof(PushoverNotifier));
            var form = new Dictionary<string, string>
            {
                ["token"] = appToken,
                ["user"] = userKey,
                ["title"] = title,
                ["message"] = message
            };

            using var response = await http.PostAsync(ApiUrl, new FormUrlEncodedContent(form), ct);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return (false, $"Pushover returned {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
