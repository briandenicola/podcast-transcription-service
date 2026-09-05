namespace PodcastTranscription.Web.Services;

/// <summary>
/// In-process fan-out of job changes to every connected Blazor circuit. A circuit is already a
/// SignalR connection, so pushing through it needs no hub of its own: components subscribe here
/// and re-render when the worker reports progress.
/// </summary>
public class JobNotifier
{
    /// <summary>Raised with the id of the job that changed. Handlers run on the worker's thread.</summary>
    public event Action<int>? JobChanged;

    public void Notify(int jobId) => JobChanged?.Invoke(jobId);
}
