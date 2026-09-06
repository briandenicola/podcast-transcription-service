namespace PodcastTranscription.Web.Configuration;

/// <summary>Housekeeping: pruning source audio and backing up the database.</summary>
public class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public bool Enabled { get; set; } = true;

    /// <summary>Local time of day to run, as HH:mm.</summary>
    public string RunAt { get; set; } = "03:30";

    /// <summary>
    /// Delete source audio once an episode has a completed transcript. Transcripts are kilobytes
    /// and podcast audio is not, but this is off by default: deleting the original is not
    /// reversible, and re-transcribing on a better model needs it.
    /// </summary>
    public bool DeleteSourceAfterTranscription { get; set; }

    /// <summary>
    /// Grace period before source audio is eligible, so a transcript can be eyeballed first.
    /// </summary>
    public int DeleteSourceAfterDays { get; set; } = 30;

    /// <summary>
    /// Keep the prepared 16 kHz WAV even when the source goes. Diarization (§7) reads it, and
    /// regenerating it needs exactly the source that was just deleted.
    /// </summary>
    public bool KeepPreparedWav { get; set; } = true;

    public bool BackupDatabase { get; set; } = true;

    /// <summary>How many nightly backups to keep before the oldest is deleted.</summary>
    public int BackupsToKeep { get; set; } = 7;
}
