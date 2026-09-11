namespace PodcastTranscription.Web.Domain;

/// <summary>
/// What a job does. Transcription and manual summarisation share the same table, the same
/// cancel/retry/delete endpoints and the same Jobs page, but are claimed by different workers
/// with different concurrency limits — this is what tells them apart.
/// </summary>
public enum JobKind
{
    Transcription = 0,
    Summarization = 1
}
