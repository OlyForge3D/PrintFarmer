using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddCalibrationGenerationOrchestration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "FinalArtifactId",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GcodeSha256",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GenerationRequestSha256",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratorVersion",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LeaseExpiresAtUtc",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LeaseOwner",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ManifestSha256",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PlanManifestSha256",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationId",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerBinarySha256",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SlicerContainerDigest",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SpecificationSha256",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "StepStartedAtUtc",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "WorkerId",
            table: "CalibrationOrchestrations",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationOrchestrations_LeaseExpiresAtUtc",
            table: "CalibrationOrchestrations",
            column: "LeaseExpiresAtUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CalibrationOrchestrations_LeaseExpiresAtUtc",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "FinalArtifactId",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "GcodeSha256",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "GenerationRequestSha256",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "GeneratorVersion",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "LeaseExpiresAtUtc",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "LeaseOwner",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "ManifestSha256",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "PlanManifestSha256",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "PromotionOperationId",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "SlicerBinarySha256",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "SlicerContainerDigest",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "SpecificationSha256",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "StepStartedAtUtc",
            table: "CalibrationOrchestrations");

        migrationBuilder.DropColumn(
            name: "WorkerId",
            table: "CalibrationOrchestrations");
    }
}
