using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSliceJobLayoutDegradationReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LayoutDegradationReason",
                table: "SliceJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LayoutDegradationReason",
                table: "SliceJobs");
        }
    }
}
