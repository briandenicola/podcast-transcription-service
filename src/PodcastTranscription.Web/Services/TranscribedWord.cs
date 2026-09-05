using System.Text.Json.Serialization;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// One word with its own timing. Serialized into <c>Segment.WordsJson</c> as
/// <c>[{ "t0": ms, "t1": ms, "text": "…", "p": 0.0-1.0 }]</c> — a single read alongside the
/// text, rather than 13,000 rows per episode that would have to be indexed, joined and paged.
/// </summary>
public record TranscribedWord
{
    [JsonPropertyName("t0")] public int StartMs { get; init; }
    [JsonPropertyName("t1")] public int EndMs { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Confidence, 0 to 1. whisper-server reports <c>avg_logprob</c>; with one word per segment
    /// that average is over a single word, so exponentiating it gives that word's probability.
    /// Drives the low-confidence shading that finds mangled proper nouns (M3.5b).
    /// </summary>
    [JsonPropertyName("p")] public double? Probability { get; init; }

    [JsonIgnore] public double? AvgLogProb { get; init; }
}
