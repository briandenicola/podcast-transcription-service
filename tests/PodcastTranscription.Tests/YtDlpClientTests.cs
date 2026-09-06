using PodcastTranscription.Web.Services.Ingest;

namespace PodcastTranscription.Tests;

public class YtDlpClientTests
{
    [Theory]
    [InlineData("https://example.com/episode.mp3")]
    [InlineData("http://example.com/watch?v=abc")]
    public void Http_and_https_urls_are_accepted(string url) =>
        Assert.True(YtDlpClient.IsSupportedUrl(url));

    /// <summary>
    /// Arguments reach yt-dlp as an array rather than through a shell, so there is no command
    /// injection — but yt-dlp understands schemes that would read the server's own disk.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a.mp3")]
    [InlineData("/etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused(string? url) =>
        Assert.False(YtDlpClient.IsSupportedUrl(url));

    [Fact]
    public void Metadata_is_read_from_the_json_yt_dlp_prints()
    {
        var stdout = """
            [download] Destination: /media/source/abc.m4a
            {"id":"abc","title":"Ep. 12 — Pricing","upload_date":"20260303","duration":3723.4,"requested_downloads":[{"filepath":"/media/source/abc.m4a"}]}
            """;

        var result = YtDlpClient.ParseMetadata(stdout);

        Assert.NotNull(result);
        Assert.Equal("/media/source/abc.m4a", result!.FilePath);
        Assert.Equal("Ep. 12 — Pricing", result.Title);
        Assert.Equal(3723.4, result.DurationSec);
        Assert.Equal(new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero), result.PublishedAt);
    }

    [Fact]
    public void Older_yt_dlp_output_reports_the_path_as_underscore_filename()
    {
        var result = YtDlpClient.ParseMetadata("""{"id":"abc","_filename":"/media/source/abc.mp3"}""");

        Assert.Equal("/media/source/abc.mp3", result!.FilePath);
    }

    [Fact]
    public void Missing_optional_fields_are_tolerated()
    {
        var result = YtDlpClient.ParseMetadata("""{"_filename":"/media/a.mp3","duration":null}""");

        Assert.NotNull(result);
        Assert.Null(result!.Title);
        Assert.Null(result.DurationSec);
        Assert.Null(result.PublishedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[download] 100% of 12MiB")]
    [InlineData("{not json}")]
    [InlineData("""{"title":"no path anywhere"}""")]
    public void Output_that_does_not_say_where_the_file_went_yields_nothing(string stdout) =>
        Assert.Null(YtDlpClient.ParseMetadata(stdout));
}
