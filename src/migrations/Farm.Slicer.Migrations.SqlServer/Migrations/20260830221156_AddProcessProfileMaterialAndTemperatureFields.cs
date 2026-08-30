using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessProfileMaterialAndTemperatureFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BedTemperature",
                schema: "slicer",
                table: "ProcessProfiles",
                type: "int",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<string>(
                name: "Material",
                schema: "slicer",
                table: "ProcessProfiles",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "PLA");

            migrationBuilder.AddColumn<int>(
                name: "NozzleTemperature",
                schema: "slicer",
                table: "ProcessProfiles",
                type: "int",
                nullable: false,
                defaultValue: 210);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BedTemperature",
                schema: "slicer",
                table: "ProcessProfiles");

            migrationBuilder.DropColumn(
                name: "Material",
                schema: "slicer",
                table: "ProcessProfiles");

            migrationBuilder.DropColumn(
                name: "NozzleTemperature",
                schema: "slicer",
                table: "ProcessProfiles");
        }
    }
}
