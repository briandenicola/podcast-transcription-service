namespace PodcastTranscription.Web.Services.Search;

/// <summary>One matching segment, with the snippet FTS5 built around the match.</summary>
public record SearchHit
{
    public int SegmentId { get; init; }
    public int TranscriptId { get; init; }
    public int EpisodeId { get; init; }
    public string EpisodeTitle { get; init; } = string.Empty;
    public string? Show { get; init; }
    public int StartMs { get; init; }
    public int EndMs { get; init; }

    /// <summary>
    /// The snippet with matches wrapped in the sentinel characters FTS5 was given. Never render
    /// this directly — see <see cref="SearchHighlighter"/>.
    /// </summary>
    public string Snippet { get; init; } = string.Empty;

    /// <summary>bm25 score. Lower is a better match, which is what FTS5's own ordering uses.</summary>
    public double Rank { get; init; }
}

/// <summary>Hits from one episode, best match first.</summary>
public record EpisodeHits(int EpisodeId, string Title, string? Show, IReadOnlyList<SearchHit> Hits);

public record SearchResults(string Query, int TotalHits, IReadOnlyList<EpisodeHits> Episodes)
{
    public static SearchResults Empty(string query) => new(query, 0, []);
}
