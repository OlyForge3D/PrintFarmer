using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddWorkerAttestationAndCleanupReservation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "PinnedWorkerId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerBinarySha256",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CleanupReservationToken",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CleanupReservedAtUtc",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PinnedWorkerId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerBinarySha256",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CleanupReservationToken",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "CleanupReservedAtUtc",
            table: "Artifacts");
    }
}
