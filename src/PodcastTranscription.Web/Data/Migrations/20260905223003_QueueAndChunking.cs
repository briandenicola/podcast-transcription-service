using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastTranscription.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueueAndChunking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RawJson",
                table: "Transcripts");

            migrationBuilder.AddColumn<long>(
                name: "CompletedAt",
                table: "Transcripts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsComplete",
                table: "Transcripts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ChunkPlanJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletedChunks",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAt",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ProcessedAudioSec",
                table: "Jobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "TotalChunks",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptId",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TranscriptChunks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TranscriptId = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    StartMs = table.Column<int>(type: "INTEGER", nullable: false),
                    EndMs = table.Column<int>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscriptChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranscriptChunks_Transcripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "Transcripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TranscriptId",
                table: "Jobs",
                column: "TranscriptId");

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptChunks_TranscriptId_Index",
                table: "TranscriptChunks",
                columns: new[] { "TranscriptId", "Index" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Transcripts_TranscriptId",
                table: "Jobs",
                column: "TranscriptId",
                principalTable: "Transcripts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Transcripts_TranscriptId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "TranscriptChunks");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TranscriptId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "IsComplete",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "ChunkPlanJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CompletedChunks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ProcessedAudioSec",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TotalChunks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TranscriptId",
                table: "Jobs");

            migrationBuilder.AddColumn<string>(
                name: "RawJson",
                table: "Transcripts",
                type: "TEXT",
                nullable: true);
        }
    }
}
