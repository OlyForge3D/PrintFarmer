using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSliceJobCalibrationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalibrationMethod",
                table: "SliceJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalibrationParamsJson",
                table: "SliceJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalibrationMethod",
                table: "SliceJobs");

            migrationBuilder.DropColumn(
                name: "CalibrationParamsJson",
                table: "SliceJobs");
        }
    }
}
