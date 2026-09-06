using System.Text;

namespace PodcastTranscription.Web.Services.Search;

/// <summary>
/// Turns whatever someone typed into a safe FTS5 MATCH expression.
///
/// FTS5 has its own query syntax, and raw input hits it hard: an unbalanced quote, a bare
/// <c>NEAR</c>, or a stray <c>*</c> is a syntax error rather than a search. Every token is
/// therefore quoted, which makes it a literal, and the operators are dropped.
/// </summary>
public static class FtsQuery
{
    /// <summary>
    /// Null when there is nothing to search for — the caller should show the empty state rather
    /// than run a query.
    /// </summary>
    public static string? Build(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var terms = Tokenize(input);
        if (terms.Count == 0)
        {
            return null;
        }

        var query = new StringBuilder();

        for (var i = 0; i < terms.Count; i++)
        {
            if (i > 0)
            {
                query.Append(' '); // implicit AND
            }

            // Doubling embedded quotes is how a literal quote survives inside an FTS5 string.
            query.Append('"').Append(terms[i].Text.Replace("\"", "\"\"")).Append('"');

            // The last word of a search is usually still being typed, so match it as a prefix.
            if (i == terms.Count - 1 && !terms[i].WasQuoted && terms[i].Text.Length >= 2)
            {
                query.Append('*');
            }
        }

        return query.ToString();
    }

    /// <summary>Splits on whitespace, but keeps "quoted phrases" together as one term.</summary>
    private static List<(string Text, bool WasQuoted)> Tokenize(string input)
    {
        var terms = new List<(string, bool)>();
        var current = new StringBuilder();
        var inQuotes = false;

        void Flush(bool wasQuoted)
        {
            var text = current.ToString().Trim();
            current.Clear();

            // Anything that is only punctuation carries no search meaning.
            if (text.Length > 0 && text.Any(char.IsLetterOrDigit))
            {
                terms.Add((text, wasQuoted));
            }
        }

        foreach (var c in input)
        {
            if (c == '"')
            {
                Flush(inQuotes);
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                Flush(false);
                continue;
            }

            current.Append(c);
        }

        Flush(inQuotes);
        return terms;
    }
}
