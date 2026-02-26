using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterModelFeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasBowdenTube",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasCarbonFilter",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasFilamentCutter",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasHeatedChamber",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasHepaFilter",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasLeadScrews",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasLinearRails",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasPtfeLiner",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasToolchanger",
                table: "PrinterModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasBowdenTube",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasCarbonFilter",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasFilamentCutter",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasHeatedChamber",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasHepaFilter",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasLeadScrews",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasLinearRails",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasPtfeLiner",
                table: "PrinterModels");

            migrationBuilder.DropColumn(
                name: "HasToolchanger",
                table: "PrinterModels");
        }
    }
}
