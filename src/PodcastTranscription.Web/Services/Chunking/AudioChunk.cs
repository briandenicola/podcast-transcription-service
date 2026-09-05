namespace PodcastTranscription.Web.Services.Chunking;

/// <summary>
/// One slice of the prepared WAV. <see cref="StartMs"/> is what every timestamp coming back
/// from whisper for this chunk has to be offset by.
/// </summary>
public record AudioChunk(int Index, int StartMs, int EndMs)
{
    public int DurationMs => EndMs - StartMs;
}
