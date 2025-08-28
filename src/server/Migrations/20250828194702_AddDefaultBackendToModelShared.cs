using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultBackendToModelShared : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultBackend",
                table: "Models",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultBackend",
                table: "Models");
        }
    }
}
