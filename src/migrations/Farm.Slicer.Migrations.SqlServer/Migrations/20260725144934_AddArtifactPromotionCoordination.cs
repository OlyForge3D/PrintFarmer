using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddArtifactPromotionCoordination : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "PromotedAtUtc",
            schema: "slicer",
            table: "Artifacts",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PromotedGcodeFileId",
            schema: "slicer",
            table: "Artifacts",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PromotionCheckpointId",
            schema: "slicer",
            table: "Artifacts",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationId",
            schema: "slicer",
            table: "Artifacts",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PromotionStartedAtUtc",
            schema: "slicer",
            table: "Artifacts",
            type: "datetime2",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotedGcodeFileId",
            schema: "slicer",
            table: "Artifacts",
            column: "PromotedGcodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationId",
            schema: "slicer",
            table: "Artifacts",
            column: "PromotionOperationId",
            unique: true,
            filter: "[PromotionOperationId] IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotedGcodeFileId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotedAtUtc",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotedGcodeFileId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionCheckpointId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionOperationId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionStartedAtUtc",
            schema: "slicer",
            table: "Artifacts");
    }
}
