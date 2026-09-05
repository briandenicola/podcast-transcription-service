using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Resolves paths under the media root. Everything stored on an entity is relative to that
/// root so the volume can be remounted anywhere without rewriting the database.
/// </summary>
public class MediaStore
{
    private readonly StorageOptions _options;

    public MediaStore(IOptions<StorageOptions> options, IHostEnvironment env)
    {
        _options = options.Value;
        Root = Path.IsPathRooted(_options.MediaPath)
            ? _options.MediaPath
            : Path.Combine(env.ContentRootPath, _options.MediaPath);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public long MaxUploadBytes => (long)_options.MaxUploadMb * 1024 * 1024;

    public string Resolve(string relativePath) => Path.Combine(Root, relativePath);

    public bool Exists(string? relativePath) =>
        !string.IsNullOrWhiteSpace(relativePath) && File.Exists(Resolve(relativePath));

    /// <summary>
    /// Builds a collision-free relative path for freshly ingested source audio, keeping the
    /// original extension so ffmpeg can sniff the format.
    /// </summary>
    public string NewSourcePath(string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".bin";
        }

        var stem = Sanitize(Path.GetFileNameWithoutExtension(originalFileName));
        var relative = Path.Combine("source", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{stem}{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(Resolve(relative))!);
        return relative;
    }

    /// <summary>Path for the prepared 16 kHz WAV belonging to an episode.</summary>
    public string PreparedPath(int episodeId)
    {
        var relative = Path.Combine("prepared", $"episode-{episodeId}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(Resolve(relative))!);
        return relative;
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray())
            .Trim('-');

        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60];
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "audio" : cleaned;
    }
}
