using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Tests;

public class WordGrouperTests
{
    private static TranscribedWord Word(string text, int startMs, int endMs, double? logProb = null) =>
        new() { Text = text, StartMs = startMs, EndMs = endMs, AvgLogProb = logProb };

    [Fact]
    public void No_words_produce_no_segments() =>
        Assert.Empty(WordGrouper.Group([], 700, 200));

    [Fact]
    public void Terminal_punctuation_closes_a_segment()
    {
        var words = new[]
        {
            Word("Hello", 0, 400),
            Word("there.", 400, 900),
            Word("General", 950, 1400),
            Word("Kenobi.", 1400, 2000)
        };

        var segments = WordGrouper.Group(words, 700, 200);

        Assert.Equal(2, segments.Count);
        Assert.Equal("Hello there.", segments[0].Text);
        Assert.Equal("General Kenobi.", segments[1].Text);
    }

    [Fact]
    public void Question_and_exclamation_marks_close_a_segment_too()
    {
        var words = new[] { Word("Really?", 0, 500), Word("Yes!", 600, 1000) };

        var segments = WordGrouper.Group(words, 700, 200);

        Assert.Equal(["Really?", "Yes!"], segments.Select(s => s.Text));
    }

    [Fact]
    public void A_long_pause_starts_a_new_segment_even_mid_sentence()
    {
        var words = new[]
        {
            Word("the", 0, 300),
            Word("thing", 300, 700),
            Word("is", 2000, 2300) // 1.3 s gap
        };

        var segments = WordGrouper.Group(words, 700, 200);

        Assert.Equal(2, segments.Count);
        Assert.Equal("the thing", segments[0].Text);
        Assert.Equal("is", segments[1].Text);
    }

    [Fact]
    public void A_gap_at_the_threshold_does_not_split()
    {
        var words = new[] { Word("one", 0, 300), Word("two", 1000, 1300) }; // exactly 700 ms

        Assert.Single(WordGrouper.Group(words, 700, 200));
    }

    [Fact]
    public void A_run_on_sentence_is_broken_once_it_gets_long()
    {
        // 60 words of five characters each, no punctuation and no pauses.
        var words = Enumerable.Range(0, 60)
            .Select(i => Word("aaaa", i * 300, i * 300 + 250))
            .ToList();

        var segments = WordGrouper.Group(words, 700, 200);

        Assert.True(segments.Count > 1);
        Assert.All(segments, s => Assert.True(s.Text.Length <= 210, $"segment was {s.Text.Length} chars"));
    }

    [Fact]
    public void Segment_timings_span_the_words_it_was_built_from()
    {
        var words = new[] { Word("Hello", 1_000, 1_400), Word("there.", 1_400, 1_900) };

        var segment = WordGrouper.Group(words, 700, 200).Single();

        Assert.Equal(1_000, segment.StartMs);
        Assert.Equal(1_900, segment.EndMs);
        Assert.Equal(2, segment.Words.Count);
    }

    [Fact]
    public void Segment_confidence_averages_the_words()
    {
        var words = new[] { Word("Hello", 0, 400, -0.2), Word("there.", 400, 900, -0.4) };

        var segment = WordGrouper.Group(words, 700, 200).Single();

        Assert.Equal(-0.3, segment.AvgLogProb!.Value, precision: 10);
    }

    [Fact]
    public void Punctuation_arriving_as_its_own_word_is_not_spaced_off()
    {
        var words = new[] { Word("Well", 0, 300), Word(",", 300, 350), Word("yes.", 350, 800) };

        Assert.Equal("Well, yes.", WordGrouper.Group(words, 700, 200).Single().Text);
    }

    [Fact]
    public void A_closing_quote_after_the_full_stop_still_ends_the_sentence()
    {
        var words = new[] { Word("\"Stop.\"", 0, 500), Word("Then", 550, 900) };

        var segments = WordGrouper.Group(words, 700, 200);

        Assert.Equal(2, segments.Count);
        Assert.Equal("\"Stop.\"", segments[0].Text);
    }

    [Fact]
    public void Every_word_survives_the_regrouping()
    {
        var words = Enumerable.Range(0, 40)
            .Select(i => Word(i % 7 == 6 ? $"w{i}." : $"w{i}", i * 400, i * 400 + 350))
            .ToList();

        var segments = WordGrouper.Group(words, 700, 200);

        Assert.Equal(words.Count, segments.Sum(s => s.Words.Count));
        Assert.Equal(words.Select(w => w.Text), segments.SelectMany(s => s.Words).Select(w => w.Text));
    }
}
