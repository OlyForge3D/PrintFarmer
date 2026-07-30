using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddUserTaskUniqueOpenProfileImportIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_OpenProfileImport",
            table: "UserTasks",
            columns: new[] { "TaskType", "EntityType", "EntityId" },
            unique: true,
            filter: "[TaskType] = 1 AND [EntityType] = 'PrinterModel' AND [Status] IN (0, 1)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_UserTasks_OpenProfileImport",
            table: "UserTasks");
    }
}
