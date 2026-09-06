using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastTranscription.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeedIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_FeedId",
                table: "Episodes");

            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Feeds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedItemGuid",
                table: "Episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_FeedId_FeedItemGuid",
                table: "Episodes",
                columns: new[] { "FeedId", "FeedItemGuid" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_FeedId_FeedItemGuid",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "FeedItemGuid",
                table: "Episodes");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_FeedId",
                table: "Episodes",
                column: "FeedId");
        }
    }
}
