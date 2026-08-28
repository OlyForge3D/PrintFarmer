using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
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
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxJerk",
                table: "Printers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SquareCornerVelocity",
                table: "Printers",
                type: "float",
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
