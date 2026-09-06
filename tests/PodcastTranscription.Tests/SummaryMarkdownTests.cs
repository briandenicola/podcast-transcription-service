using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

public class SummaryMarkdownTests
{
    [Fact]
    public void Headings_are_pushed_below_the_page_heading_levels()
    {
        var html = SummaryMarkdown.ToHtml("## Overview\n### Detail");

        Assert.Contains("<h3>Overview</h3>", html);
        Assert.Contains("<h4>Detail</h4>", html);
    }

    [Fact]
    public void Bullets_become_one_list_rather_than_one_list_per_bullet()
    {
        var html = SummaryMarkdown.ToHtml("- first\n- second\n- third");

        Assert.Equal(1, CountOf(html, "<ul>"));
        Assert.Equal(3, CountOf(html, "<li>"));
    }

    [Fact]
    public void A_blank_line_closes_the_list_so_the_next_one_starts_fresh()
    {
        var html = SummaryMarkdown.ToHtml("- a\n\n- b");

        Assert.Equal(2, CountOf(html, "<ul>"));
    }

    [Fact]
    public void Numbered_items_become_an_ordered_list()
    {
        var html = SummaryMarkdown.ToHtml("1. first\n2. second");

        Assert.Contains("<ol>", html);
        Assert.Equal(2, CountOf(html, "<li>"));
    }

    [Fact]
    public void Emphasis_and_code_spans_are_rendered()
    {
        var html = SummaryMarkdown.ToHtml("A **strong** and an *aside* and `code`.");

        Assert.Contains("<strong>strong</strong>", html);
        Assert.Contains("<em>aside</em>", html);
        Assert.Contains("<code>code</code>", html);
    }

    [Fact]
    public void Bold_is_not_re_read_as_two_italics()
    {
        var html = SummaryMarkdown.ToHtml("**both words**");

        Assert.Contains("<strong>both words</strong>", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Timestamps_become_cues_carrying_their_offset_in_milliseconds()
    {
        var html = SummaryMarkdown.ToHtml("- They disagree here [01:02:03].");

        Assert.Contains("data-t0=\"3723000\"", html);
        Assert.Contains("summary-cue", html);
    }

    [Fact]
    public void A_timestamp_without_an_hour_is_read_as_minutes_and_seconds()
    {
        var html = SummaryMarkdown.ToHtml("Mentioned at [12:30].");

        Assert.Contains("data-t0=\"750000\"", html);
    }

    [Fact]
    public void Model_output_cannot_smuggle_markup_through()
    {
        // The model is echoing a transcript it did not write; nothing in either is trusted.
        var html = SummaryMarkdown.ToHtml("- <script>alert('x')</script> and <img src=x onerror=y>");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void An_html_attribute_in_the_text_cannot_break_out_of_a_cue()
    {
        var html = SummaryMarkdown.ToHtml("""He said "quote" [00:01:00].""");

        Assert.Contains("&quot;quote&quot;", html);
        Assert.Contains("<a class=\"summary-cue\" role=\"button\" tabindex=\"0\" data-t0=\"60000\">", html);
    }

    [Fact]
    public void Consecutive_prose_lines_join_into_one_paragraph()
    {
        var html = SummaryMarkdown.ToHtml("One sentence.\nAnother sentence.\n\nA new paragraph.");

        Assert.Equal(2, CountOf(html, "<p>"));
        Assert.Contains("<p>One sentence. Another sentence.</p>", html);
    }

    [Fact]
    public void A_wrapped_bullet_stays_inside_its_list_item()
    {
        // Models hard-wrap. Without lazy continuation the second line escapes the list and
        // lands against the left margin as a paragraph of its own.
        var html = SummaryMarkdown.ToHtml("- The host argues the cuts are the cause: cheap\ncredit chasing a fixed stock [00:04:02].");

        Assert.Equal(1, CountOf(html, "<li>"));
        Assert.DoesNotContain("<p>", html);
        Assert.Contains("cheap credit chasing a fixed stock", html);
    }

    [Fact]
    public void A_wrapped_line_still_gets_its_timestamp_cue()
    {
        var html = SummaryMarkdown.ToHtml("- Something happened\nlater on [00:04:02].");

        Assert.Contains("data-t0=\"242000\"", html);
    }

    [Fact]
    public void A_horizontal_rule_is_dropped_rather_than_read_as_a_bullet()
    {
        var html = SummaryMarkdown.ToHtml("## Head\n\n---\n\nText.");

        Assert.DoesNotContain("<li>", html);
        Assert.DoesNotContain("<hr", html);
        Assert.Contains("<p>Text.</p>", html);
    }

    [Fact]
    public void Prose_after_a_blank_line_is_a_paragraph_again_rather_than_more_of_the_bullet()
    {
        var html = SummaryMarkdown.ToHtml("- a bullet\n\nA new paragraph.");

        Assert.Contains("<li>a bullet</li>", html);
        Assert.Contains("<p>A new paragraph.</p>", html);
    }

    [Fact]
    public void Empty_input_renders_nothing() => Assert.Equal(string.Empty, SummaryMarkdown.ToHtml("   "));

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
