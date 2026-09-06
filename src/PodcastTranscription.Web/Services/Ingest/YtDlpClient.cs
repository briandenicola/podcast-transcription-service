using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CliWrap;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services.Ingest;

/// <summary>What yt-dlp reported about a download.</summary>
public record DownloadResult(string FilePath, string? Title, DateTimeOffset? PublishedAt, double? DurationSec);

/// <summary>
/// Downloads episode audio with yt-dlp, which handles both a plain enclosure URL and the video
/// sites a show might also publish to.
///
/// The download method is virtual so tests can exercise the ingest path without the binary.
/// </summary>
public class YtDlpClient(
    IOptions<MediaToolOptions> tools,
    IOptions<IngestOptions> ingest,
    ILogger<YtDlpClient> log)
{
    private readonly MediaToolOptions _tools = tools.Value;
    private readonly IngestOptions _ingest = ingest.Value;

    /// <summary>
    /// Rejects anything that is not plain http(s) before it reaches the subprocess. Arguments are
    /// passed as an array rather than through a shell, so there is no command injection to worry
    /// about, but yt-dlp itself understands schemes like <c>file:</c> that would let a URL read
    /// the server's own disk.
    /// </summary>
    public static bool IsSupportedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    public virtual async Task<DownloadResult> DownloadAudioAsync(string url, string destinationDirectory, CancellationToken ct = default)
    {
        if (!IsSupportedUrl(url))
        {
            throw new IngestException($"'{url}' is not an http or https URL.");
        }

        Directory.CreateDirectory(destinationDirectory);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(_ingest.DownloadTimeoutMinutes));

        try
        {
            var result = await Cli.Wrap(_tools.YtDlpPath)
                .WithArguments(
                [
                    "--no-playlist",
                    "--no-progress",
                    "--no-warnings",
                    // Audio only: the video track is dead weight for a transcript.
                    "-f", "bestaudio/best",
                    "-o", Path.Combine(destinationDirectory, "%(id)s.%(ext)s"),
                    // Emit the metadata alongside the download rather than fetching it twice.
                    "--print-json",
                    "--no-simulate",
                    url
                ])
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(timeout.Token);

            if (result.ExitCode != 0)
            {
                throw new IngestException($"yt-dlp failed (exit {result.ExitCode}): {Tail(stderr.ToString())}");
            }
        }
        catch (Win32Exception ex)
        {
            throw new IngestException($"Could not run '{_tools.YtDlpPath}'. Is it installed and on PATH? ({ex.Message})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IngestException($"yt-dlp timed out after {_ingest.DownloadTimeoutMinutes} minutes.");
        }

        var parsed = ParseMetadata(stdout.ToString());
        if (parsed is null)
        {
            throw new IngestException("yt-dlp reported success but did not say where it put the file.");
        }

        log.LogInformation("Downloaded {Title} to {Path}", parsed.Title ?? "(untitled)", parsed.FilePath);
        return parsed;
    }

    /// <summary>
    /// Reads the JSON yt-dlp prints per download. Fields vary a lot by extractor, so every one
    /// beyond the path is treated as optional.
    /// </summary>
    internal static DownloadResult? ParseMetadata(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            var path = ReadFilePath(root);
            if (path is null)
            {
                continue;
            }

            return new DownloadResult(
                path,
                root.TryGetProperty("title", out var title) ? title.GetString() : null,
                ReadUploadDate(root),
                root.TryGetProperty("duration", out var duration) && duration.ValueKind is JsonValueKind.Number
                    ? duration.GetDouble()
                    : null);
        }

        return null;
    }

    private static string? ReadFilePath(JsonElement root)
    {
        // Newer yt-dlp reports the final path here, after any post-processing.
        if (root.TryGetProperty("requested_downloads", out var downloads)
            && downloads.ValueKind == JsonValueKind.Array
            && downloads.GetArrayLength() > 0
            && downloads[0].TryGetProperty("filepath", out var filepath)
            && filepath.GetString() is { Length: > 0 } fromDownloads)
        {
            return fromDownloads;
        }

        // Older versions only set _filename.
        return root.TryGetProperty("_filename", out var filename) ? filename.GetString() : null;
    }

    private static DateTimeOffset? ReadUploadDate(JsonElement root)
    {
        if (!root.TryGetProperty("upload_date", out var uploadDate) || uploadDate.GetString() is not { } value)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(value, "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "…" + trimmed[^400..];
    }
}

public class IngestException(string message) : Exception(message);
