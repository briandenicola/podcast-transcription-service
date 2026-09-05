namespace PodcastTranscription.Web.Domain;

public class Segment
{
    public int Id { get; set; }

    public int TranscriptId { get; set; }
    public Transcript? Transcript { get; set; }

    public int Ordinal { get; set; }

    /// <summary>Milliseconds from the start of the episode. Integers, never floats — every export and seek gets easier.</summary>
    public int StartMs { get; set; }
    public int EndMs { get; set; }

    public string Text { get; set; } = string.Empty;
    public double? AvgLogProb { get; set; }

    /// <summary>Words as JSON: [{ "t0": ms, "t1": ms, "text": "...", "p": 0.0-1.0 }]. Populated from M2.</summary>
    public string? WordsJson { get; set; }

    public bool IsEdited { get; set; }

    /// <summary>Unused until diarization (§7.3). Present so the schema does not have to change then.</summary>
    public int? SpeakerId { get; set; }
}
