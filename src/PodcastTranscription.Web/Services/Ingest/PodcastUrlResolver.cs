using System.Text.Json;
using System.Text.RegularExpressions;

namespace PodcastTranscription.Web.Services.Ingest;

/// <summary>
/// Turns a podcast directory link into the RSS feed behind it.
///
/// Nobody browsing for a show ends up holding its RSS URL — they have the Apple Podcasts page
/// they were listening on. Rejecting that as "not a feed" is technically correct and useless, so
/// the id is pulled out of the link and exchanged for the real feed through the public iTunes
/// lookup API.
/// </summary>
public partial class PodcastUrlResolver(IHttpClientFactory httpClientFactory, ILogger<PodcastUrlResolver> log)
{
    [GeneratedRegex(@"^https?://(?:[a-z0-9-]+\.)*apple\.com/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplePodcastsHost();

    /// <summary>The numeric collection id, which appears as <c>/id1569466131</c> or <c>?i=…</c>.</summary>
    [GeneratedRegex(@"/id(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AppleCollectionId();

    public static bool IsApplePodcastsUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && ApplePodcastsHost().IsMatch(url.Trim());

    internal static string? ExtractAppleId(string url)
    {
        var match = AppleCollectionId().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Returns the RSS URL for a directory link, or the input unchanged when it is already a feed.
    /// </summary>
    public async Task<string> ResolveAsync(string url, CancellationToken ct = default)
    {
        var trimmed = url.Trim();

        if (!IsApplePodcastsUrl(trimmed))
        {
            return trimmed;
        }

        var id = ExtractAppleId(trimmed)
            ?? throw new IngestException(
                "That looks like an Apple Podcasts link, but it has no show id in it. "
                + "Open the show's page and copy the URL, which ends in something like /id1569466131.");

        var http = httpClientFactory.CreateClient(nameof(FeedService));
        http.Timeout = TimeSpan.FromSeconds(20);

        using var response = await http.GetAsync(
            $"https://itunes.apple.com/lookup?id={id}&entity=podcast", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new IngestException(
                $"Could not look that show up with Apple ({(int)response.StatusCode}). "
                + "Paste the podcast's RSS URL instead.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var feedUrl = ReadFeedUrl(body)
            ?? throw new IngestException(
                "Apple knows that show but does not publish an RSS URL for it. "
                + "Find the feed on the show's own site and paste that instead.");

        log.LogInformation("Resolved Apple Podcasts id {Id} to {FeedUrl}", id, feedUrl);
        return feedUrl;
    }

    /// <summary>Pulls <c>feedUrl</c> out of the lookup response, tolerating a shape that changes.</summary>
    internal static string? ReadFeedUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("feedUrl", out var feedUrl)
                    && feedUrl.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
