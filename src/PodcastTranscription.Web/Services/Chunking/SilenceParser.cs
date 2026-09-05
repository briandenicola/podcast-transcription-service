using System.Globalization;
using System.Text.RegularExpressions;

namespace PodcastTranscription.Web.Services.Chunking;

/// <summary>
/// Reads the stderr of <c>ffmpeg -af silencedetect</c>, which reports pauses as pairs of lines:
/// <code>
/// [silencedetect @ 0x…] silence_start: 12.345
/// [silencedetect @ 0x…] silence_end: 13.5 | silence_duration: 1.155
/// </code>
/// </summary>
public static partial class SilenceParser
{
    [GeneratedRegex(@"silence_start:\s*(-?[0-9]*\.?[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StartPattern();

    [GeneratedRegex(@"silence_end:\s*(-?[0-9]*\.?[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EndPattern();

    public static List<SilenceInterval> Parse(string ffmpegStderr)
    {
        var intervals = new List<SilenceInterval>();
        int? pendingStart = null;

        foreach (var line in ffmpegStderr.Split('\n'))
        {
            var start = StartPattern().Match(line);
            if (start.Success && TryMilliseconds(start.Groups[1].Value, out var startMs))
            {
                // A second silence_start without an end means the first was never closed; the
                // later one is the one that matters.
                pendingStart = Math.Max(0, startMs);
            }

            var end = EndPattern().Match(line);
            if (end.Success && pendingStart is { } openStart && TryMilliseconds(end.Groups[1].Value, out var endMs))
            {
                if (endMs > openStart)
                {
                    intervals.Add(new SilenceInterval(openStart, endMs));
                }

                pendingStart = null;
            }
        }

        return intervals;
    }

    private static bool TryMilliseconds(string seconds, out int milliseconds)
    {
        if (double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            milliseconds = (int)Math.Round(value * 1000);
            return true;
        }

        milliseconds = 0;
        return false;
    }
}
