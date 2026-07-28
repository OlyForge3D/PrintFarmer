using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddArtifactCleanupDeletionState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CleanupDeletionStartedAtUtc",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CleanupDeletionStartedAtUtc",
            table: "Artifacts");
    }
}
