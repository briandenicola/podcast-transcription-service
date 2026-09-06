using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastTranscription.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TranscriptQualityWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualityWarning",
                table: "Transcripts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualityWarning",
                table: "Transcripts");
        }
    }
}
