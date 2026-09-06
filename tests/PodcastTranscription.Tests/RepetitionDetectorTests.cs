using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Tests;

public class RepetitionDetectorTests
{
    private static List<Segment> Segments(params string[] lines) =>
        lines.Select((text, i) => new Segment
        {
            Ordinal = i,
            StartMs = i * 5_000,
            EndMs = (i * 5_000) + 4_000,
            Text = text
        }).ToList();

    [Fact]
    public void A_normal_transcript_raises_nothing() =>
        Assert.Null(RepetitionDetector.Detect(Segments(
            "Hello, and welcome to the history of Rome.",
            "The founding of Rome is an event wrapped in myth.",
            "But we do know the legend the Romans told themselves.",
            "It is the story of a refugee Trojan prince.",
            "The story of Rome begins with the end of the Trojan War.",
            "After Troy was finally sacked by the Greeks, Aeneas escaped.")));

    /// <summary>The failure that prompted this: one line emitted to the end of the chunk.</summary>
    [Fact]
    public void A_stuck_decoder_is_detected()
    {
        var lines = new List<string>
        {
            "Hello, and welcome to the history of Rome.",
            "Virgil writes that in the final moments before she committed suicide,"
        };
        lines.AddRange(Enumerable.Repeat("Virgil wrote that the Aeneas was the only one who could be saved.", 40));

        var finding = RepetitionDetector.Detect(Segments([.. lines]));

        Assert.NotNull(finding);
        Assert.Equal(40, finding!.RepeatCount);
        Assert.Equal(10_000, finding.StartMs);
        Assert.Contains("Virgil wrote", finding.Text);
    }

    [Fact]
    public void The_description_names_the_count_and_the_timestamp()
    {
        var lines = Enumerable.Repeat("stuck", 8).ToList();
        var finding = RepetitionDetector.Detect(Segments([.. lines]))!;

        var description = RepetitionDetector.Describe(finding);

        Assert.Contains("8 times", description);
        Assert.Contains("0:00", description);
        Assert.Contains("Re-transcribing", description);
    }

    [Fact]
    public void A_run_just_under_the_threshold_is_left_alone()
    {
        // A refrain repeated a few times is normal speech, not a decoder fault.
        var lines = Enumerable.Repeat("So it goes.", RepetitionDetector.DefaultThreshold - 1).ToList();
        lines.Insert(0, "Something else entirely.");

        Assert.Null(RepetitionDetector.Detect(Segments([.. lines])));
    }

    [Fact]
    public void A_run_at_the_threshold_is_reported()
    {
        var lines = Enumerable.Repeat("So it goes.", RepetitionDetector.DefaultThreshold).ToList();

        Assert.NotNull(RepetitionDetector.Detect(Segments([.. lines])));
    }

    [Fact]
    public void Only_consecutive_repeats_count()
    {
        // The same sentence recurring through an episode is not a loop.
        var lines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            lines.Add("And now for something completely different.");
            lines.Add($"Filler line {i}.");
        }

        Assert.Null(RepetitionDetector.Detect(Segments([.. lines])));
    }

    [Fact]
    public void Case_and_punctuation_differences_do_not_hide_a_loop()
    {
        var finding = RepetitionDetector.Detect(Segments(
            "The same line.", "the same line", "The Same Line!", "the same line...", "THE SAME LINE.",
            "The same line?"));

        Assert.NotNull(finding);
        Assert.Equal(6, finding!.RepeatCount);
    }

    [Fact]
    public void The_longest_run_is_the_one_reported()
    {
        var lines = new List<string>();
        lines.AddRange(Enumerable.Repeat("first loop", 6));
        lines.Add("a real line");
        lines.AddRange(Enumerable.Repeat("second loop", 12));

        var finding = RepetitionDetector.Detect(Segments([.. lines]));

        Assert.Equal(12, finding!.RepeatCount);
        Assert.Equal("second loop", finding.Text);
    }

    [Fact]
    public void Empty_segments_are_not_treated_as_a_repetition()
    {
        var finding = RepetitionDetector.Detect(Segments("", "  ", "", "   ", "", " ", ""));

        Assert.Null(finding);
    }

    [Fact]
    public void A_transcript_shorter_than_the_threshold_is_skipped() =>
        Assert.Null(RepetitionDetector.Detect(Segments("one", "two")));

    [Fact]
    public void Segments_are_examined_in_ordinal_order_not_list_order()
    {
        var segments = Segments("loop", "loop", "loop", "loop", "loop", "different");
        segments.Reverse();

        var finding = RepetitionDetector.Detect(segments);

        Assert.NotNull(finding);
        Assert.Equal(5, finding!.RepeatCount);
        Assert.Equal(0, finding.StartMs);
    }
}
