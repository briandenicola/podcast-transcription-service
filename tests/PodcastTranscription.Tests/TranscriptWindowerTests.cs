using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

public class TranscriptWindowerTests
{
    private static List<Segment> Segments(params (int StartMs, string Text)[] items) =>
        items.Select((item, i) => new Segment
        {
            Ordinal = i,
            StartMs = item.StartMs,
            EndMs = item.StartMs + 1000,
            Text = item.Text
        }).ToList();

    [Fact]
    public void ToLines_prefixes_each_segment_with_a_timestamp_the_summary_can_cite()
    {
        var lines = TranscriptWindower.ToLines(Segments(
            (0, "Hello there."),
            (3_661_000, "General Kenobi.")));

        Assert.Equal(["[00:00:00] Hello there.", "[01:01:01] General Kenobi."], lines);
    }

    [Fact]
    public void ToLines_orders_by_ordinal_rather_than_trusting_the_caller()
    {
        var segments = Segments((0, "first"), (1000, "second"));
        segments.Reverse();

        var lines = TranscriptWindower.ToLines(segments);

        Assert.Equal("[00:00:00] first", lines[0]);
    }

    [Fact]
    public void ToLines_drops_segments_that_are_empty_once_trimmed()
    {
        var lines = TranscriptWindower.ToLines(Segments((0, "kept"), (1000, "   ")));

        Assert.Single(lines);
    }

    [Fact]
    public void Split_keeps_every_window_inside_the_budget()
    {
        var lines = Enumerable.Range(0, 40).Select(i => new string('x', 50) + i).ToList();

        var windows = TranscriptWindower.Split(lines, 600);

        Assert.True(windows.Count > 1);
        Assert.All(windows, w => Assert.True(w.Length <= 600, $"window was {w.Length} characters"));
    }

    [Fact]
    public void Split_never_cuts_a_line_in_half()
    {
        var lines = new List<string> { "[00:00:00] alpha", "[00:00:05] beta", "[00:00:10] gamma" };

        var windows = TranscriptWindower.Split(lines, 20);

        // Every line survives whole and in order, however the windows fell.
        Assert.Equal(lines, string.Join("\n", windows).Split('\n'));
    }

    [Fact]
    public void Split_gives_an_oversized_line_a_window_of_its_own_rather_than_dropping_it()
    {
        var monster = new string('y', 4000);
        var lines = new List<string> { "short one", monster, "another short one" };

        var windows = TranscriptWindower.Split(lines, 1000);

        Assert.Contains(windows, w => w == monster);
    }

    [Fact]
    public void Split_returns_a_single_window_when_the_whole_transcript_fits()
    {
        var windows = TranscriptWindower.Split(["one", "two"], 12000);

        Assert.Single(windows);
        Assert.Equal("one\ntwo", windows[0]);
    }

    [Fact]
    public void Split_of_nothing_is_nothing() => Assert.Empty(TranscriptWindower.Split([], 100));

    [Fact]
    public void Batch_always_puts_at_least_two_notes_together_so_a_fold_actually_shrinks()
    {
        // Each note is under the budget but no two fit together. Batching one-per-batch here
        // would leave a fold round with as many notes as it started with, and the loop that
        // calls this would never converge.
        var notes = Enumerable.Range(0, 7).Select(i => new string('n', 400) + i).ToList();

        var batches = TranscriptWindower.Batch(notes, 500);

        Assert.True(batches.Count < notes.Count, "a fold round must reduce the number of notes");
        Assert.Equal(notes, batches.SelectMany(b => b).ToList());
    }

    [Fact]
    public void Batch_groups_notes_without_losing_or_reordering_any()
    {
        var notes = Enumerable.Range(0, 9).Select(i => new string('n', 300) + i).ToList();

        var batches = TranscriptWindower.Batch(notes, 1000);

        Assert.True(batches.Count > 1);
        Assert.Equal(notes, batches.SelectMany(b => b).ToList());
    }
}
