using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultBackendToModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultBackend",
                table: "Models",
                type: "INTEGER",
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
