using PodcastTranscription.Web.Services.Chunking;

namespace PodcastTranscription.Tests;

public class SilenceParserTests
{
    private const string FfmpegOutput = """
    [silencedetect @ 0x7f8e1] silence_start: 12.345
    [silencedetect @ 0x7f8e1] silence_end: 13.5 | silence_duration: 1.155
    [silencedetect @ 0x7f8e1] silence_start: 601.02
    [silencedetect @ 0x7f8e1] silence_end: 602.8 | silence_duration: 1.78
    size=N/A time=00:20:00.00 bitrate=N/A speed=  55x
    """;

    [Fact]
    public void Parse_reads_paired_start_and_end_lines()
    {
        var intervals = SilenceParser.Parse(FfmpegOutput);

        Assert.Equal(2, intervals.Count);
        Assert.Equal(new SilenceInterval(12_345, 13_500), intervals[0]);
        Assert.Equal(new SilenceInterval(601_020, 602_800), intervals[1]);
    }

    [Fact]
    public void Midpoint_sits_in_the_middle_of_the_pause() =>
        Assert.Equal(12_922, new SilenceInterval(12_345, 13_500).MidpointMs);

    [Fact]
    public void Parse_ignores_a_trailing_start_that_never_closed()
    {
        var intervals = SilenceParser.Parse("""
        [silencedetect @ 0x1] silence_start: 5.0
        [silencedetect @ 0x1] silence_end: 6.0 | silence_duration: 1.0
        [silencedetect @ 0x1] silence_start: 99.0
        """);

        Assert.Single(intervals);
        Assert.Equal(5_000, intervals[0].StartMs);
    }

    [Fact]
    public void Parse_returns_nothing_for_audio_with_no_pauses() =>
        Assert.Empty(SilenceParser.Parse("size=N/A time=00:00:10.00 bitrate=N/A"));
}
