namespace PodcastTranscription.Web.Configuration;

/// <summary>Paths to the external binaries baked into the image.</summary>
public class MediaToolOptions
{
    public const string SectionName = "MediaTools";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string YtDlpPath { get; set; } = "yt-dlp";
}
