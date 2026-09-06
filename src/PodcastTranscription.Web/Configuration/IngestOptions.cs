namespace PodcastTranscription.Web.Configuration;

/// <summary>Feed polling and URL downloads.</summary>
public class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>Set false to stop the poller entirely; feeds can still be polled by hand.</summary>
    public bool PollFeeds { get; set; } = true;

    /// <summary>How often each feed is checked.</summary>
    public int PollIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How long after startup the first poll runs. Long enough that a restart during an
    /// episode does not race the transcription worker for the whisper endpoint.
    /// </summary>
    public int InitialPollDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Episodes already in a feed when it is first subscribed are recorded but not queued —
    /// subscribing to a decade-old show should not enqueue a decade of audio. Use Backfill to
    /// pull history deliberately.
    /// </summary>
    public bool QueueExistingItemsOnSubscribe { get; set; }

    /// <summary>Cap on how many items one poll will accept from a single feed.</summary>
    public int MaxItemsPerPoll { get; set; } = 50;

    /// <summary>Default count for the Backfill action.</summary>
    public int DefaultBackfillCount { get; set; } = 5;

    /// <summary>Give up on a download that hangs.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 60;

    /// <summary>Feed fetches are small; this only needs to cover a slow host.</summary>
    public int FeedTimeoutSeconds { get; set; } = 30;
}
