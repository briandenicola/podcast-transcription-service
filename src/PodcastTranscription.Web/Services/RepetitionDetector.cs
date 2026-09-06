using System.Text;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Finds runs of identical consecutive segments — the signature of a whisper repetition loop,
/// where the decoder latches onto a phrase and emits it until the chunk runs out.
///
/// The output is still returned and stored: it may be genuinely repetitive audio, and throwing
/// away a transcript on a heuristic would be worse than flagging it. The point is that a loop
/// should be visible rather than sitting in the archive looking like a real transcript.
/// </summary>
public static class RepetitionDetector
{
    /// <summary>Below this, a repeated line is more likely a real refrain than a decoder fault.</summary>
    public const int DefaultThreshold = 5;

    public record Finding(int RepeatCount, int StartMs, string Text);

    /// <summary>The longest run of identical consecutive segments, if it reaches the threshold.</summary>
    public static Finding? Detect(IReadOnlyList<Segment> segments, int threshold = DefaultThreshold)
    {
        if (segments.Count < threshold)
        {
            return null;
        }

        var ordered = segments.OrderBy(s => s.Ordinal).ToList();

        Finding? worst = null;
        var runStart = 0;

        for (var i = 1; i <= ordered.Count; i++)
        {
            var sameAsPrevious = i < ordered.Count
                && Normalize(ordered[i].Text) == Normalize(ordered[i - 1].Text)
                && Normalize(ordered[i].Text).Length > 0;

            if (sameAsPrevious)
            {
                continue;
            }

            var runLength = i - runStart;
            if (runLength >= threshold && (worst is null || runLength > worst.RepeatCount))
            {
                worst = new Finding(runLength, ordered[runStart].StartMs, ordered[runStart].Text);
            }

            runStart = i;
        }

        return worst;
    }

    /// <summary>A short sentence describing the finding, for the UI and the log.</summary>
    public static string Describe(Finding finding) =>
        $"The same line repeats {finding.RepeatCount} times from {TimeFormat.Clock(finding.StartMs)} "
        + "— whisper most likely got stuck rather than the audio repeating. "
        + "Re-transcribing, often on a different model, usually clears it.";

    /// <summary>
    /// Case and punctuation are ignored: a loop sometimes varies the trailing punctuation while
    /// repeating the same words.
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsWhiteSpace(c) && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }
}
