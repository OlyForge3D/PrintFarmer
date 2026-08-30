using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessProfileMaterialAndTemperatureFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BedTemperature",
                table: "ProcessProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Material",
                table: "ProcessProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<int>(
                name: "NozzleTemperature",
                table: "ProcessProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BedTemperature",
                table: "ProcessProfiles");

            migrationBuilder.DropColumn(
                name: "Material",
                table: "ProcessProfiles");

            migrationBuilder.DropColumn(
                name: "NozzleTemperature",
                table: "ProcessProfiles");
        }
    }
}
