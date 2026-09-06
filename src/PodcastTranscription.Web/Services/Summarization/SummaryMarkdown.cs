using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// Renders the Markdown a local model produced into the small subset of HTML this page needs.
///
/// A full Markdown library is a dependency and an attack surface for one card of text, and the
/// text comes from a model rather than from a person, so it is HTML-encoded before anything else
/// happens: whatever the transcript contained, and whatever the model decided to echo back out
/// of it, cannot become markup. Everything below builds tags out of already-safe text.
/// </summary>
public static partial class SummaryMarkdown
{
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        var paragraph = new List<string>();

        // The open list, held as items rather than written straight out, so a wrapped line can
        // still be appended to the item above it.
        var listType = (string?)null;
        var items = new List<string>();

        void CloseParagraph()
        {
            if (paragraph.Count > 0)
            {
                html.Append("<p>").Append(string.Join(" ", paragraph)).Append("</p>");
                paragraph.Clear();
            }
        }

        void CloseList()
        {
            if (listType is null)
            {
                return;
            }

            html.Append('<').Append(listType).Append('>');
            foreach (var item in items)
            {
                html.Append("<li>").Append(item).Append("</li>");
            }

            html.Append("</").Append(listType).Append('>');

            listType = null;
            items.Clear();
        }

        void OpenList(string type)
        {
            if (listType != type)
            {
                CloseList();
                listType = type;
            }
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                CloseParagraph();
                CloseList();
                continue;
            }

            if (HeadingPattern().Match(trimmed) is { Success: true } heading)
            {
                CloseParagraph();
                CloseList();

                // The page already owns h1 and h2, so markdown's own levels are pushed down to
                // sit under them rather than competing with the episode title.
                var level = Math.Clamp(heading.Groups[1].Value.Length + 1, 3, 6);
                html.Append("<h").Append(level).Append('>')
                    .Append(Inline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append('>');
                continue;
            }

            // A horizontal rule would only draw a line across the card; the headings already
            // separate the sections. Checked before the bullet pattern, which "---" also matches.
            if (RulePattern().IsMatch(trimmed))
            {
                CloseParagraph();
                CloseList();
                continue;
            }

            if (BulletPattern().Match(trimmed) is { Success: true } bullet)
            {
                CloseParagraph();
                OpenList("ul");
                items.Add(Inline(bullet.Groups[1].Value));
                continue;
            }

            if (NumberedPattern().Match(trimmed) is { Success: true } numbered)
            {
                CloseParagraph();
                OpenList("ol");
                items.Add(Inline(numbered.Groups[1].Value));
                continue;
            }

            // A plain line under an open bullet is that bullet continuing. Models hard-wrap their
            // output, so without this every wrapped line would break out of the list and land
            // against the left margin as a paragraph of its own.
            if (listType is not null && items.Count > 0)
            {
                items[^1] += " " + Inline(trimmed);
                continue;
            }

            paragraph.Add(Inline(trimmed));
        }

        CloseParagraph();
        CloseList();

        return html.ToString();
    }

    /// <summary>
    /// Encodes, then applies emphasis, code spans and timestamp cues. Encoding first is the whole
    /// safety story: every tag emitted after this point is one this method wrote.
    /// </summary>
    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);

        encoded = CodePattern().Replace(encoded, "<code>$1</code>");
        encoded = BoldPattern().Replace(encoded, "<strong>$1</strong>");
        encoded = ItalicPattern().Replace(encoded, "<em>$1</em>");

        // The prompts ask for [hh:mm:ss] against every claim precisely so this can happen: each
        // one becomes a cue that seeks the player, which is the difference between a summary you
        // read and a summary you navigate the episode with.
        encoded = CuePattern().Replace(encoded, match =>
        {
            var hours = match.Groups[1].Success
                ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0;
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var ms = ((hours * 3600) + (minutes * 60) + seconds) * 1000;

            return $"""<a class="summary-cue" role="button" tabindex="0" data-t0="{ms}">{match.Value}</a>""";
        });

        return encoded;
    }

    [GeneratedRegex(@"^(#{1,4})\s+(.+)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^[-*+]\s+(.+)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^\d+[.)]\s+(.+)$")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"^([-*_])\1{2,}$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldPattern();

    // Single asterisks only, and never the ones bold already consumed — by this point those are
    // inside <strong> tags, so the pattern refuses any run touching another asterisk.
    [GeneratedRegex(@"(?<![*\w])\*(?!\s)([^*\n]+?)(?<!\s)\*(?![*\w])")]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"\[(?:(\d{1,2}):)?([0-5]?\d):([0-5]\d)\]")]
    private static partial Regex CuePattern();
}
