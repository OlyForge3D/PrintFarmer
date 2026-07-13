using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftPlanUniqueSourceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserTasks_SourceKind_SourceId",
                table: "UserTasks");

            migrationBuilder.CreateIndex(
                name: "IX_UserTasks_SourceKind_SourceId",
                table: "UserTasks",
                columns: new[] { "SourceKind", "SourceId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL AND \"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserTasks_SourceKind_SourceId",
                table: "UserTasks");

            migrationBuilder.CreateIndex(
                name: "IX_UserTasks_SourceKind_SourceId",
                table: "UserTasks",
                columns: new[] { "SourceKind", "SourceId" });
        }
    }
}
