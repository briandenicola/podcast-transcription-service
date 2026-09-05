namespace PodcastTranscription.Web.Services.Chunking;

/// <summary>A stretch of silence found by ffmpeg's <c>silencedetect</c> filter.</summary>
public record SilenceInterval(int StartMs, int EndMs)
{
    /// <summary>The point to cut on: the middle of the pause, furthest from speech on either side.</summary>
    public int MidpointMs => StartMs + (EndMs - StartMs) / 2;
}
