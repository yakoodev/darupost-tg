using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgAutoposter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTalents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TalentsCsv",
                table: "Stories",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Talents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Agency = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Group = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AliasesCsv = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    YouTube = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Twitter = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Telegram = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrackYouTube = table.Column<bool>(type: "boolean", nullable: false),
                    TrackTwitter = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Talents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Talents_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Talents_ChannelId_Name",
                table: "Talents",
                columns: new[] { "ChannelId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Talents");

            migrationBuilder.DropColumn(
                name: "TalentsCsv",
                table: "Stories");
        }
    }
}
