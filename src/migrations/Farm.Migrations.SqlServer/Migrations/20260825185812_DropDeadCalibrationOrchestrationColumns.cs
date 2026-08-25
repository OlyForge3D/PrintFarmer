using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadCalibrationOrchestrationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalArtifactId",
                table: "CalibrationOrchestrations");

            migrationBuilder.DropColumn(
                name: "GcodeSha256",
                table: "CalibrationOrchestrations");

            migrationBuilder.DropColumn(
                name: "GeneratorVersion",
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
                name: "SourceArtifactId",
                table: "CalibrationOrchestrations");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "CalibrationOrchestrations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FinalArtifactId",
                table: "CalibrationOrchestrations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GcodeSha256",
                table: "CalibrationOrchestrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeneratorVersion",
                table: "CalibrationOrchestrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestSha256",
                table: "CalibrationOrchestrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanManifestSha256",
                table: "CalibrationOrchestrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionOperationId",
                table: "CalibrationOrchestrations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlicerBinarySha256",
                table: "CalibrationOrchestrations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlicerContainerDigest",
                table: "CalibrationOrchestrations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceArtifactId",
                table: "CalibrationOrchestrations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "CalibrationOrchestrations",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}
