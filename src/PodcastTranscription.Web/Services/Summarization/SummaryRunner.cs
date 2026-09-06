using System.Collections.Concurrent;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// Runs summarisation in the background and remembers how far along it is.
///
/// Summarising a long episode is several minutes of model time, so the button that starts it
/// cannot be a request that waits for the answer — the browser would sit on a dead connection
/// until something timed out. It is started here, the POST redirects immediately, and the
/// episode page reads progress back out over ordinary HTTP.
///
/// A singleton for the same reason <see cref="RunningJobs"/> is: the request that starts a run
/// is gone long before the run finishes, so the state has to outlive it.
/// </summary>
public class SummaryRunner(IServiceScopeFactory scopes, ILogger<SummaryRunner> log)
{
    private readonly ConcurrentDictionary<int, SummaryRun> _runs = new();

    /// <summary>The run for a transcript, finished or otherwise. Null when it has never been asked for.</summary>
    public SummaryRun? For(int transcriptId) => _runs.GetValueOrDefault(transcriptId);

    public bool IsRunning(int transcriptId) => For(transcriptId) is { Finished: false };

    /// <summary>
    /// Starts a run unless one is already going. Returns false when it was already in flight, so
    /// a double-click — or a page reload that re-posts — does not start a second one.
    /// </summary>
    public bool Start(int transcriptId, int episodeId)
    {
        var run = new SummaryRun { EpisodeId = episodeId };

        if (_runs.TryGetValue(transcriptId, out var existing) && !existing.Finished)
        {
            return false;
        }

        _runs[transcriptId] = run;

        // Deliberately not awaited, and given its own scope: the request that started this is
        // about to end, taking its scoped DbContext with it.
        _ = Task.Run(() => RunAsync(transcriptId, run));
        return true;
    }

    private async Task RunAsync(int transcriptId, SummaryRun run)
    {
        using var scope = scopes.CreateScope();
        var summaries = scope.ServiceProvider.GetRequiredService<SummaryService>();

        var progress = new Progress<SummaryProgress>(p =>
        {
            run.Pass = p.Pass;
            run.TotalPasses = p.TotalPasses;
            run.Stage = p.Stage;
        });

        try
        {
            var result = await summaries.SummarizeAsync(transcriptId, progress, CancellationToken.None);

            run.Error = result.Error;
            run.Succeeded = result.Success;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Background summarisation of transcript {TranscriptId} failed", transcriptId);
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
            run.Succeeded = false;
        }
        finally
        {
            run.Finished = true;
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>
/// One summarisation, in flight or done with. Mutated by the background task and read by
/// whichever request asks next; the fields are simple enough that torn reads do not matter —
/// a progress bar one poll behind is not worth a lock.
/// </summary>
public class SummaryRun
{
    public int EpisodeId { get; init; }

    public volatile string Stage = "Starting";
    public volatile int Pass;
    public volatile int TotalPasses;

    public volatile bool Finished;
    public volatile bool Succeeded;
    public volatile string? Error;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public int Percent => TotalPasses <= 0 ? 0 : Math.Clamp(Pass * 100 / TotalPasses, 0, 100);

    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}
