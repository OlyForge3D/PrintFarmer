using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddCalibrationPrinterContext : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DriveType",
            table: "Toolheads",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExtruderGearRatio",
            table: "Toolheads",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "HotendMaxTemperature",
            table: "Toolheads",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDirectDrive",
            table: "Toolheads",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "MaxVolumetricFlow",
            table: "Toolheads",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "NozzleDiameter",
            table: "Toolheads",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "NozzleIsHardened",
            table: "Toolheads",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "NozzleMaterial",
            table: "Toolheads",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "NozzleMaxTemperature",
            table: "Toolheads",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "NozzleType",
            table: "Toolheads",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "OffsetX",
            table: "Toolheads",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "OffsetY",
            table: "Toolheads",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "OffsetZ",
            table: "Toolheads",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ActiveToolheadIndex",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendApiVersion",
            table: "Printers",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BackendVersion",
            table: "Printers",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "BedOriginX",
            table: "Printers",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "BedOriginY",
            table: "Printers",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CalibrationConfigurationUpdatedAtUtc",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationFilamentProfileId",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CalibrationHardwareVerifiedAtUtc",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "CalibrationHasEnclosure",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "CalibrationHasHeatedBed",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationMachineProfileId",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CalibrationMotionType",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationProcessProfileId",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationProfileFormat",
            table: "Printers",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationSlicerDistribution",
            table: "Printers",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationSlicerEngine",
            table: "Printers",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationSlicerVersion",
            table: "Printers",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ConfigurationRevision",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1L);

        migrationBuilder.AddColumn<string>(
            name: "ExcludedRegionsJson",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "FirmwareDetectedAtUtc",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "FirmwareDetectionConfidence",
            table: "Printers",
            type: "TEXT",
            precision: 5,
            scale: 4,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "FirmwareDetectionSource",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "FirmwareDetectionVersion",
            table: "Printers",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "FirmwareFamily",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "FirmwareIdentityVerified",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "FirmwareVersion",
            table: "Printers",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "GcodeDialect",
            table: "Printers",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "HasHeatedChamber",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxAcceleration",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxChamberTemp",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxTravelAcceleration",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxTravelSpeed",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PrintablePolygonJson",
            table: "Printers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "SupportsFirmwareRetraction",
            table: "Printers",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "SupportsPressureAdvance",
            table: "Printers",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DriveType",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "ExtruderGearRatio",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "HotendMaxTemperature",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "IsDirectDrive",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "MaxVolumetricFlow",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "NozzleDiameter",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "NozzleIsHardened",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "NozzleMaterial",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "NozzleMaxTemperature",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "NozzleType",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "OffsetX",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "OffsetY",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "OffsetZ",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "ActiveToolheadIndex",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "BackendApiVersion",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "BackendVersion",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "BedOriginX",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "BedOriginY",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationConfigurationUpdatedAtUtc",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationFilamentProfileId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationHardwareVerifiedAtUtc",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationHasEnclosure",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationHasHeatedBed",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationMachineProfileId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationMotionType",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationProcessProfileId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationProfileFormat",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationSlicerDistribution",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationSlicerEngine",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "CalibrationSlicerVersion",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ConfigurationRevision",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ExcludedRegionsJson",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareDetectedAtUtc",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareDetectionConfidence",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareDetectionSource",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareDetectionVersion",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareFamily",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareIdentityVerified",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "FirmwareVersion",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "GcodeDialect",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "HasHeatedChamber",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "MaxAcceleration",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "MaxChamberTemp",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "MaxTravelAcceleration",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "MaxTravelSpeed",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "PrintablePolygonJson",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "SupportsFirmwareRetraction",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "SupportsPressureAdvance",
            table: "Printers");
    }
}
