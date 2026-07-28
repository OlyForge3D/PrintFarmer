using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddArtifactCleanupDeletionState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CleanupDeletionStartedAtUtc",
            schema: "slicer",
            table: "Artifacts",
            type: "datetime2",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CleanupDeletionStartedAtUtc",
            schema: "slicer",
            table: "Artifacts");
    }
}
