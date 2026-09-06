using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastTranscription.Web.Data.Migrations
{
    /// <summary>
    /// FTS5 over segment text. An external-content table indexes <c>Segments.Text</c> without
    /// duplicating it, and triggers keep the index in step with every insert, update and delete —
    /// including the ones EF makes, since they run inside SQLite rather than in application code.
    /// </summary>
    public partial class SegmentFullTextSearch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 'porter' stems, so a search for "running" also finds "run".
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE SegmentSearch USING fts5(
                    Text,
                    content='Segments',
                    content_rowid='Id',
                    tokenize='porter unicode61'
                );
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER Segments_ai AFTER INSERT ON Segments BEGIN
                    INSERT INTO SegmentSearch(rowid, Text) VALUES (new.Id, new.Text);
                END;
                """);

            // External-content tables need the old value pushed in as a 'delete' row before the
            // new one goes in, or the index keeps stale terms.
            migrationBuilder.Sql("""
                CREATE TRIGGER Segments_ad AFTER DELETE ON Segments BEGIN
                    INSERT INTO SegmentSearch(SegmentSearch, rowid, Text) VALUES ('delete', old.Id, old.Text);
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER Segments_au AFTER UPDATE ON Segments BEGIN
                    INSERT INTO SegmentSearch(SegmentSearch, rowid, Text) VALUES ('delete', old.Id, old.Text);
                    INSERT INTO SegmentSearch(rowid, Text) VALUES (new.Id, new.Text);
                END;
                """);

            // Index whatever was transcribed before this migration ran.
            migrationBuilder.Sql("INSERT INTO SegmentSearch(SegmentSearch) VALUES ('rebuild');");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Segments_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Segments_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Segments_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS SegmentSearch;");
        }
    }
}
