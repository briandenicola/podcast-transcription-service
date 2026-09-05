using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services.Chunking;

/// <summary>
/// Decides where to cut an episode. Pure: given a duration and the silences found in it, the
/// same plan comes out every time, which is what lets a resumed job pick up mid-episode
/// against boundaries identical to the ones the first attempt used.
/// </summary>
public static class ChunkPlanner
{
    public static List<AudioChunk> Plan(int totalMs, IReadOnlyList<SilenceInterval> silences, TranscriptionOptions options)
    {
        var chunks = new List<AudioChunk>();
        if (totalMs <= 0)
        {
            return chunks;
        }

        var targetMs = Math.Max(1000, options.ChunkSeconds * 1000);
        var windowMs = Math.Max(0, options.SilenceSearchWindowSeconds * 1000);
        var minTailMs = (int)(targetMs * Math.Clamp(options.MinTailFraction, 0, 1));

        var position = 0;
        var index = 0;

        while (position < totalMs)
        {
            var target = position + targetMs;

            // Everything left fits in one chunk, or the remainder is too short to be worth
            // splitting off as a stub.
            if (target >= totalMs || totalMs - target < minTailMs)
            {
                chunks.Add(new AudioChunk(index, position, totalMs));
                break;
            }

            var boundary = FindCutPoint(target, windowMs, position, silences) ?? target;
            chunks.Add(new AudioChunk(index++, position, boundary));
            position = boundary;
        }

        return chunks;
    }

    /// <summary>
    /// The midpoint of the silence closest to the target, within the search window, that still
    /// leaves a non-empty chunk. Null when the audio is unbroken there and the cut has to land
    /// mid-speech.
    /// </summary>
    private static int? FindCutPoint(int targetMs, int windowMs, int positionMs, IReadOnlyList<SilenceInterval> silences)
    {
        int? best = null;
        var bestDistance = int.MaxValue;

        foreach (var silence in silences)
        {
            var candidate = silence.MidpointMs;
            if (candidate <= positionMs)
            {
                continue;
            }

            var distance = Math.Abs(candidate - targetMs);
            if (distance <= windowMs && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }
}
