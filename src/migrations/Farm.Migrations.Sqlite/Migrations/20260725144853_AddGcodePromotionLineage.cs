using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddGcodePromotionLineage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationAttemptId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationManifestJson",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CalibrationManifestSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationOrchestrationId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CalibrationProjectId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ContentSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentProfileSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FirmwareFamily",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GcodeDialect",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratorName",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratorVersion",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsImmutable",
            table: "GcodeFiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "MachineProfileSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedSlicerVersion",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProcessProfileSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PromotedAtUtc",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PromotionCorrelationId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationId",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerContainerDigest",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerDistribution",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerEngineName",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SourceArtifactId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceModelSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SourceSliceJobId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SourceWorkerId",
            table: "GcodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SpecificationSha256",
            table: "GcodeFiles",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "GcodePromotionCheckpoints",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                OperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                RequestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SourceArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceSliceJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceWorkerId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SourceSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                CalibrationProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                CalibrationAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                CalibrationOrchestrationId = table.Column<Guid>(type: "TEXT", nullable: true),
                GcodeFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                FailureCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ReconcileAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                SourceAcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GcodePromotionCheckpoints", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_CalibrationAttemptId",
            table: "GcodeFiles",
            column: "CalibrationAttemptId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_CalibrationOrchestrationId",
            table: "GcodeFiles",
            column: "CalibrationOrchestrationId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles",
            column: "PromotionOperationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_SourceArtifactId_ContentSha256",
            table: "GcodeFiles",
            columns: new[] { "SourceArtifactId", "ContentSha256" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_GcodeFileId",
            table: "GcodePromotionCheckpoints",
            column: "GcodeFileId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_OperationScope_OperationId",
            table: "GcodePromotionCheckpoints",
            columns: new[] { "OperationScope", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_SourceArtifactId_SourceContentSha256",
            table: "GcodePromotionCheckpoints",
            columns: new[] { "SourceArtifactId", "SourceContentSha256" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodePromotionCheckpoints_State_UpdatedAtUtc",
            table: "GcodePromotionCheckpoints",
            columns: new[] { "State", "UpdatedAtUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GcodePromotionCheckpoints");

        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_CalibrationAttemptId",
            table: "GcodeFiles");

        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_CalibrationOrchestrationId",
            table: "GcodeFiles");

        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles");

        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_SourceArtifactId_ContentSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "CalibrationAttemptId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "CalibrationManifestJson",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "CalibrationManifestSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "CalibrationOrchestrationId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "CalibrationProjectId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ContentSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "FilamentProfileSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "FirmwareFamily",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "GcodeDialect",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "GeneratorName",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "GeneratorVersion",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "IsImmutable",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "MachineProfileSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "PinnedSlicerVersion",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ProcessProfileSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "PromotedAtUtc",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "PromotionCorrelationId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "PromotionOperationId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SlicerContainerDigest",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SlicerDistribution",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SlicerEngineName",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SourceArtifactId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SourceModelSha256",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SourceSliceJobId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SourceWorkerId",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SpecificationSha256",
            table: "GcodeFiles");
    }
}
