using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPrinterControlMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrinterControlMode",
                table: "UserSettings",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Guided");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrinterControlMode",
                table: "UserSettings");
        }
    }
}
