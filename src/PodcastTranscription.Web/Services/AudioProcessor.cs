using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services.Chunking;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Everything that shells out to ffmpeg. Commands run with <c>-nostdin</c> and both output
/// streams are always drained, or the process deadlocks on a full pipe.
///
/// The methods that invoke ffmpeg are virtual so tests can exercise the pipeline without one
/// installed.
/// </summary>
public class AudioProcessor(IOptions<MediaToolOptions> tools, ILogger<AudioProcessor> log)
{
    private readonly MediaToolOptions _tools = tools.Value;

    /// <summary>The only sample rate whisper.cpp accepts without resampling internally.</summary>
    public const int TargetSampleRate = 16_000;

    /// <summary>Duration of the file in seconds, via ffprobe. Null when ffprobe cannot determine it.</summary>
    public virtual async Task<double?> ProbeDurationAsync(string path, CancellationToken ct = default)
    {
        // Duration is nice to have, not required: an import still succeeds without it.
        (int ExitCode, string Stdout, string Stderr) result;
        try
        {
            result = await RunAsync(
                _tools.FfprobePath,
                [
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path
                ],
                ct);
        }
        catch (AudioProcessingException ex)
        {
            log.LogWarning(ex, "Could not probe {Path}", Path.GetFileName(path));
            return null;
        }

        if (result.ExitCode != 0)
        {
            log.LogWarning("ffprobe failed on {Path} (exit {ExitCode}): {Error}",
                path, result.ExitCode, result.Stderr.Trim());
            return null;
        }

        return double.TryParse(result.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    /// <summary>
    /// Transcodes to 16 kHz mono signed 16-bit WAV — the format whisper-server reliably accepts.
    /// Returns the output path.
    /// </summary>
    public virtual async Task<string> PrepareWavAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
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

    /// <summary>
    /// Finds the pauses in a file with ffmpeg's <c>silencedetect</c> filter. Decodes the whole
    /// file without writing anything, so it costs one pass over the audio.
    /// </summary>
    public virtual async Task<List<SilenceInterval>> DetectSilenceAsync(
        string path, int noiseDb, double minDurationSeconds, CancellationToken ct = default)
    {
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"silencedetect=noise={noiseDb}dB:d={minDurationSeconds}");

        var (exitCode, _, stderr) = await RunAsync(
            _tools.FfmpegPath,
            [
                "-nostdin",
                "-hide_banner",
                "-i", path,
                "-af", filter,
                "-f", "null",
                "-"
            ],
            ct);

        if (exitCode != 0)
        {
            // A failed scan is not fatal: without silences the planner cuts on the clock instead.
            log.LogWarning("silencedetect failed on {Path} (exit {ExitCode}); chunks will be cut on time alone",
                Path.GetFileName(path), exitCode);
            return [];
        }

        var intervals = SilenceParser.Parse(stderr);
        log.LogInformation("Found {Count} silences in {Path}", intervals.Count, Path.GetFileName(path));
        return intervals;
    }

    /// <summary>
    /// Cuts one chunk out of a prepared WAV. Chunks are extracted just before they are posted
    /// and deleted straight after, so a stalled job leaves no pile of intermediate audio behind.
    /// </summary>
    public virtual async Task ExtractChunkAsync(
        string sourceWavPath, string destinationPath, int startMs, int endMs, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var start = (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var duration = ((endMs - startMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

        var (exitCode, _, stderr) = await RunAsync(
            _tools.FfmpegPath,
            [
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                // Seeking before -i is exact on PCM WAV and avoids decoding the skipped audio.
                "-ss", start,
                "-t", duration,
                "-i", sourceWavPath,
                "-ac", "1",
                "-ar", TargetSampleRate.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_s16le",
                destinationPath
            ],
            ct);

        if (exitCode != 0)
        {
            throw new AudioProcessingException(
                $"ffmpeg failed to cut {startMs}-{endMs}ms from '{Path.GetFileName(sourceWavPath)}' (exit {exitCode}): {stderr.Trim()}");
        }
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

        try
        {
            var result = await Cli.Wrap(executable)
                .WithArguments(arguments)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);

            return (result.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Win32Exception ex)
        {
            // The container bakes these in; locally they are the thing most likely missing.
            throw new AudioProcessingException(
                $"Could not run '{executable}'. Is it installed and on PATH? ({ex.Message})");
        }
    }
}

public class AudioProcessingException(string message) : Exception(message);
