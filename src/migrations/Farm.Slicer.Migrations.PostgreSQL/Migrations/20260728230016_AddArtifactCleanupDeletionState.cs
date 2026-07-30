using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

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
            type: "timestamp with time zone",
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
