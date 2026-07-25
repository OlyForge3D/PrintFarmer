using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddCalibrationProfileIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            schema: "slicer",
            table: "ProcessProfiles",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "ProcessProfiles",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            schema: "slicer",
            table: "MachineProfiles",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "MachineProfiles",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            schema: "slicer",
            table: "FilamentProfiles",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "FilamentProfiles",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProfileFormat",
            schema: "slicer",
            table: "ProcessProfiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "ProcessProfiles");

        migrationBuilder.DropColumn(
            name: "ProfileFormat",
            schema: "slicer",
            table: "MachineProfiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "MachineProfiles");

        migrationBuilder.DropColumn(
            name: "ProfileFormat",
            schema: "slicer",
            table: "FilamentProfiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "FilamentProfiles");
    }
}
