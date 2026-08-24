using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RenameCalibrationHasHeatedChamberToHasHeatedChamber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CalibrationHasHeatedChamber",
                table: "Printers",
                newName: "HasHeatedChamber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "HasHeatedChamber",
                table: "Printers",
                newName: "CalibrationHasHeatedChamber");
        }
    }
}
