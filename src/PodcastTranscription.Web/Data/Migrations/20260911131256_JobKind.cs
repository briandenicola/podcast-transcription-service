using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastTranscription.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Jobs");
        }
    }
}
