using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// The write side of summarisation: loads a transcript, has it summarised, and stores the result
/// against it. What the pipeline calls at the end of a job and what the episode page calls when
/// someone presses the button.
///
/// Every failure comes back as a <see cref="SummaryResult"/> rather than an exception. A summary
/// is a nice-to-have bolted onto the end of a transcription that took an hour, and losing that
/// transcription because a language model was unreachable would be an absurd trade.
/// </summary>
public class SummaryService(
    AppDbContext db,
    TranscriptSummarizer summarizer,
    OllamaClient ollama,
    IOptions<OllamaOptions> options,
    ILogger<SummaryService> log)
{
    private readonly OllamaOptions _options = options.Value;

    public bool Enabled => _options.Enabled;

    /// <summary>The configured model, for the empty state on a transcript that has no summary yet.</summary>
    public string Model => ollama.Model;
    public bool AutoSummarize => _options.Enabled && _options.AutoSummarize;

    public async Task<SummaryResult> SummarizeAsync(
        int transcriptId, IProgress<SummaryProgress>? progress = null, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return SummaryResult.Failed(
                "Summarisation is off. Set Ollama__Enabled=true (OLLAMA_ENABLED in .env) and restart.");
        }

        var transcript = await db.Transcripts
            .Include(t => t.Episode)
            .FirstOrDefaultAsync(t => t.Id == transcriptId, ct);

        if (transcript is null)
        {
            return SummaryResult.Failed($"Transcript {transcriptId} no longer exists.");
        }

        var segments = await db.Segments
            .Where(s => s.TranscriptId == transcriptId)
            .OrderBy(s => s.Ordinal)
            .ToListAsync(ct);

        if (segments.Count == 0)
        {
            return SummaryResult.Failed("That transcript has no segments yet.");
        }

        // Checked up front rather than discovered on the first generate call: a model that was
        // never pulled fails every window in turn, and on a long episode that is a lot of waiting
        // for an error that was knowable before any of it started.
        var health = await ollama.CheckHealthAsync(ct);
        if (!health.Ready)
        {
            return SummaryResult.Failed(health.Reachable
                ? $"{ollama.Endpoint} answered, but {health.Status}. Run: ollama pull {ollama.Model}"
                : $"{ollama.Endpoint} did not answer.");
        }

        try
        {
            var draft = await summarizer.SummarizeAsync(
                transcript.Episode?.Title ?? "Untitled episode",
                transcript.Episode?.Show,
                segments,
                progress,
                ct);

            var summary = await db.Summaries.FirstOrDefaultAsync(s => s.TranscriptId == transcriptId, ct);
            if (summary is null)
            {
                summary = new Summary { TranscriptId = transcriptId };
                db.Summaries.Add(summary);
            }

            summary.Model = draft.Model;
            summary.Content = draft.Content;
            summary.WindowCount = draft.WindowCount;
            summary.PromptTokens = draft.PromptTokens;
            summary.CompletionTokens = draft.CompletionTokens;
            summary.DurationMs = draft.DurationMs;
            summary.CreatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);

            log.LogInformation("Summarised transcript {TranscriptId} with {Model} in {Ms}ms",
                transcriptId, draft.Model, draft.DurationMs);

            return SummaryResult.Succeeded(summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OllamaException ex)
        {
            log.LogWarning(ex, "Could not summarise transcript {TranscriptId}", transcriptId);
            return SummaryResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not summarise transcript {TranscriptId}", transcriptId);
            return SummaryResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public record SummaryResult(bool Success, Summary? Summary, string? Error)
{
    public static SummaryResult Succeeded(Summary summary) => new(true, summary, null);
    public static SummaryResult Failed(string error) => new(false, null, error);
}
