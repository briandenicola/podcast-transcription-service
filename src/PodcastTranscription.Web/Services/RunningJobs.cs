using System.Collections.Concurrent;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// The cancellation tokens of jobs currently being worked. Cancelling from the UI means
/// signalling the worker that holds the job, which needs somewhere shared to look it up.
/// </summary>
public class RunningJobs
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    public IDisposable Track(int jobId, CancellationTokenSource cts)
    {
        _running[jobId] = cts;
        return new Registration(this, jobId);
    }

    public bool IsRunning(int jobId) => _running.ContainsKey(jobId);

    /// <summary>Signals the worker to stop. False when the job is not currently running.</summary>
    public bool Cancel(int jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    private void Release(int jobId) => _running.TryRemove(jobId, out _);

    private sealed class Registration(RunningJobs owner, int jobId) : IDisposable
    {
        public void Dispose() => owner.Release(jobId);
    }
}
