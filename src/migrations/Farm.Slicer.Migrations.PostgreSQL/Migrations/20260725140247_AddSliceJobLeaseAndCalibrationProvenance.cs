using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddSliceJobLeaseAndCalibrationProvenance : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationAttemptId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationOrchestrationId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationProjectId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "FilamentProfileId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentProfileJson",
            schema: "slicer",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentProfileSha256",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "IdempotencyScopeId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.AddColumn<long>(
            name: "LeaseFence",
            schema: "slicer",
            table: "SliceJobs",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>(
            name: "LeaseToken",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "MachineProfileId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MachineProfileJson",
            schema: "slicer",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MachineProfileSha256",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "Model3DId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ModelSha256",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "OperationId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ProcessProfileId",
            schema: "slicer",
            table: "SliceJobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProcessProfileJson",
            schema: "slicer",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProcessProfileSha256",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerContainerDigest",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerEngineName",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerVersion",
            schema: "slicer",
            table: "SliceJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeclaredSha256",
            schema: "slicer",
            table: "Artifacts",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_CalibrationOrchestrationId",
            schema: "slicer",
            table: "SliceJobs",
            column: "CalibrationOrchestrationId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Model3DId",
            schema: "slicer",
            table: "SliceJobs",
            column: "Model3DId");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Owner_Project_Checksum",
            schema: "slicer",
            table: "SliceJobs",
            columns: new[] { "UserId", "IdempotencyScopeId", "Checksum" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Owner_Project_Correlation",
            schema: "slicer",
            table: "SliceJobs",
            columns: new[] { "UserId", "IdempotencyScopeId", "CorrelationId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_CalibrationOrchestrationId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Model3DId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Owner_Project_Checksum",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Owner_Project_Correlation",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationAttemptId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationOrchestrationId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "CalibrationProjectId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileJson",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "FilamentProfileSha256",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "IdempotencyScopeId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "LeaseFence",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "LeaseToken",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileJson",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "MachineProfileSha256",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "Model3DId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ModelSha256",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "OperationId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileId",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileJson",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ProcessProfileSha256",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerContainerDigest",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerEngineName",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "SlicerVersion",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "DeclaredSha256",
            schema: "slicer",
            table: "Artifacts");
    }
}
