using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PodcastTranscription.Web.Services.Ingest;

/// <summary>One item from a podcast feed, reduced to what ingest needs.</summary>
public record FeedItem
{
    /// <summary>The feed's &lt;guid&gt;, falling back to the enclosure URL when absent.</summary>
    public string Guid { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>The enclosure URL — the audio itself.</summary>
    public string AudioUrl { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; init; }
    public double? DurationSec { get; init; }
}

public record ParsedFeed(string Title, IReadOnlyList<FeedItem> Items);

/// <summary>
/// Reads podcast RSS. Parsed with XDocument rather than a syndication library because podcast
/// feeds are RSS 2.0 with a couple of iTunes extensions, and the handful of elements that matter
/// are easier to read — and to be strict about — directly.
/// </summary>
public static class FeedParser
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    /// <summary>
    /// Feeds are fetched from wherever a user pointed us, so the XML is untrusted: DTD processing
    /// is prohibited and no external resolver is supplied, which closes off entity expansion and
    /// external entity attacks.
    /// </summary>
    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    public static ParsedFeed Parse(string xml)
    {
        XDocument document;
        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, SafeSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new IngestException($"That does not parse as XML: {ex.Message}");
        }

        var channel = document.Root?.Element("channel")
            ?? throw new IngestException("No <channel> element — is this an RSS feed?");

        var items = new List<FeedItem>();

        foreach (var element in channel.Elements("item"))
        {
            // Without an enclosure there is no audio, so there is nothing to transcribe.
            var audioUrl = element.Element("enclosure")?.Attribute("url")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                continue;
            }

            var guid = element.Element("guid")?.Value?.Trim();

            items.Add(new FeedItem
            {
                Guid = string.IsNullOrWhiteSpace(guid) ? audioUrl : guid,
                Title = element.Element("title")?.Value?.Trim() is { Length: > 0 } title ? title : "Untitled episode",
                AudioUrl = audioUrl,
                PublishedAt = ParseDate(element.Element("pubDate")?.Value),
                DurationSec = ParseDuration(element.Element(Itunes + "duration")?.Value)
            });
        }

        var feedTitle = channel.Element("title")?.Value?.Trim();

        return new ParsedFeed(
            string.IsNullOrWhiteSpace(feedTitle) ? "Untitled feed" : feedTitle,
            items);
    }

    /// <summary>RFC 822 in theory; in practice feeds are loose, so fall back to a general parse.</summary>
    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        return DateTimeOffset.TryParseExact(value, [
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            "ddd, dd MMM yyyy HH:mm zzz",
            "dd MMM yyyy HH:mm:ss zzz"
        ], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact)
            ? exact
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose)
                ? loose
                : null;
    }

    /// <summary>itunes:duration is seconds, mm:ss, or hh:mm:ss depending on the publisher.</summary>
    internal static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split(':');

        if (parts.Length == 1)
        {
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var component))
            {
                return null;
            }

            total = (total * 60) + component;
        }

        return total;
    }
}
