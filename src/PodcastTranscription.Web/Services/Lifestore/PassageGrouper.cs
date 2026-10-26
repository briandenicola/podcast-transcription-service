namespace PodcastTranscription.Web.Services.Lifestore;

/// <summary>A run of consecutive segments, long enough to be worth finding in a search.</summary>
public record Passage(int StartMs, int EndMs, string Text);

/// <summary>
/// Joins transcript segments into passages for the life store feed. A segment is a sentence or
/// two, too short to search on its own; a whole transcript is too long to rank. Passages break
/// only between segments, so a timestamp still points at the start of what was said. Pure.
/// </summary>
public static class PassageGrouper
{
    public const int TargetChars = 2000;

    public static List<Passage> Group(IEnumerable<(int StartMs, int EndMs, string Text)> segments, int targetChars = TargetChars)
    {
        var passages = new List<Passage>();
        var text = new System.Text.StringBuilder();
        int start = 0, end = 0;

        foreach (var (startMs, endMs, segmentText) in segments)
        {
            var line = segmentText.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (text.Length == 0)
            {
                start = startMs;
            }
            else
            {
                text.Append(' ');
            }

            text.Append(line);
            end = endMs;

            if (text.Length >= targetChars)
            {
                passages.Add(new Passage(start, end, text.ToString()));
                text.Clear();
            }
        }

        if (text.Length > 0)
        {
            passages.Add(new Passage(start, end, text.ToString()));
        }

        return passages;
    }
}
