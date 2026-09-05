namespace PodcastTranscription.Web.Configuration;

/// <summary>
/// Where whisper.cpp's <c>whisper-server</c> lives and how we talk to it. The endpoint is a
/// config value on purpose: it can be a sidecar container or a box elsewhere on the network
/// that owns the GPU. Override with <c>Whisper__BaseUrl</c> in the environment.
/// </summary>
public class WhisperOptions
{
    public const string SectionName = "Whisper";

    /// <summary>Base address of whisper-server, e.g. <c>http://windows-host:8080</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Recorded against each transcript so the back catalogue can be reprocessed and compared.
    /// whisper-server loads its model at startup, so this must match what the server is running.
    /// </summary>
    public string Model { get; set; } = "large-v3-turbo-q5_0";

    /// <summary>ISO language code, or <c>auto</c> to let whisper detect it.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Biases decoding toward names and jargon. The cheapest accuracy win available.</summary>
    public string? Prompt { get; set; }

    public double Temperature { get; set; } = 0.0;

    /// <summary>
    /// The default HttpClient timeout of 100 seconds will bite on the first long segment.
    /// This covers a whole un-chunked file in M1, so it is deliberately generous.
    /// </summary>
    public int RequestTimeoutMinutes { get; set; } = 120;
}
