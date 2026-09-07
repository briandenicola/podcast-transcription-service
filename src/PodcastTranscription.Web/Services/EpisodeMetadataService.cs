using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;

namespace PodcastTranscription.Web.Services;

public sealed record EpisodeMetadataResult(bool Updated, bool NotFound, string? Error)
{
    public static EpisodeMetadataResult Saved() => new(true, false, null);
    public static EpisodeMetadataResult Missing() => new(false, true, null);
    public static EpisodeMetadataResult Invalid(string error) => new(false, false, error);
}

/// <summary>Validates and updates the reader-facing identity of an episode.</summary>
public class EpisodeMetadataService(AppDbContext db, ILogger<EpisodeMetadataService> log)
{
    public async Task<EpisodeMetadataResult> UpdateAsync(
        int episodeId, string? title, string? show, CancellationToken ct = default)
    {
        var normalizedTitle = title?.Trim() ?? string.Empty;
        var normalizedShow = string.IsNullOrWhiteSpace(show) ? null : show.Trim();

        if (normalizedTitle.Length == 0)
        {
            return EpisodeMetadataResult.Invalid("Give the episode a title.");
        }

        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null)
        {
            return EpisodeMetadataResult.Missing();
        }

        episode.Title = normalizedTitle;
        episode.Show = normalizedShow;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Updated title and show for episode {EpisodeId}", episodeId);
        return EpisodeMetadataResult.Saved();
    }
}
