using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgAutoposter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEditorialAndHeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EditorCandidatesCount",
                table: "Stories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditorCheckedAtUtc",
                table: "Stories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditorNote",
                table: "Stories",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditorRubric",
                table: "Stories",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EditorScore",
                table: "Stories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Headline",
                table: "Posts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rubric",
                table: "Posts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditorCandidatesCount",
                table: "Stories");

            migrationBuilder.DropColumn(
                name: "EditorCheckedAtUtc",
                table: "Stories");

            migrationBuilder.DropColumn(
                name: "EditorNote",
                table: "Stories");

            migrationBuilder.DropColumn(
                name: "EditorRubric",
                table: "Stories");

            migrationBuilder.DropColumn(
                name: "EditorScore",
                table: "Stories");

            migrationBuilder.DropColumn(
                name: "Headline",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "Rubric",
                table: "Posts");
        }
    }
}
