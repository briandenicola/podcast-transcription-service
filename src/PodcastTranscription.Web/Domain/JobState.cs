namespace PodcastTranscription.Web.Domain;

public enum JobState
{
    Queued = 0,
    Downloading = 1,
    Preparing = 2,
    Transcribing = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}
