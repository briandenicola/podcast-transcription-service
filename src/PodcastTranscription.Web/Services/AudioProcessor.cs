using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Everything that shells out to ffmpeg. Commands run with <c>-nostdin</c> and both output
/// streams are always drained, or the process deadlocks on a full pipe.
/// </summary>
public class AudioProcessor(IOptions<MediaToolOptions> tools, ILogger<AudioProcessor> log)
{
    private readonly MediaToolOptions _tools = tools.Value;

    /// <summary>The only sample rate whisper.cpp accepts without resampling internally.</summary>
    public const int TargetSampleRate = 16_000;

    /// <summary>Duration of the file in seconds, via ffprobe. Null when ffprobe cannot determine it.</summary>
    public async Task<double?> ProbeDurationAsync(string path, CancellationToken ct = default)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            _tools.FfprobePath,
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path
            ],
            ct);

        if (exitCode != 0)
        {
            log.LogWarning("ffprobe failed on {Path} (exit {ExitCode}): {Error}", path, exitCode, stderr.Trim());
            return null;
        }

        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    /// <summary>
    /// Transcodes to 16 kHz mono signed 16-bit WAV — the format whisper-server reliably accepts.
    /// Returns the output path.
    /// </summary>
    public async Task<string> PrepareWavAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var (exitCode, _, stderr) = await RunAsync(
            _tools.FfmpegPath,
            [
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", sourcePath,
                "-vn",
                "-ac", "1",
                "-ar", TargetSampleRate.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_s16le",
                destinationPath
            ],
            ct);

        if (exitCode != 0)
        {
            throw new AudioProcessingException(
                $"ffmpeg failed to prepare '{Path.GetFileName(sourcePath)}' (exit {exitCode}): {stderr.Trim()}");
        }

        log.LogInformation("Prepared {Destination} at {Rate} Hz mono", Path.GetFileName(destinationPath), TargetSampleRate);
        return destinationPath;
    }

    /// <summary>SHA-256 of a file, lowercase hex. Catches re-uploads of the same audio under a new name.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var result = await Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        return (result.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

public class AudioProcessingException(string message) : Exception(message);
