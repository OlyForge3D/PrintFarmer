using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddSliceJobLeaseAndCalibrationProvenance : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationAttemptId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationOrchestrationId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationProjectId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "FilamentProfileId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentProfileJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentProfileSha256",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "IdempotencyScopeId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.AddColumn<long>(
            name: "LeaseFence",
            table: "SliceJobs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>(
            name: "LeaseToken",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "MachineProfileId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MachineProfileJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MachineProfileSha256",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "Model3DId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ModelSha256",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "OperationId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ProcessProfileId",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProcessProfileJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProcessProfileSha256",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerContainerDigest",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerEngineName",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerVersion",
            table: "SliceJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeclaredSha256",
            table: "Artifacts",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_CalibrationOrchestrationId",
            table: "SliceJobs",
            column: "CalibrationOrchestrationId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Model3DId",
            table: "SliceJobs",
            column: "Model3DId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Owner_Project_Checksum",
            table: "SliceJobs",
            columns: new[] { "UserId", "IdempotencyScopeId", "Checksum" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Owner_Project_Correlation",
            table: "SliceJobs",
            columns: new[] { "UserId", "IdempotencyScopeId", "CorrelationId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_CalibrationOrchestrationId",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Model3DId",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Owner_Project_Checksum",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Owner_Project_Correlation",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationAttemptId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationOrchestrationId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationProjectId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileSha256",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "IdempotencyScopeId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "LeaseFence",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "LeaseToken",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileSha256",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "Model3DId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ModelSha256",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "OperationId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileId",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileSha256",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerContainerDigest",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerEngineName",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerVersion",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "DeclaredSha256",
            table: "Artifacts");
    }
}
