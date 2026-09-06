using Dapper;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;

namespace PodcastTranscription.Web.Services.Search;

/// <summary>
/// Full-text search over transcript segments. Queried with Dapper because EF has no model for an
/// FTS5 virtual table, and none of this is expressible in LINQ: the MATCH operator, bm25 ranking
/// and snippet() are all SQLite features.
/// </summary>
public class SearchService(AppDbContext db, ILogger<SearchService> log)
{
    /// <summary>Segments matching the query, grouped by episode, best match first.</summary>
    public async Task<SearchResults> SearchAsync(string? input, int limit = 100, CancellationToken ct = default)
    {
        var match = FtsQuery.Build(input);
        if (match is null)
        {
            return SearchResults.Empty(input ?? string.Empty);
        }

        var connection = db.Database.GetDbConnection();

        // char(1) and char(2) are the sentinels SearchHighlighter turns into <mark> tags, after
        // the surrounding text has been HTML-encoded.
        const string sql = """
            SELECT s.Id                AS SegmentId,
                   s.TranscriptId      AS TranscriptId,
                   s.StartMs           AS StartMs,
                   s.EndMs             AS EndMs,
                   snippet(SegmentSearch, 0, char(1), char(2), '…', 12) AS Snippet,
                   bm25(SegmentSearch) AS Rank,
                   t.EpisodeId         AS EpisodeId,
                   e.Title             AS EpisodeTitle,
                   e.Show              AS Show
            FROM SegmentSearch
            JOIN Segments    s ON s.Id = SegmentSearch.rowid
            JOIN Transcripts t ON t.Id = s.TranscriptId
            JOIN Episodes    e ON e.Id = t.EpisodeId
            WHERE SegmentSearch MATCH @match
            ORDER BY bm25(SegmentSearch)
            LIMIT @limit;
            """;

        try
        {
            var hits = (await connection.QueryAsync<SearchHit>(
                new CommandDefinition(sql, new { match, limit }, cancellationToken: ct))).ToList();

            // Grouped by episode, but episodes stay in best-hit order rather than alphabetical,
            // and hits within an episode run in playback order.
            var grouped = hits
                .GroupBy(h => h.EpisodeId)
                .Select(g => new EpisodeHits(g.Key, g.First().EpisodeTitle, g.First().Show,
                    g.OrderBy(h => h.StartMs).ToList()))
                .OrderBy(g => g.Hits.Min(h => h.Rank))
                .ToList();

            return new SearchResults(input!, hits.Count, grouped);
        }
        catch (Exception ex)
        {
            // A MATCH that SQLite rejects is bad user input, not a server fault.
            log.LogWarning(ex, "Search failed for {Query} (match expression {Match})", input, match);
            return SearchResults.Empty(input!);
        }
    }

    /// <summary>Distinct show names, for the library filter.</summary>
    public async Task<List<string>> GetShowsAsync(CancellationToken ct = default) =>
        await db.Episodes
            .Where(e => e.Show != null && e.Show != "")
            .Select(e => e.Show!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);
}
