using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgAutoposter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceKindsAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LastError",
                table: "Sources",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireNewsSignal",
                table: "Sources",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "Sources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireNewsSignal",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "Sources");

            migrationBuilder.AlterColumn<string>(
                name: "LastError",
                table: "Sources",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);
        }
    }
}
