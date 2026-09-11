using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgAutoposter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoriesAndDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingJson",
                table: "SourceCandidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StoryId",
                table: "SourceCandidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DigestEnabled",
                table: "Channels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "DigestMaxDrafts",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "DigestMaxStories",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "DigestTimeLocal",
                table: "Channels",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(20, 0, 0));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDigestAtUtc",
                table: "Channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CandidatesCount = table.Column<int>(type: "integer", nullable: false),
                    SourcesCount = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    IsBreaking = table.Column<bool>(type: "boolean", nullable: false),
                    KindHint = table.Column<string>(type: "text", nullable: true),
                    EmbeddingJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LeadCandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostId = table.Column<Guid>(type: "uuid", nullable: true),
                    DigestPostId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Stories_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCandidates_StoryId",
                table: "SourceCandidates",
                column: "StoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Stories_ChannelId_Status_LastSeenAtUtc",
                table: "Stories",
                columns: new[] { "ChannelId", "Status", "LastSeenAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_SourceCandidates_Stories_StoryId",
                table: "SourceCandidates",
                column: "StoryId",
                principalTable: "Stories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceCandidates_Stories_StoryId",
                table: "SourceCandidates");

            migrationBuilder.DropTable(
                name: "Stories");

            migrationBuilder.DropIndex(
                name: "IX_SourceCandidates_StoryId",
                table: "SourceCandidates");

            migrationBuilder.DropColumn(
                name: "EmbeddingJson",
                table: "SourceCandidates");

            migrationBuilder.DropColumn(
                name: "StoryId",
                table: "SourceCandidates");

            migrationBuilder.DropColumn(
                name: "DigestEnabled",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "DigestMaxDrafts",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "DigestMaxStories",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "DigestTimeLocal",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LastDigestAtUtc",
                table: "Channels");
        }
    }
}
