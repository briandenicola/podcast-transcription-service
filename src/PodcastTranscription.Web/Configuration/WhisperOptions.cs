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
    /// How much to raise the temperature on each retry when a decode looks degenerate. This is
    /// whisper's own defence against repetition loops — the failure where a segment latches onto
    /// a phrase and emits it over and over to the end of the chunk. Zero disables the fallback
    /// entirely, which is almost never what you want.
    /// </summary>
    public double TemperatureIncrement { get; set; } = 0.2;

    /// <summary>
    /// Entropy above which a decode is considered degenerate and retried at a higher temperature.
    /// Repetitive text compresses well, so a loop shows up here first. Lower is more aggressive.
    /// </summary>
    public double EntropyThreshold { get; set; } = 2.4;

    /// <summary>Average log probability below which a decode is retried.</summary>
    public double LogProbThreshold { get; set; } = -1.0;

    /// <summary>Above this probability of "no speech", a window is treated as silence.</summary>
    public double NoSpeechThreshold { get; set; } = 0.6;

    /// <summary>
    /// How many previous tokens are carried into the next window as context. Carried text is what
    /// lets a repetition loop feed itself, so this defaults to none: chunks are already cut at
    /// silence, where continuity matters least.
    /// </summary>
    public int MaxContext { get; set; }

    /// <summary>
    /// Suppress non-speech tokens. Music stings and applause are where hallucinated text usually
    /// starts, and a hallucination is what a repetition loop latches onto.
    /// </summary>
    public bool SuppressNonSpeechTokens { get; set; } = true;

    /// <summary>Beam search width. Above 1 is slower and markedly less prone to looping.</summary>
    public int BeamSize { get; set; } = 5;

    /// <summary>Candidates sampled per temperature step during fallback.</summary>
    public int BestOf { get; set; } = 5;

    /// <summary>
    /// The default HttpClient timeout of 100 seconds will bite on the first long segment.
    /// This covers a whole un-chunked file in M1, so it is deliberately generous.
    /// </summary>
    public int RequestTimeoutMinutes { get; set; } = 120;
}
