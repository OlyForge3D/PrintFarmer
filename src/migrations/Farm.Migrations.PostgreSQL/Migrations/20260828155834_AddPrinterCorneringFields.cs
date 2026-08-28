using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterCorneringFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "JunctionDeviation",
                table: "Printers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxJerk",
                table: "Printers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SquareCornerVelocity",
                table: "Printers",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JunctionDeviation",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "MaxJerk",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "SquareCornerVelocity",
                table: "Printers");
        }
    }
}
