using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddWattageToEntities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "Wattage",
            table: "Printers",
            type: "numeric",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "DefaultWattage",
            table: "PrinterModels",
            type: "numeric",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Wattage",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "DefaultWattage",
            table: "PrinterModels");
    }
}
