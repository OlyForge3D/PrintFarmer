using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceColorRequirementWithSpoolmanSpoolId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintProjectFiles_ColorRequirement",
                table: "PrintProjectFiles");

            migrationBuilder.DropColumn(
                name: "ColorRequirement",
                table: "PrintProjectFiles");

            migrationBuilder.AddColumn<int>(
                name: "SpoolmanSpoolId",
                table: "PrintProjectFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_SpoolmanSpoolId",
                table: "PrintProjectFiles",
                column: "SpoolmanSpoolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintProjectFiles_SpoolmanSpoolId",
                table: "PrintProjectFiles");

            migrationBuilder.DropColumn(
                name: "SpoolmanSpoolId",
                table: "PrintProjectFiles");

            migrationBuilder.AddColumn<int>(
                name: "ColorRequirement",
                table: "PrintProjectFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_ColorRequirement",
                table: "PrintProjectFiles",
                column: "ColorRequirement");
        }
    }
}
