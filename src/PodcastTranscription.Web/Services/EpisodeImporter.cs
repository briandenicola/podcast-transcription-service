using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

public record ImportResult(Episode Episode, bool WasDuplicate);

/// <summary>Turns an incoming audio stream into an <see cref="Episode"/> row plus a file on disk.</summary>
public class EpisodeImporter(
    AppDbContext db,
    MediaStore media,
    AudioProcessor audio,
    ILogger<EpisodeImporter> log)
{
    public async Task<ImportResult> ImportAsync(
        Stream content,
        string fileName,
        string? title = null,
        string? show = null,
        CancellationToken ct = default)
    {
        var relativePath = media.NewSourcePath(fileName);
        var absolutePath = media.Resolve(relativePath);

        await using (var destination = File.Create(absolutePath))
        {
            await content.CopyToAsync(destination, ct);
        }

        var sha = await AudioProcessor.ComputeSha256Async(absolutePath, ct);

        var existing = await db.Episodes.FirstOrDefaultAsync(e => e.AudioSha256 == sha, ct);
        if (existing is not null)
        {
            // Same audio, different name or a re-download. Keep the copy we already had.
            File.Delete(absolutePath);
            log.LogInformation("Upload {FileName} matched existing episode {EpisodeId} by hash", fileName, existing.Id);
            return new ImportResult(existing, WasDuplicate: true);
        }

        var episode = new Episode
        {
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title.Trim(),
            Show = string.IsNullOrWhiteSpace(show) ? null : show.Trim(),
            AudioPath = relativePath,
            AudioSha256 = sha,
            DurationSec = await audio.ProbeDurationAsync(absolutePath, ct)
        };

        db.Episodes.Add(episode);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Imported episode {EpisodeId} '{Title}' ({Duration:N0}s)",
            episode.Id, episode.Title, episode.DurationSec ?? 0);

        return new ImportResult(episode, WasDuplicate: false);
    }
}
