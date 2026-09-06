using System.Text.Json;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Export;

namespace PodcastTranscription.Tests;

public class TranscriptExporterTests
{
    private static readonly Episode Episode = new()
    {
        Id = 7,
        Title = "The Rest Is History — Ep. 412",
        Show = "The Rest Is History",
        DurationSec = 3723.5
    };

    private static Transcript BuildTranscript() => new()
    {
        Id = 3,
        EpisodeId = 7,
        Model = "large-v3-turbo-q5_0",
        Language = "en",
        CreatedAt = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero),
        Segments =
        [
            new Segment
            {
                Ordinal = 0, StartMs = 0, EndMs = 2_240, Text = "Hello there.", AvgLogProb = -0.21,
                WordsJson = """[{"t0":0,"t1":900,"text":"Hello","p":0.9},{"t0":900,"t1":2240,"text":"there.","p":0.8}]"""
            },
            // Deliberately out of order, to prove the exporters sort by ordinal.
            new Segment { Ordinal = 2, StartMs = 3_661_500, EndMs = 3_665_000, Text = "Much later." },
            new Segment { Ordinal = 1, StartMs = 2_240, EndMs = 5_500, Text = "General Kenobi." }
        ]
    };

    [Fact]
    public void Srt_numbers_cues_from_one_and_uses_a_comma_before_milliseconds()
    {
        var srt = TranscriptExporter.Render(ExportFormat.Srt, Episode, BuildTranscript());

        Assert.StartsWith("1\n00:00:00,000 --> 00:00:02,240\nHello there.\n", srt);
        Assert.Contains("2\n00:00:02,240 --> 00:00:05,500\nGeneral Kenobi.", srt);

        // Past an hour, so it also covers the hours field.
        Assert.Contains("3\n01:01:01,500 --> 01:01:05,000\nMuch later.", srt);
    }

    [Fact]
    public void Vtt_has_the_header_and_a_full_stop_before_milliseconds()
    {
        var vtt = TranscriptExporter.Render(ExportFormat.Vtt, Episode, BuildTranscript());

        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:00.000 --> 00:00:02.240", vtt);
        Assert.DoesNotContain(",240", vtt);
    }

    [Fact]
    public void Text_is_the_lines_in_order_with_no_timestamps()
    {
        var text = TranscriptExporter.Render(ExportFormat.Txt, Episode, BuildTranscript());

        Assert.Equal("Hello there.\nGeneral Kenobi.\nMuch later.\n", text);
    }

    [Fact]
    public void Json_carries_the_episode_the_segments_and_the_words()
    {
        var json = TranscriptExporter.Render(ExportFormat.Json, Episode, BuildTranscript());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("The Rest Is History — Ep. 412", root.GetProperty("Episode").GetProperty("Title").GetString());
        Assert.Equal("large-v3-turbo-q5_0", root.GetProperty("Model").GetString());

        var segments = root.GetProperty("Segments");
        Assert.Equal(3, segments.GetArrayLength());
        Assert.Equal("Hello there.", segments[0].GetProperty("Text").GetString());
        Assert.Equal(2, segments[0].GetProperty("Words").GetArrayLength());
        Assert.Equal("Hello", segments[0].GetProperty("Words")[0].GetProperty("text").GetString());

        // A segment without word timings omits the property rather than emitting null.
        Assert.False(segments[1].TryGetProperty("Words", out _));
    }

    [Fact]
    public void Markdown_leads_with_the_episode_and_timestamps_every_line()
    {
        var markdown = TranscriptExporter.Render(ExportFormat.Markdown, Episode, BuildTranscript());

        Assert.StartsWith("# The Rest Is History — Ep. 412\n\n", markdown);
        Assert.Contains("**Show:** The Rest Is History", markdown);
        Assert.Contains("**Model:** large-v3-turbo-q5_0", markdown);
        Assert.Contains("`0:00` Hello there.", markdown);
        Assert.Contains("`1:01:01` Much later.", markdown);
    }

    [Fact]
    public void An_empty_transcript_still_produces_a_valid_file()
    {
        var empty = new Transcript { Model = "tiny", Segments = [] };

        Assert.Equal(string.Empty, TranscriptExporter.Render(ExportFormat.Srt, Episode, empty));
        Assert.Equal("WEBVTT\n\n", TranscriptExporter.Render(ExportFormat.Vtt, Episode, empty));
        Assert.Equal(string.Empty, TranscriptExporter.Render(ExportFormat.Txt, Episode, empty));
    }

    [Fact]
    public void Malformed_words_json_does_not_break_the_json_export()
    {
        var transcript = new Transcript
        {
            Model = "tiny",
            Segments = [new Segment { Ordinal = 0, Text = "Hello.", WordsJson = "{not json" }]
        };

        var json = TranscriptExporter.Render(ExportFormat.Json, Episode, transcript);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("Segments")[0].TryGetProperty("Words", out _));
    }

    [Theory]
    [InlineData("srt", ExportFormat.Srt)]
    [InlineData("VTT", ExportFormat.Vtt)]
    [InlineData("text", ExportFormat.Txt)]
    [InlineData("markdown", ExportFormat.Markdown)]
    [InlineData("md", ExportFormat.Markdown)]
    public void Format_names_are_parsed_case_insensitively(string input, ExportFormat expected)
    {
        Assert.True(TranscriptExporter.TryParseFormat(input, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_format_is_rejected(string? input) =>
        Assert.False(TranscriptExporter.TryParseFormat(input, out _));
}
