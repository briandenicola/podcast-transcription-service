namespace PodcastTranscription.Web.Configuration;

/// <summary>
/// Where Ollama lives and how episode summaries are asked for. Same shape as
/// <see cref="WhisperOptions"/> and for the same reason: the model that summarises does not have
/// to run on the box that serves the UI, and usually should not. Override with
/// <c>Ollama__BaseUrl</c> and friends in the environment.
/// </summary>
public class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>
    /// Off unless asked for. Summarisation is optional, and a host with no Ollama on it should
    /// not spend every finished job failing to reach one.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Base address of the Ollama server, e.g. <c>http://gpu-box:11434</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// The model tag to generate with, as <c>ollama list</c> shows it. Recorded against each
    /// summary, so a re-run on a different model is distinguishable from the old one.
    /// </summary>
    public string Model { get; set; } = "llama3.1:8b";

    /// <summary>Summarise as the last step of every transcription job. Off leaves it to the button on the episode page.</summary>
    public bool AutoSummarize { get; set; } = true;

    /// <summary>
    /// A long episode is several generate calls back to back, and a CPU-only Ollama is slow.
    /// The 100 s HttpClient default would abandon most of them.
    /// </summary>
    public int RequestTimeoutMinutes { get; set; } = 30;

    /// <summary>Low, deliberately: this is summarisation, not writing. Higher invents.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>
    /// <c>num_ctx</c>. Ollama's own default is 2048 tokens, which silently truncates the middle
    /// of anything episode-sized — the failure looks like a model that ignored half the
    /// transcript, because that is exactly what happened. Set it explicitly.
    /// </summary>
    public int ContextTokens { get; set; } = 8192;

    /// <summary><c>num_predict</c>: the ceiling on how long one answer may run.</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>
    /// Whether a reasoning model may think before answering.
    ///
    /// Off, and sent explicitly, because Ollama turns it <em>on</em> by default for any model
    /// that supports it — and a thinking model then spends <see cref="MaxOutputTokens"/> on
    /// reasoning and returns an empty answer. qwen3, deepseek-r1 and gpt-oss all do this. The
    /// failure is invisible from the outside: a 200 with nothing in it.
    ///
    /// Sending <c>false</c> is safe on models that cannot think — Ollama only rejects the field
    /// when it is set to <c>true</c> on a model without the capability.
    ///
    /// Summarising does not want reasoning anyway: the work is reading and compressing, and the
    /// thinking budget is better spent on the summary. Turn it on only alongside a much larger
    /// <see cref="MaxOutputTokens"/>.
    /// </summary>
    public bool Think { get; set; }

    /// <summary>
    /// How much transcript goes into one pass, in characters. Roughly four characters to a token,
    /// so this wants to sit well inside <see cref="ContextTokens"/> with the prompt and the
    /// answer allowed for. Anything longer is summarised in windows and then combined.
    /// </summary>
    public int MaxWindowChars { get; set; } = 12000;

    /// <summary>
    /// Appended to the summary instructions. Where a show-specific steer goes — "the hosts are X
    /// and Y", "always note which guest made a claim" — without editing the code.
    /// </summary>
    public string? ExtraInstructions { get; set; }
}
