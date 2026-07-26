using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddSliceJobClaimIncarnation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ClaimToken",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ClaimToken",
            schema: "slicer",
            table: "Artifacts",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ClaimToken",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ClaimToken",
            schema: "slicer",
            table: "Artifacts");
    }
}
