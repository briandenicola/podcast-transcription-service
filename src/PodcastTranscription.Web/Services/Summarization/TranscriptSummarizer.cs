using System.Diagnostics;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// Turns a transcript into a summary by asking a local model, in as many passes as the episode
/// needs.
///
/// A short episode is one call. A long one does not fit any context window worth running on a
/// home GPU, so it is read in windows, each window producing notes, and a final pass writes the
/// summary from the notes. When even the notes are too long to fit — a three-hour episode on a
/// small context — they are folded in batches first, and that repeats until they do. The shape
/// is map/reduce, and the reason for it is that the alternative is not a worse summary but a
/// summary of the first twenty minutes with the rest silently truncated away.
/// </summary>
public class TranscriptSummarizer(
    OllamaClient ollama,
    IOptions<OllamaOptions> options,
    ILogger<TranscriptSummarizer> log)
{
    private readonly OllamaOptions _options = options.Value;

    /// <summary>
    /// A hard stop on the fold loop. Each pass reduces the notes by a large factor, so three
    /// rounds covers an implausibly long episode; the cap is here so a model that answers with
    /// more text than it was given cannot spin forever.
    /// </summary>
    private const int MaxFoldRounds = 3;

    public async Task<SummaryDraft> SummarizeAsync(
        string episodeTitle, string? show, IReadOnlyList<Segment> segments,
        IProgress<SummaryProgress>? progress = null, CancellationToken ct = default)
    {
        var lines = TranscriptWindower.ToLines(segments);
        if (lines.Count == 0)
        {
            throw new OllamaException("The transcript has no text to summarise.");
        }

        var system = SummaryPrompts.WithExtra(SummaryPrompts.System, _options.ExtraInstructions);
        var windows = TranscriptWindower.Split(lines, _options.MaxWindowChars);
        var wallClock = Stopwatch.StartNew();

        // One call per window plus the final one. Folding adds more, and raises the total as it
        // goes — an estimate that grows is still far better than a spinner with no number on it,
        // and on a long episode this is minutes of staring at the page.
        var pass = 0;
        var totalPasses = windows.Count + 1;

        void Report(string stage) => progress?.Report(new SummaryProgress(pass, totalPasses, stage));

        var promptTokens = 0;
        var completionTokens = 0;
        var counted = false;

        void Account(OllamaCompletion completion)
        {
            if (completion.PromptTokens is { } prompt)
            {
                promptTokens += prompt;
                counted = true;
            }

            if (completion.CompletionTokens is { } output)
            {
                completionTokens += output;
                counted = true;
            }
        }

        string body;
        bool bodyIsNotes;

        if (windows.Count == 1)
        {
            body = windows[0];
            bodyIsNotes = false;
        }
        else
        {
            log.LogInformation(
                "Summarising '{Title}' in {Windows} windows over {Chars:N0} characters of transcript",
                episodeTitle, windows.Count, lines.Sum(l => l.Length));

            var notes = new List<string>(windows.Count);

            for (var i = 0; i < windows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Reading part {i + 1} of {windows.Count}");

                var completion = await ollama.GenerateAsync(
                    system, SummaryPrompts.MapPrompt(windows[i], i, windows.Count), ct);

                pass++;

                Account(completion);
                notes.Add(completion.Text);

                log.LogDebug("Window {Index}/{Total} noted in {Elapsed}ms",
                    i + 1, windows.Count, completion.ElapsedMs);
            }

            notes = await FoldAsync(notes, system, Account, Report, () => { pass++; totalPasses++; }, ct);

            body = string.Join("\n\n", notes);
            bodyIsNotes = true;
        }

        ct.ThrowIfCancellationRequested();
        Report("Writing the summary");

        var final = await ollama.GenerateAsync(
            system, SummaryPrompts.ReducePrompt(episodeTitle, show, body, bodyIsNotes), ct);

        Account(final);
        pass++;
        Report("Finished");

        log.LogInformation("Summarised '{Title}' with {Model} in {Elapsed}",
            episodeTitle, ollama.Model, wallClock.Elapsed);

        return new SummaryDraft(
            final.Text,
            ollama.Model,
            windows.Count,
            counted ? promptTokens : null,
            counted ? completionTokens : null,
            wallClock.ElapsedMilliseconds);
    }

    /// <summary>
    /// Folds notes until they fit one prompt. Returns them unchanged when they already do, which
    /// is the case for everything but a very long episode on a small context window.
    /// </summary>
    private async Task<List<string>> FoldAsync(
        List<string> notes, string system, Action<OllamaCompletion> account,
        Action<string> report, Action counted, CancellationToken ct)
    {
        for (var round = 0; round < MaxFoldRounds; round++)
        {
            var total = notes.Sum(n => n.Length) + (notes.Count * 2);
            if (total <= _options.MaxWindowChars)
            {
                return notes;
            }

            // A single set of notes has nothing to be merged with. It is over budget, and folding
            // it against itself would send the same prompt again for the same answer, so hand it
            // to the final pass as it is.
            if (notes.Count < 2)
            {
                break;
            }

            var batches = TranscriptWindower.Batch(notes, _options.MaxWindowChars);

            log.LogDebug("Folding {Notes} sets of notes into {Batches} (round {Round})",
                notes.Count, batches.Count, round + 1);

            var folded = new List<string>(batches.Count);
            for (var i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                report($"Combining notes ({i + 1} of {batches.Count})");

                var completion = await ollama.GenerateAsync(
                    system, SummaryPrompts.FoldPrompt(string.Join("\n\n", batches[i])), ct);

                account(completion);
                counted();
                folded.Add(completion.Text);
            }

            notes = folded;
        }

        return notes;
    }
}

/// <summary>
/// How far along a run is. <see cref="TotalPasses"/> can rise mid-run when folding turns out to
/// be needed, so it is an estimate rather than a promise.
/// </summary>
public record SummaryProgress(int Pass, int TotalPasses, string Stage)
{
    public int Percent => TotalPasses <= 0 ? 0 : Math.Clamp(Pass * 100 / TotalPasses, 0, 100);
}

/// <summary>A finished summary, before it is attached to anything.</summary>
public record SummaryDraft(
    string Content,
    string Model,
    int WindowCount,
    int? PromptTokens,
    int? CompletionTokens,
    long DurationMs);
