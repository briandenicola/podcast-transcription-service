using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Web.Endpoints;

public static class MediaEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapMediaEndpoints(this WebApplication app)
    {
        // Range processing is what makes seeking work: without it the browser has to download
        // the whole episode before it can jump to a timestamp.
        app.MapGet("/media/episodes/{id:int}/audio", async (int id, AppDbContext db, MediaStore media) =>
        {
            var episode = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
            if (episode is null)
            {
                return Results.NotFound();
            }

            // Prefer the source: it is smaller than the 16 kHz WAV and already in a format
            // browsers play. The prepared WAV is the fallback once retention drops the source.
            var relative = media.Exists(episode.AudioPath) ? episode.AudioPath
                : media.Exists(episode.PreparedAudioPath) ? episode.PreparedAudioPath!
                : null;

            if (relative is null)
            {
                return Results.NotFound();
            }

            var path = media.Resolve(relative);
            if (!ContentTypes.TryGetContentType(path, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(path, contentType, enableRangeProcessing: true);
        });
    }
}
