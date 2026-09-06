using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

/// <summary>
/// The map/reduce, against a stubbed Ollama. What is being checked is the shape of the
/// conversation — how many calls, over what — because that is what decides whether the summary
/// describes the whole episode or only the part that fitted in the context window.
/// </summary>
public class TranscriptSummarizerTests
{
    private static string Answer(string text, int promptTokens = 100, int evalTokens = 50) =>
        JsonSerializer.Serialize(new
        {
            model = "llama3.1:8b",
            response = text,
            done = true,
            prompt_eval_count = promptTokens,
            eval_count = evalTokens
        });

    private static TranscriptSummarizer Create(HttpMessageHandler handler, OllamaOptions options)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        var client = new OllamaClient(http, Options.Create(options), NullLogger<OllamaClient>.Instance);

        return new TranscriptSummarizer(
            client, Options.Create(options), NullLogger<TranscriptSummarizer>.Instance);
    }

    private static OllamaOptions Options_(int windowChars) => new()
    {
        Enabled = true,
        BaseUrl = "http://ollama.test:11434",
        Model = "llama3.1:8b",
        MaxWindowChars = windowChars
    };

    /// <summary>Roughly <paramref name="count"/> segments of ~60 characters each.</summary>
    private static List<Segment> Transcript(int count) =>
        Enumerable.Range(0, count).Select(i => new Segment
        {
            Ordinal = i,
            StartMs = i * 5000,
            EndMs = (i * 5000) + 5000,
            Text = $"Sentence number {i} with enough words in it to take up some room."
        }).ToList();

    [Fact]
    public async Task A_short_transcript_is_one_call_over_the_transcript_itself()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("## Overview\nShort.")));
        var summarizer = Create(handler, Options_(12000));

        var draft = await summarizer.SummarizeAsync("Test episode", "A Show", Transcript(5));

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, draft.WindowCount);
        Assert.Equal("## Overview\nShort.", draft.Content);

        // The one call is the final pass, and it gets the transcript, not notes.
        Assert.Contains("Sentence number 0", handler.RequestBodies[0]);
        Assert.Contains("## Points of view", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task The_episode_title_and_show_reach_the_model()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("summary")));

        await Create(handler, Options_(12000)).SummarizeAsync("Rates and Rents", "Econ Hour", Transcript(3));

        Assert.Contains("Rates and Rents", handler.RequestBodies[0]);
        Assert.Contains("Econ Hour", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task A_long_transcript_is_read_in_windows_and_then_summarised_from_the_notes()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("notes for this part")));
        var summarizer = Create(handler, Options_(500));

        var draft = await summarizer.SummarizeAsync("Long episode", null, Transcript(40));

        Assert.True(draft.WindowCount > 1, "the transcript should not have fitted one window");

        // One call per window, plus the final pass over the notes.
        Assert.Equal(draft.WindowCount + 1, handler.RequestCount);

        // Every window pass asks for notes; only the last asks for the finished summary.
        Assert.All(handler.RequestBodies.Take(draft.WindowCount),
            body => Assert.Contains("TRANSCRIPT PART", body));
        Assert.Contains("## Points of view", handler.RequestBodies[^1]);
        Assert.Contains("notes for this part", handler.RequestBodies[^1]);
    }

    [Fact]
    public async Task Every_part_of_a_long_transcript_reaches_the_model_exactly_once()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("notes")));
        var summarizer = Create(handler, Options_(500));

        var draft = await summarizer.SummarizeAsync("Long episode", null, Transcript(40));

        // The whole point of windowing: nothing is silently truncated away.
        var windowPrompts = string.Join("\n", handler.RequestBodies.Take(draft.WindowCount));
        for (var i = 0; i < 40; i++)
        {
            Assert.Contains($"Sentence number {i} ", windowPrompts);
        }
    }

    [Fact]
    public async Task Token_counts_are_summed_across_every_pass()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("notes", 100, 50)));

        var draft = await Create(handler, Options_(500)).SummarizeAsync("Long", null, Transcript(40));

        Assert.Equal(handler.RequestCount * 100, draft.PromptTokens);
        Assert.Equal(handler.RequestCount * 50, draft.CompletionTokens);
    }

    [Fact]
    public async Task Notes_too_long_for_one_prompt_are_folded_before_the_final_pass()
    {
        // Each window answers with notes far larger than the budget, so they cannot all go into
        // the final prompt at once and have to be folded down first.
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, Answer(new string('n', 400))));
        var summarizer = Create(handler, Options_(500));

        var draft = await summarizer.SummarizeAsync("Very long episode", null, Transcript(40));

        Assert.True(handler.RequestCount > draft.WindowCount + 1,
            "there should be fold passes between the windows and the final summary");
        Assert.Contains(handler.RequestBodies, body => body.Contains("Merge them into a single set"));
    }

    [Fact]
    public async Task A_transcript_with_no_usable_text_is_refused_rather_than_sent()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("never asked")));
        var segments = new List<Segment> { new() { Ordinal = 0, Text = "   " } };

        await Assert.ThrowsAsync<OllamaException>(
            () => Create(handler, Options_(12000)).SummarizeAsync("Empty", null, segments));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Extra_instructions_are_appended_to_the_system_prompt()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, Answer("summary")));
        var options = Options_(12000);
        options.ExtraInstructions = "The hosts are Alice and Bob.";

        await Create(handler, options).SummarizeAsync("Test", null, Transcript(3));

        Assert.Contains("The hosts are Alice and Bob.", handler.RequestBodies[0]);
    }
}
