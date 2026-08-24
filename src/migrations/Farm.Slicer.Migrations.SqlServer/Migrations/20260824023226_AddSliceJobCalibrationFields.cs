using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSliceJobCalibrationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalibrationMethod",
                schema: "slicer",
                table: "SliceJobs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalibrationParamsJson",
                schema: "slicer",
                table: "SliceJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalibrationMethod",
                schema: "slicer",
                table: "SliceJobs");

            migrationBuilder.DropColumn(
                name: "CalibrationParamsJson",
                schema: "slicer",
                table: "SliceJobs");
        }
    }
}
