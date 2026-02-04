using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterDigestAuthCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Password",
                table: "Printers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "Printers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Password",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "Printers");
        }
    }
}
