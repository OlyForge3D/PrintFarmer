using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterModelAccelerationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxAcceleration",
                table: "PrinterModels",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxTravelAcceleration",
                table: "PrinterModels",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxAcceleration",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "MaxTravelAcceleration",
                table: "PrinterModels");
        }
    }
}
