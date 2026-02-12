using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RenameSpoolmanSpoolIdToFilamentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SpoolmanSpoolId",
                table: "PrintProjectFiles",
                newName: "SpoolmanFilamentId");

            migrationBuilder.RenameIndex(
                name: "IX_PrintProjectFiles_SpoolmanSpoolId",
                table: "PrintProjectFiles",
                newName: "IX_PrintProjectFiles_SpoolmanFilamentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SpoolmanFilamentId",
                table: "PrintProjectFiles",
                newName: "SpoolmanSpoolId");

            migrationBuilder.RenameIndex(
                name: "IX_PrintProjectFiles_SpoolmanFilamentId",
                table: "PrintProjectFiles",
                newName: "IX_PrintProjectFiles_SpoolmanSpoolId");
        }
    }
}
