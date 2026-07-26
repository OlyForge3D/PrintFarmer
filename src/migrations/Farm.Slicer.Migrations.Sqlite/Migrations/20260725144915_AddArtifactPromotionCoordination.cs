using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddArtifactPromotionCoordination : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "PromotedAtUtc",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PromotedGcodeFileId",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PromotionCheckpointId",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationId",
            table: "Artifacts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PromotionStartedAtUtc",
            table: "Artifacts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotedGcodeFileId",
            table: "Artifacts",
            column: "PromotedGcodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationId",
            table: "Artifacts",
            column: "PromotionOperationId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotedGcodeFileId",
            table: "Artifacts");

        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationId",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotedAtUtc",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotedGcodeFileId",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionCheckpointId",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionOperationId",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionStartedAtUtc",
            table: "Artifacts");
    }
}
