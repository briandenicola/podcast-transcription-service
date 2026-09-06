namespace PodcastTranscription.Web.Domain;

public enum JobState
{
    Queued = 0,
    Downloading = 1,
    Preparing = 2,
    Transcribing = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,

    /// <summary>
    /// The transcript is finished and stored; a local model is writing its summary. Appended
    /// rather than slotted in after Transcribing because the value is what is on disk — renumbering
    /// would silently relabel every job already in the database.
    /// </summary>
    Summarizing = 7
}
