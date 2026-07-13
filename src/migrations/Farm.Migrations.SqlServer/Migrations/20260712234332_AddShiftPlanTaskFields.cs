using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddShiftPlanTaskFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "AnchorAtUtc",
            table: "UserTasks",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AnchorKind",
            table: "UserTasks",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.AddColumn<string>(
            name: "SourceId",
            table: "UserTasks",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceKind",
            table: "UserTasks",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.AddColumn<DateTime>(
            name: "WindowEndUtc",
            table: "UserTasks",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "WindowStartUtc",
            table: "UserTasks",
            type: "datetime2",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_SourceKind_SourceId",
            table: "UserTasks",
            columns: new[] { "SourceKind", "SourceId" });

        migrationBuilder.CreateIndex(
            name: "IX_UserTasks_Status_AnchorKind_AnchorAtUtc",
            table: "UserTasks",
            columns: new[] { "Status", "AnchorKind", "AnchorAtUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_UserTasks_SourceKind_SourceId",
            table: "UserTasks");

        migrationBuilder.DropIndex(
            name: "IX_UserTasks_Status_AnchorKind_AnchorAtUtc",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "AnchorAtUtc",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "AnchorKind",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "SourceId",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "SourceKind",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "WindowEndUtc",
            table: "UserTasks");

        migrationBuilder.DropColumn(
            name: "WindowStartUtc",
            table: "UserTasks");
    }
}
