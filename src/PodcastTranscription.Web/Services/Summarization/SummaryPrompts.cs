using System.Text;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// The instructions handed to the model. Kept together and out of the service so they can be
/// read as prose and changed without touching the orchestration around them.
///
/// The brief throughout is the one the summary is actually for: not "what topics came up" but
/// what was argued, by whom, and what they disagreed about. A model asked for a neutral summary
/// of an argument reliably flattens it into a topic list, so the prompts say so repeatedly.
/// </summary>
public static class SummaryPrompts
{
    public const string System =
        "You summarise podcast transcripts for someone deciding whether to listen and for someone "
        + "who listened and wants the argument back. You are precise, concrete and neutral about "
        + "the speakers even when they are not neutral about each other.\n\n"
        + "Rules you never break:\n"
        + "- Use only what is in the transcript. If something is not there, leave it out. Never "
        + "invent names, numbers, dates or quotations.\n"
        + "- The transcript is machine-generated, so names and jargon may be misspelled and "
        + "speakers are not labelled. Infer who is speaking only when the text makes it obvious, "
        + "and say \"a speaker\" when it does not.\n"
        + "- Attribute positions to whoever argued them. A summary that says \"they discussed X\" "
        + "when one speaker attacked X and another defended it has lost the episode.\n"
        + "- Write plain prose. No preamble, no sign-off, no \"in this episode\".";

    /// <summary>
    /// The window pass. Notes, not a summary: this output is raw material for the pass that
    /// follows, so it keeps detail a finished summary would drop.
    /// </summary>
    public static string MapPrompt(string window, int index, int total) =>
        $"""
         These are notes-in-progress on part {index + 1} of {total} of a podcast transcript.
         Timestamps are in [hh:mm:ss].

         Write dense notes on this part alone. Do not summarise, do not smooth it out, and do not
         refer to parts you have not seen. Capture:

         - The subjects actually under discussion, in the order they come up.
         - Every position a speaker takes, and any reasoning or evidence they give for it.
         - Disagreements: who is on which side and where they part company.
         - Specific claims, numbers, names, anecdotes and examples, with a [hh:mm:ss] against each.

         Bullet points. No introduction and no conclusion.

         TRANSCRIPT PART {index + 1} OF {total}:
         {window}
         """;

    /// <summary>
    /// Folds a batch of window notes into one set. Only used when there are too many notes to
    /// fit the final pass, which a very long episode on a small context window will reach.
    /// </summary>
    public static string FoldPrompt(string notes) =>
        $"""
         These are notes on consecutive parts of one podcast transcript.

         Merge them into a single set of notes covering the same ground, in the same order. Keep
         every position, disagreement, claim and [hh:mm:ss] timestamp. Combine points that repeat
         across parts rather than listing them twice. Do not summarise and do not shorten by
         dropping content — this is still raw material, not a finished summary.

         NOTES:
         {notes}
         """;

    /// <summary>The final pass, over either the whole transcript or the accumulated notes.</summary>
    public static string ReducePrompt(string episodeTitle, string? show, string body, bool bodyIsNotes)
    {
        var source = bodyIsNotes
            ? "notes taken over the whole of a podcast episode, in order"
            : "the transcript of a podcast episode, with timestamps in [hh:mm:ss]";

        var header = new StringBuilder();
        header.Append("Episode: ").Append(episodeTitle);
        if (!string.IsNullOrWhiteSpace(show))
        {
            header.Append("\nShow: ").Append(show);
        }

        return $"""
                Below is {source}.

                {header}

                Write the episode summary in Markdown, using exactly these five headings and
                nothing else. No title, no preamble before the first heading.

                ## Overview
                Two or three sentences: what this episode is and what it is really about. Someone
                who reads only this should know whether they want it.

                ## What they discuss
                The subjects covered, in order, one bullet each, with a [hh:mm:ss] against each so
                a reader can jump to it. Enough detail to be worth having — "the state of the
                housing market" is useless, "why they think the 2021 rate cuts caused it" is not.

                ## Points of view
                The heart of it. Each distinct position argued in the episode, who argued it, and
                what they based it on. Where speakers disagree, say so and say where exactly the
                disagreement is. Where they all agree on something contestable, say that too. If a
                speaker changes their mind, that is worth a bullet on its own.

                ## Notable claims and moments
                Specific claims, figures, predictions, anecdotes and stories worth remembering,
                each with a [hh:mm:ss]. Attribute each to whoever said it. Leave this out entirely
                if there is nothing that qualifies.

                ## Takeaways
                Three to five bullets: what a listener actually leaves with.

                SOURCE:
                {body}
                """;
    }

    /// <summary>Appends the operator's own steer, when <c>Ollama__ExtraInstructions</c> is set.</summary>
    public static string WithExtra(string system, string? extra) =>
        string.IsNullOrWhiteSpace(extra) ? system : system + "\n\nAdditional instructions:\n" + extra.Trim();
}
