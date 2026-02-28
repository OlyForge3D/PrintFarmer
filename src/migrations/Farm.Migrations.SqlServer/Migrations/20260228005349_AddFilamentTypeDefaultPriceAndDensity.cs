using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentTypeDefaultPriceAndDensity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DefaultDensity",
                table: "FilamentTypes",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DefaultPricePerKg",
                table: "FilamentTypes",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultDensity",
                table: "FilamentTypes");

            migrationBuilder.DropColumn(
                name: "DefaultPricePerKg",
                table: "FilamentTypes");
        }
    }
}
