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
        string episodeTitle, string? show, IReadOnlyList<Segment> segments, CancellationToken ct = default)
    {
        var lines = TranscriptWindower.ToLines(segments);
        if (lines.Count == 0)
        {
            throw new OllamaException("The transcript has no text to summarise.");
        }

        var system = SummaryPrompts.WithExtra(SummaryPrompts.System, _options.ExtraInstructions);
        var windows = TranscriptWindower.Split(lines, _options.MaxWindowChars);
        var wallClock = Stopwatch.StartNew();

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

                var completion = await ollama.GenerateAsync(
                    system, SummaryPrompts.MapPrompt(windows[i], i, windows.Count), ct);

                Account(completion);
                notes.Add(completion.Text);

                log.LogDebug("Window {Index}/{Total} noted in {Elapsed}ms",
                    i + 1, windows.Count, completion.ElapsedMs);
            }

            notes = await FoldAsync(notes, system, Account, ct);

            body = string.Join("\n\n", notes);
            bodyIsNotes = true;
        }

        ct.ThrowIfCancellationRequested();

        var final = await ollama.GenerateAsync(
            system, SummaryPrompts.ReducePrompt(episodeTitle, show, body, bodyIsNotes), ct);

        Account(final);

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
        List<string> notes, string system, Action<OllamaCompletion> account, CancellationToken ct)
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
            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();

                var completion = await ollama.GenerateAsync(
                    system, SummaryPrompts.FoldPrompt(string.Join("\n\n", batch)), ct);

                account(completion);
                folded.Add(completion.Text);
            }

            notes = folded;
        }

        return notes;
    }
}

/// <summary>A finished summary, before it is attached to anything.</summary>
public record SummaryDraft(
    string Content,
    string Model,
    int WindowCount,
    int? PromptTokens,
    int? CompletionTokens,
    long DurationMs);
