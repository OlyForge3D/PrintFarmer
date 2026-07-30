using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

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
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "ProcessProfiles",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            schema: "slicer",
            table: "MachineProfiles",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "MachineProfiles",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileFormat",
            schema: "slicer",
            table: "FilamentProfiles",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "FilamentProfiles",
            type: "nvarchar(max)",
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
