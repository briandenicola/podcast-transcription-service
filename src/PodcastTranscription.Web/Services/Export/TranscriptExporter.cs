using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Export;

public enum ExportFormat
{
    Srt,
    Vtt,
    Txt,
    Json,
    Markdown
}

/// <summary>
/// Renders a transcript in the formats other tools expect. Pure string building over the stored
/// segments — nothing here re-reads the audio or calls whisper.
/// </summary>
public static class TranscriptExporter
{
    public static string Render(ExportFormat format, Episode episode, Transcript transcript) => format switch
    {
        ExportFormat.Srt => RenderSrt(transcript),
        ExportFormat.Vtt => RenderVtt(transcript),
        ExportFormat.Txt => RenderText(transcript),
        ExportFormat.Json => RenderJson(episode, transcript),
        ExportFormat.Markdown => RenderMarkdown(episode, transcript),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string ContentType(ExportFormat format) => format switch
    {
        ExportFormat.Srt => "application/x-subrip",
        ExportFormat.Vtt => "text/vtt",
        ExportFormat.Txt => "text/plain",
        ExportFormat.Json => "application/json",
        ExportFormat.Markdown => "text/markdown",
        _ => "application/octet-stream"
    };

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Srt => "srt",
        ExportFormat.Vtt => "vtt",
        ExportFormat.Txt => "txt",
        ExportFormat.Json => "json",
        ExportFormat.Markdown => "md",
        _ => "txt"
    };

    public static bool TryParseFormat(string? value, out ExportFormat format)
    {
        format = ExportFormat.Txt;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "srt": format = ExportFormat.Srt; return true;
            case "vtt": format = ExportFormat.Vtt; return true;
            case "txt" or "text": format = ExportFormat.Txt; return true;
            case "json": format = ExportFormat.Json; return true;
            case "md" or "markdown": format = ExportFormat.Markdown; return true;
            default: return false;
        }
    }

    /// <summary>SubRip: 1-based cue numbers and comma before the milliseconds.</summary>
    private static string RenderSrt(Transcript transcript)
    {
        var output = new StringBuilder();
        var cue = 1;

        foreach (var segment in Ordered(transcript))
        {
            output.Append(cue++).Append('\n')
                .Append(Timestamp(segment.StartMs, ',')).Append(" --> ").Append(Timestamp(segment.EndMs, ','))
                .Append('\n')
                .Append(segment.Text).Append('\n')
                .Append('\n');
        }

        return output.ToString();
    }

    /// <summary>WebVTT: same shape, a header, and a full stop before the milliseconds.</summary>
    private static string RenderVtt(Transcript transcript)
    {
        var output = new StringBuilder("WEBVTT\n\n");

        foreach (var segment in Ordered(transcript))
        {
            output.Append(Timestamp(segment.StartMs, '.')).Append(" --> ").Append(Timestamp(segment.EndMs, '.'))
                .Append('\n')
                .Append(segment.Text).Append('\n')
                .Append('\n');
        }

        return output.ToString();
    }

    private static string RenderText(Transcript transcript)
    {
        var output = new StringBuilder();

        foreach (var segment in Ordered(transcript))
        {
            output.Append(segment.Text).Append('\n');
        }

        return output.ToString();
    }

    private static string RenderJson(Episode episode, Transcript transcript)
    {
        var payload = new ExportedTranscript
        {
            Episode = new ExportedEpisode
            {
                Id = episode.Id,
                Title = episode.Title,
                Show = episode.Show,
                SourceUrl = episode.SourceUrl,
                DurationSec = episode.DurationSec
            },
            Model = transcript.Model,
            Language = transcript.Language,
            CreatedAt = transcript.CreatedAt,
            Segments = Ordered(transcript).Select(s => new ExportedSegment
            {
                Ordinal = s.Ordinal,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Text = s.Text,
                AvgLogProb = s.AvgLogProb,
                IsEdited = s.IsEdited,
                Words = ParseWords(s.WordsJson)
            }).ToList()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    /// <summary>
    /// Markdown with a timestamp at the start of each line. The timestamps are plain text rather
    /// than links: there is no stable public URL for a self-hosted episode to point at.
    /// </summary>
    private static string RenderMarkdown(Episode episode, Transcript transcript)
    {
        var output = new StringBuilder();

        output.Append("# ").Append(episode.Title).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(episode.Show))
        {
            output.Append("**Show:** ").Append(episode.Show).Append("  \n");
        }

        output.Append("**Model:** ").Append(transcript.Model).Append("  \n");

        if (!string.IsNullOrWhiteSpace(transcript.Language))
        {
            output.Append("**Language:** ").Append(transcript.Language).Append("  \n");
        }

        output.Append("**Transcribed:** ")
            .Append(transcript.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC\n\n---\n\n");

        foreach (var segment in Ordered(transcript))
        {
            output.Append('`').Append(TimeFormat.Clock(segment.StartMs)).Append("` ")
                .Append(segment.Text).Append("\n\n");
        }

        return output.ToString();
    }

    private static IEnumerable<Segment> Ordered(Transcript transcript) =>
        transcript.Segments.OrderBy(s => s.Ordinal);

    private static List<TranscribedWord>? ParseWords(string? wordsJson)
    {
        if (string.IsNullOrWhiteSpace(wordsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<TranscribedWord>>(wordsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>hh:mm:ss followed by the separator the format wants and three-digit milliseconds.</summary>
    private static string Timestamp(int milliseconds, char separator)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));

        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}{separator}{span.Milliseconds:D3}");
    }

    private record ExportedTranscript
    {
        public ExportedEpisode Episode { get; init; } = new();
        public string Model { get; init; } = string.Empty;
        public string? Language { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public List<ExportedSegment> Segments { get; init; } = [];
    }

    private record ExportedEpisode
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Show { get; init; }
        public string? SourceUrl { get; init; }
        public double? DurationSec { get; init; }
    }

    private record ExportedSegment
    {
        public int Ordinal { get; init; }
        public int StartMs { get; init; }
        public int EndMs { get; init; }
        public string Text { get; init; } = string.Empty;
        public double? AvgLogProb { get; init; }
        public bool IsEdited { get; init; }
        public List<TranscribedWord>? Words { get; init; }
    }
}
