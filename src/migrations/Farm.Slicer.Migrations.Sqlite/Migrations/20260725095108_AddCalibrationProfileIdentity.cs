using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddCalibrationProfileIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            table: "ProcessProfiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            table: "ProcessProfiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            table: "MachineProfiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            table: "MachineProfiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            table: "FilamentProfiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            table: "FilamentProfiles",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProfileFormat",
            table: "ProcessProfiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            table: "ProcessProfiles");

        migrationBuilder.DropColumn(
            name: "ProfileFormat",
            table: "MachineProfiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            table: "MachineProfiles");

        migrationBuilder.DropColumn(
            name: "ProfileFormat",
            table: "FilamentProfiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            table: "FilamentProfiles");
    }
}
