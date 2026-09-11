using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgAutoposter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCollectedAtUtc",
                table: "Sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCollectedCount",
                table: "Sources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Author",
                table: "SourceCandidates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsumedReason",
                table: "SourceCandidates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "SourceCandidates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceCandidates_ChannelId_IsConsumed_FoundAtUtc",
                table: "SourceCandidates",
                columns: new[] { "ChannelId", "IsConsumed", "FoundAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceCandidates_ChannelId_IsConsumed_FoundAtUtc",
                table: "SourceCandidates");

            migrationBuilder.DropColumn(
                name: "LastCollectedAtUtc",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "LastCollectedCount",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Author",
                table: "SourceCandidates");

            migrationBuilder.DropColumn(
                name: "ConsumedReason",
                table: "SourceCandidates");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "SourceCandidates");
        }
    }
}
