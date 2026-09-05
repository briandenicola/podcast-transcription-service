using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Chunking;

namespace PodcastTranscription.Tests;

/// <summary>Stands in for ffmpeg: reports a duration and silences, and writes stub chunk files.</summary>
public class FakeAudioProcessor(double durationSeconds, List<SilenceInterval>? silences = null)
    : AudioProcessor(Options.Create(new MediaToolOptions()), NullLogger<AudioProcessor>.Instance)
{
    public List<(int StartMs, int EndMs)> ExtractedChunks { get; } = [];

    public override Task<double?> ProbeDurationAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<double?>(durationSeconds);

    public override Task<List<SilenceInterval>> DetectSilenceAsync(
        string path, int noiseDb, double minDurationSeconds, CancellationToken ct = default) =>
        Task.FromResult(silences ?? []);

    public override Task<string> PrepareWavAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, new byte[64]);
        return Task.FromResult(destinationPath);
    }

    public override Task ExtractChunkAsync(
        string sourceWavPath, string destinationPath, int startMs, int endMs, CancellationToken ct = default)
    {
        ExtractedChunks.Add((startMs, endMs));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, new byte[64]);
        return Task.CompletedTask;
    }
}
