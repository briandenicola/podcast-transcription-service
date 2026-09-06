using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services;

public static class JobDisplay
{
    public static string BadgeClass(JobState state) => state switch
    {
        JobState.Queued => "bg-secondary",
        JobState.Downloading or JobState.Preparing => "bg-info text-dark",
        JobState.Transcribing => "bg-primary",
        JobState.Summarizing => "bg-info text-dark",
        JobState.Completed => "bg-success",
        JobState.Failed => "bg-danger",
        JobState.Cancelled => "bg-warning text-dark",
        _ => "bg-secondary"
    };

    public static bool IsActive(JobState state) =>
        state is JobState.Queued or JobState.Downloading or JobState.Preparing
              or JobState.Transcribing or JobState.Summarizing;

    public static bool IsRunning(JobState state) =>
        state is JobState.Downloading or JobState.Preparing
              or JobState.Transcribing or JobState.Summarizing;

    public static string Elapsed(Job job)
    {
        if (job.StartedAt is not { } started)
        {
            return "—";
        }

        var end = job.CompletedAt ?? DateTimeOffset.UtcNow;
        var span = end - started;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>
    /// Audio seconds divided by wall seconds so far. The number to look at when deciding whether
    /// the model or the hardware needs to change.
    /// </summary>
    public static string RealtimeFactor(Job job)
    {
        if (job.StartedAt is not { } started || job.ProcessedAudioSec <= 0)
        {
            return "—";
        }

        var elapsed = ((job.CompletedAt ?? DateTimeOffset.UtcNow) - started).TotalSeconds;
        return elapsed <= 0 ? "—" : $"{job.ProcessedAudioSec / elapsed:N1}×";
    }
}
