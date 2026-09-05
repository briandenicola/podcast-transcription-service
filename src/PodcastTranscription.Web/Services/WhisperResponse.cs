using System.Text.Json.Serialization;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// whisper-server's <c>verbose_json</c> body. Note this is not the plan's
/// <c>response_format=json</c>: plain json returns only a flat <c>text</c> field, with no
/// segments, timings or <c>avg_logprob</c>, so there is nothing to build a transcript from.
/// </summary>
public record WhisperResponse
{
    [JsonPropertyName("task")] public string? Task { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }

    /// <summary>Audio duration in seconds, as whisper saw it.</summary>
    [JsonPropertyName("duration")] public double? Duration { get; init; }

    [JsonPropertyName("text")] public string? Text { get; init; }

    [JsonPropertyName("segments")] public List<WhisperSegment> Segments { get; init; } = [];
}

public record WhisperSegment
{
    [JsonPropertyName("id")] public int Id { get; init; }

    /// <summary>Seconds from the start of the posted audio.</summary>
    [JsonPropertyName("start")] public double Start { get; init; }
    [JsonPropertyName("end")] public double End { get; init; }

    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

    [JsonPropertyName("avg_logprob")] public double? AvgLogProb { get; init; }
    [JsonPropertyName("no_speech_prob")] public double? NoSpeechProb { get; init; }
    [JsonPropertyName("compression_ratio")] public double? CompressionRatio { get; init; }

    public int StartMs => (int)Math.Round(Start * 1000);
    public int EndMs => (int)Math.Round(End * 1000);
}

/// <summary>Per-request overrides. Anything left null falls back to <c>WhisperOptions</c>.</summary>
public record WhisperRequest
{
    public string? Language { get; init; }
    public string? Prompt { get; init; }
    public double? Temperature { get; init; }

    /// <summary>
    /// Set to 1 together with <see cref="SplitOnWord"/> to collapse each segment down to a
    /// single word with its own timing — how word-level timestamps come out of the HTTP API.
    /// </summary>
    public int? MaxLen { get; init; }

    public bool SplitOnWord { get; init; }
}
