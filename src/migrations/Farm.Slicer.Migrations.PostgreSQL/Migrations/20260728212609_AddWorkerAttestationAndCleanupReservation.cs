using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddWorkerAttestationAndCleanupReservation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "PinnedWorkerId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerBinarySha256",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CleanupReservationToken",
            schema: "slicer",
            table: "Artifacts",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CleanupReservedAtUtc",
            schema: "slicer",
            table: "Artifacts",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PinnedWorkerId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerBinarySha256",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CleanupReservationToken",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "CleanupReservedAtUtc",
            schema: "slicer",
            table: "Artifacts");
    }
}
