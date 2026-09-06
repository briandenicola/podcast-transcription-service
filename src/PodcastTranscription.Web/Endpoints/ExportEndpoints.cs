using System.Text;
using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Services.Export;

namespace PodcastTranscription.Web.Endpoints;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapGet("/episodes/{episodeId:int}/transcripts/{transcriptId:int}/export/{format}",
            async (int episodeId, int transcriptId, string format, AppDbContext db) =>
        {
            if (!TranscriptExporter.TryParseFormat(format, out var exportFormat))
            {
                return Results.BadRequest($"Unknown export format '{format}'. Try srt, vtt, txt, json or md.");
            }

            var episode = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == episodeId);
            if (episode is null)
            {
                return Results.NotFound();
            }

            var transcript = await db.Transcripts
                .AsNoTracking()
                .Include(t => t.Segments)
                .FirstOrDefaultAsync(t => t.Id == transcriptId && t.EpisodeId == episodeId);

            if (transcript is null)
            {
                return Results.NotFound();
            }

            var body = TranscriptExporter.Render(exportFormat, episode, transcript);
            var fileName = $"{Slug(episode.Title)}.{TranscriptExporter.Extension(exportFormat)}";

            return Results.File(
                Encoding.UTF8.GetBytes(body),
                TranscriptExporter.ContentType(exportFormat),
                fileName);
        });
    }

    /// <summary>A filename-safe version of the episode title.</summary>
    private static string Slug(string title)
    {
        var cleaned = new string(title
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray());

        while (cleaned.Contains("--"))
        {
            cleaned = cleaned.Replace("--", "-");
        }

        cleaned = cleaned.Trim('-');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60].TrimEnd('-');
        }

        return cleaned.Length == 0 ? "transcript" : cleaned;
    }
}
