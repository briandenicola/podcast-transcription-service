using System.Text;

namespace PodcastTranscription.Web.Services;

/// <summary>A display segment rebuilt from words, with the words it was built from.</summary>
public record GroupedSegment(int StartMs, int EndMs, string Text, double? AvgLogProb, IReadOnlyList<TranscribedWord> Words);

/// <summary>
/// Rebuilds readable segments out of the one-word-per-segment stream that
/// <c>max_len=1 &amp; split_on_word=true</c> produces. Asking for word timing costs the natural
/// sentence grouping, so it has to be put back: a new segment starts on terminal punctuation,
/// on a pause, or once a segment has run long enough to be unwieldy.
/// </summary>
public static class WordGrouper
{
    private const string TerminalPunctuation = ".?!…";

    /// <summary>Trailing characters that can sit after the punctuation that ends a sentence.</summary>
    private const string ClosingCharacters = "\"'”’)]}»";

    public static List<GroupedSegment> Group(IReadOnlyList<TranscribedWord> words, int maxGapMs, int maxChars)
    {
        var segments = new List<GroupedSegment>();
        if (words.Count == 0)
        {
            return segments;
        }

        var current = new List<TranscribedWord>();

        foreach (var word in words)
        {
            // A pause before this word closes the previous segment, so the gap falls between
            // segments rather than inside one.
            if (current.Count > 0 && word.StartMs - current[^1].EndMs > maxGapMs)
            {
                segments.Add(Build(current));
                current = [];
            }

            current.Add(word);

            var endsSentence = EndsSentence(word.Text);
            var longEnough = MeasureLength(current) >= maxChars;

            if (endsSentence || longEnough)
            {
                segments.Add(Build(current));
                current = [];
            }
        }

        if (current.Count > 0)
        {
            segments.Add(Build(current));
        }

        return segments;
    }

    /// <summary>
    /// True when the word ends in terminal punctuation, ignoring any closing quote or bracket
    /// after it. Abbreviations like "Mr." split a sentence early; the pause rule usually keeps
    /// the damage to one short segment, and it is not worth a dictionary to avoid.
    /// </summary>
    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd().TrimEnd(ClosingCharacters.ToCharArray());
        return trimmed.Length > 0 && TerminalPunctuation.Contains(trimmed[^1]);
    }

    private static int MeasureLength(List<TranscribedWord> words) =>
        words.Sum(w => w.Text.Trim().Length + 1);

    private static GroupedSegment Build(List<TranscribedWord> words)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            var piece = word.Text.Trim();
            if (piece.Length == 0)
            {
                continue;
            }

            // Punctuation that arrives as its own "word" must not get a space in front of it.
            if (text.Length > 0 && !IsPunctuationOnly(piece))
            {
                text.Append(' ');
            }

            text.Append(piece);
        }

        var logProbs = words.Where(w => w.AvgLogProb.HasValue).Select(w => w.AvgLogProb!.Value).ToList();

        return new GroupedSegment(
            words[0].StartMs,
            words[^1].EndMs,
            text.ToString(),
            logProbs.Count > 0 ? logProbs.Average() : null,
            words);
    }

    private static bool IsPunctuationOnly(string text) =>
        text.All(c => char.IsPunctuation(c) || char.IsSymbol(c));
}
