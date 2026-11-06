using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddObicoPrintRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ObicoFallbackUrl",
                table: "PrinterServiceState",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObicoPrintKey",
                table: "PrinterServiceState",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ObicoFallbackUrl",
                table: "PrinterServiceState");

            migrationBuilder.DropColumn(
                name: "ObicoPrintKey",
                table: "PrinterServiceState");
        }
    }
}
