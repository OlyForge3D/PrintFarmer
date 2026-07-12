using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintJobRequiredMaterialsPerTool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredMaterialsPerToolJson",
                table: "PrintJobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredMaterialsPerToolJson",
                table: "PrintJobs");
        }
    }
}
