using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Ingest;

public record PollOutcome(int Discovered, int Queued, string? Error)
{
    public static PollOutcome Failed(string error) => new(0, 0, error);
}

/// <summary>
/// Subscribing to feeds and polling them.
///
/// A poll records what is new and, when the feed says so, queues it. Existing items are recorded
/// but not queued on first subscribe: subscribing to a show with ten years of back catalogue
/// should not enqueue ten years of audio. Backfill exists for when that is what you actually want.
/// </summary>
public class FeedService(
    AppDbContext db,
    JobQueue queue,
    IHttpClientFactory httpClientFactory,
    PodcastUrlResolver resolver,
    IOptions<IngestOptions> options,
    ILogger<FeedService> log)
{
    private readonly IngestOptions _options = options.Value;

    public async Task<Feed> SubscribeAsync(
        string rssUrl, bool autoTranscribe = true, bool notifyPushover = false, CancellationToken ct = default)
    {
        if (!YtDlpClient.IsSupportedUrl(rssUrl))
        {
            throw new IngestException($"'{rssUrl}' is not an http or https URL.");
        }

        // An Apple Podcasts link is what people actually have to hand; swap it for the real feed
        // before anything else looks at it.
        var normalized = (await resolver.ResolveAsync(rssUrl, ct)).Trim();

        var existing = await db.Feeds.FirstOrDefaultAsync(f => f.RssUrl == normalized, ct);
        if (existing is not null)
        {
            return existing;
        }

        var parsed = FeedParser.Parse(await FetchAsync(normalized, ct));

        var feed = new Feed
        {
            Title = parsed.Title,
            RssUrl = normalized,
            AutoTranscribe = autoTranscribe,
            NotifyPushover = notifyPushover
        };

        db.Feeds.Add(feed);
        await db.SaveChangesAsync(ct);

        // Record what is already there so the first real poll does not see a decade of episodes
        // as new, but do not queue any of it.
        var recorded = await RecordItemsAsync(feed, parsed.Items, queueThem: _options.QueueExistingItemsOnSubscribe, ct);

        feed.LastPolledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Subscribed to {Title} ({Url}); recorded {Count} existing episodes",
            feed.Title, feed.RssUrl, recorded.Discovered);

        return feed;
    }

    /// <summary>Fetches the feed and takes anything not seen before.</summary>
    public async Task<PollOutcome> PollAsync(int feedId, CancellationToken ct = default)
    {
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == feedId, ct);
        if (feed is null)
        {
            return PollOutcome.Failed($"Feed {feedId} not found.");
        }

        try
        {
            var parsed = FeedParser.Parse(await FetchAsync(feed.RssUrl, ct));

            var outcome = await RecordItemsAsync(feed, parsed.Items, queueThem: feed.AutoTranscribe, ct);

            // Keep the feed's own title current; publishers rename shows.
            if (!string.IsNullOrWhiteSpace(parsed.Title))
            {
                feed.Title = parsed.Title;
            }

            feed.LastPolledAt = DateTimeOffset.UtcNow;
            feed.LastError = null;
            await db.SaveChangesAsync(ct);

            if (outcome.Discovered > 0)
            {
                log.LogInformation("Feed {Title}: {Discovered} new, {Queued} queued",
                    feed.Title, outcome.Discovered, outcome.Queued);
            }

            return outcome;
        }
        catch (Exception ex) when (ex is IngestException or HttpRequestException or TaskCanceledException)
        {
            // A feed being unreachable is routine — a bad gateway, a lapsed domain. Record it and
            // let the next poll try again.
            feed.LastPolledAt = DateTimeOffset.UtcNow;
            feed.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);

            log.LogWarning(ex, "Polling {Title} failed", feed.Title);
            return PollOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Queues the most recent already-recorded episodes of a feed that have no transcript yet.
    /// The deliberate counterpart to a poll only taking new items.
    /// </summary>
    public async Task<int> BackfillAsync(int feedId, int count, CancellationToken ct = default)
    {
        var episodes = await db.Episodes
            .Where(e => e.FeedId == feedId && !e.Transcripts.Any() && !e.Jobs.Any())
            .OrderByDescending(e => e.PublishedAt ?? e.CreatedAt)
            .Take(Math.Clamp(count, 1, 500))
            .Select(e => e.Id)
            .ToListAsync(ct);

        foreach (var episodeId in episodes)
        {
            await queue.EnqueueAsync(episodeId, ct: ct);
        }

        log.LogInformation("Backfilled {Count} episode(s) for feed {FeedId}", episodes.Count, feedId);
        return episodes.Count;
    }

    /// <summary>
    /// Adds episodes for items the feed has that the database does not, matched on the feed's own
    /// guid. This runs before anything is downloaded, so there is no hash to compare yet — the
    /// byte-level duplicate check happens in the pipeline once the audio arrives.
    /// </summary>
    private async Task<PollOutcome> RecordItemsAsync(
        Feed feed, IReadOnlyList<FeedItem> items, bool queueThem, CancellationToken ct)
    {
        var candidates = items
            .Where(i => YtDlpClient.IsSupportedUrl(i.AudioUrl))
            .Take(_options.MaxItemsPerPoll)
            .ToList();

        if (candidates.Count == 0)
        {
            return new PollOutcome(0, 0, null);
        }

        var guids = candidates.Select(i => i.Guid).ToList();
        var known = await db.Episodes
            .Where(e => e.FeedId == feed.Id && e.FeedItemGuid != null && guids.Contains(e.FeedItemGuid))
            .Select(e => e.FeedItemGuid!)
            .ToListAsync(ct);

        // A deleted episode's guid must count as "seen" too, or the next poll mistakes the
        // deletion for having never recorded it and brings it straight back.
        var deleted = await db.DeletedFeedItems
            .Where(d => d.FeedId == feed.Id && guids.Contains(d.FeedItemGuid))
            .Select(d => d.FeedItemGuid)
            .ToListAsync(ct);

        var seen = known.Concat(deleted).ToHashSet();
        var added = new List<Episode>();

        foreach (var item in candidates)
        {
            if (!seen.Add(item.Guid))
            {
                continue;
            }

            var episode = new Episode
            {
                Title = item.Title,
                Show = feed.Title,
                SourceUrl = item.AudioUrl,
                PublishedAt = item.PublishedAt,
                DurationSec = item.DurationSec,
                FeedId = feed.Id,
                FeedItemGuid = item.Guid,
                // Filled in by the pipeline once the audio is downloaded and hashed.
                AudioPath = string.Empty,
                AudioSha256 = string.Empty
            };

            db.Episodes.Add(episode);
            added.Add(episode);
        }

        if (added.Count == 0)
        {
            return new PollOutcome(0, 0, null);
        }

        await db.SaveChangesAsync(ct);

        var queued = 0;
        if (queueThem)
        {
            foreach (var episode in added)
            {
                await queue.EnqueueAsync(episode.Id, ct: ct);
                queued++;
            }
        }

        return new PollOutcome(added.Count, queued, null);
    }

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(nameof(FeedService));
        http.Timeout = TimeSpan.FromSeconds(_options.FeedTimeoutSeconds);

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new IngestException($"The feed returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }
}
