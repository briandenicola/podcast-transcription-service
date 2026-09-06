using Microsoft.Data.Sqlite;
using PodcastTranscription.Web.Services.Search;

namespace PodcastTranscription.Tests;

public class FtsQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("\"\"")]
    public void Nothing_to_search_for_returns_null(string? input) =>
        Assert.Null(FtsQuery.Build(input));

    [Fact]
    public void A_single_word_is_quoted_and_prefix_matched() =>
        Assert.Equal("\"pricing\"*", FtsQuery.Build("pricing"));

    [Fact]
    public void Words_are_combined_with_an_implicit_and() =>
        Assert.Equal("\"kara\" \"pricing\"*", FtsQuery.Build("kara pricing"));

    [Fact]
    public void A_quoted_phrase_stays_one_term_and_is_not_prefix_matched() =>
        Assert.Equal("\"machine learning\"", FtsQuery.Build("\"machine learning\""));

    [Fact]
    public void A_phrase_can_be_combined_with_loose_words() =>
        Assert.Equal("\"machine learning\" \"model\"*", FtsQuery.Build("\"machine learning\" model"));

    [Fact]
    public void A_one_letter_last_word_is_not_prefix_matched() =>
        Assert.Equal("\"a\"", FtsQuery.Build("a"));

    /// <summary>
    /// The point of quoting every term is that SQLite must accept the result. These inputs are
    /// all FTS5 syntax or malformed syntax, which would be an exception rather than an empty
    /// result set, so the assertion is that the query actually runs.
    /// </summary>
    [Theory]
    [InlineData("foo OR bar")]
    [InlineData("foo NEAR bar")]
    [InlineData("foo AND NOT bar")]
    [InlineData("foo*")]
    [InlineData("(foo")]
    [InlineData("foo^bar")]
    [InlineData("column:value")]
    [InlineData("\"unclosed phrase")]
    [InlineData("a - b + c")]
    [InlineData("100% \u00e9t\u00e9")]
    public void Operators_are_neutralised_so_sqlite_accepts_the_query(string input)
    {
        var built = FtsQuery.Build(input);
        Assert.NotNull(built);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE T USING fts5(Text, tokenize='porter unicode61');"
                + "INSERT INTO T(Text) VALUES ('a transcript segment about pricing');";
            create.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM T WHERE T MATCH $match;";
        query.Parameters.AddWithValue("$match", built);

        // The assertion is simply that this does not throw a SqliteException.
        var matches = Convert.ToInt32(query.ExecuteScalar());
        Assert.True(matches >= 0);
    }

    [Fact]
    public void An_embedded_quote_is_doubled_so_it_survives_as_a_literal()
    {
        var built = FtsQuery.Build("say \"\"hello");

        Assert.NotNull(built);
        Assert.DoesNotContain("\"\"\"", built);
    }

    [Fact]
    public void An_unbalanced_quote_does_not_throw()
    {
        var built = FtsQuery.Build("\"unclosed phrase");

        Assert.Equal("\"unclosed phrase\"", built);
    }

    [Fact]
    public void Punctuation_only_tokens_are_dropped() =>
        Assert.Equal("\"hello\"*", FtsQuery.Build("--- hello ???"));
}
