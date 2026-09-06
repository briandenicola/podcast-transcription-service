using System.Globalization;

namespace PodcastTranscription.Web.Services;

public static class TimeFormat
{
    /// <summary>Formats milliseconds as h:mm:ss, dropping the hour when the audio is short.</summary>
    public static string Clock(int milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>
    /// Always hh:mm:ss, unlike <see cref="Clock"/>, which drops the hour on short audio. Used
    /// wherever a timestamp is going to be read back rather than only looked at — the summary
    /// prompts ask the model to cite this shape, and the summary renderer parses it out again.
    /// </summary>
    public static string Timestamp(int milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string Duration(double? seconds) =>
        seconds is > 0 ? Clock((int)(seconds.Value * 1000)) : "—";

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
