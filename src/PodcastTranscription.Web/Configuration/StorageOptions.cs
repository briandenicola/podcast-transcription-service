namespace PodcastTranscription.Web.Configuration;

/// <summary>
/// Where the database and audio live. Both are mounted volumes in the container, which is why
/// the defaults sit under <c>var/</c> rather than <c>data/</c> and <c>media/</c>: macOS volumes
/// are usually case-insensitive, so a runtime <c>data/</c> directory would collide with the
/// source folder <c>Data/</c>.
/// </summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Directory holding <c>app.db</c>.</summary>
    public string DataPath { get; set; } = "var/data";

    /// <summary>Directory holding source audio and the prepared 16 kHz WAVs.</summary>
    public string MediaPath { get; set; } = "var/media";

    /// <summary>Largest upload accepted, in megabytes.</summary>
    public int MaxUploadMb { get; set; } = 2048;
}
