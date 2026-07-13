using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddUserTaskSourceLookupIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_Status_SourceKind_SourceId",
            table: "UserTasks",
            columns: new[] { "Status", "SourceKind", "SourceId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_UserTasks_Status_SourceKind_SourceId",
            table: "UserTasks");
    }
}
