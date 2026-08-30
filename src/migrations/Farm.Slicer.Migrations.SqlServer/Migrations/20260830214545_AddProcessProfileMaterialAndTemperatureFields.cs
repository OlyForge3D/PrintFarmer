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
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Material",
                schema: "slicer",
                table: "ProcessProfiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<int>(
                name: "NozzleTemperature",
                schema: "slicer",
                table: "ProcessProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);
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
