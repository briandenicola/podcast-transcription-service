using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(2240, "0:02")]
    [InlineData(61_000, "1:01")]
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(5_425_000, "1:30:25")]
    public void Clock_formats_milliseconds(int milliseconds, string expected) =>
        Assert.Equal(expected, TimeFormat.Clock(milliseconds));

    [Fact]
    public void Clock_never_renders_a_negative_position() =>
        Assert.Equal("0:00", TimeFormat.Clock(-500));

    [Fact]
    public void Duration_renders_an_em_dash_when_unknown() =>
        Assert.Equal("—", TimeFormat.Duration(null));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(2_147_483_648, "2.0 GB")]
    public void Size_formats_bytes(long bytes, string expected) =>
        Assert.Equal(expected, TimeFormat.Size(bytes));
}
