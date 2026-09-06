using System.Text;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// Turns a transcript into the windows of text a model is actually asked about.
///
/// A 90-minute episode is well over 100 000 characters. Ollama's context window defaults to
/// 2048 tokens and is rarely raised past 8192, so a whole episode posted in one prompt does not
/// fail — it gets silently truncated, and the answer looks like a model that ignored the second
/// half. Cutting it up first is what makes the summary reflect the whole episode.
/// </summary>
public static class TranscriptWindower
{
    /// <summary>
    /// One line per segment, timestamped, which is what lets the summary cite <c>[hh:mm:ss]</c>
    /// and a reader jump to it.
    /// </summary>
    public static List<string> ToLines(IEnumerable<Segment> segments) =>
        segments
            .OrderBy(s => s.Ordinal)
            .Select(s => (Time: TimeFormat.Timestamp(s.StartMs), Text: s.Text.Trim()))
            .Where(s => s.Text.Length > 0)
            .Select(s => $"[{s.Time}] {s.Text}")
            .ToList();

    /// <summary>
    /// Packs lines into windows of at most <paramref name="maxChars"/>, never splitting a line.
    ///
    /// A line longer than the budget on its own still gets its own window rather than being
    /// dropped or cut: a single segment that long means something went wrong upstream, and
    /// losing it silently would hide that.
    /// </summary>
    public static List<string> Split(IReadOnlyList<string> lines, int maxChars)
    {
        var budget = Math.Max(500, maxChars);
        var windows = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            // +1 for the newline this line would bring with it.
            if (current.Length > 0 && current.Length + line.Length + 1 > budget)
            {
                windows.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            windows.Add(current.ToString());
        }

        return windows;
    }

    /// <summary>
    /// Groups already-written notes into batches small enough to fold in one pass. Used only
    /// when there are so many windows that their notes will not themselves fit a single prompt.
    ///
    /// A batch is never closed at one note while others remain: folding one set of notes on its
    /// own merges nothing, so a round of those would leave just as many notes as it started with
    /// and the caller would fold forever. Holding to a minimum of two means every round is at
    /// worst a halving, which is what makes the loop terminate — at the cost of some batches
    /// running over budget, which the model handles far better than never converging.
    /// </summary>
    public static List<List<string>> Batch(IReadOnlyList<string> notes, int maxChars)
    {
        var budget = Math.Max(500, maxChars);
        var batches = new List<List<string>>();
        var current = new List<string>();
        var length = 0;

        foreach (var note in notes)
        {
            if (current.Count >= 2 && length + note.Length > budget)
            {
                batches.Add(current);
                current = [];
                length = 0;
            }

            current.Add(note);
            length += note.Length;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }
}
