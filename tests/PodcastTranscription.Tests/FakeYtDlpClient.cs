using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Tests;

/// <summary>Stands in for yt-dlp: writes a stub file and reports the metadata it was given.</summary>
public class FakeYtDlpClient(byte[]? content = null, string? title = null, double? durationSec = null)
    : YtDlpClient(Options.Create(new MediaToolOptions()), Options.Create(new IngestOptions()),
        NullLogger<YtDlpClient>.Instance)
{
    public List<string> RequestedUrls { get; } = [];

    public override Task<DownloadResult> DownloadAudioAsync(
        string url, string destinationDirectory, CancellationToken ct = default)
    {
        RequestedUrls.Add(url);

        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, "downloaded.mp3");
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4]);

        return Task.FromResult(new DownloadResult(path, title, null, durationSec));
    }
}
