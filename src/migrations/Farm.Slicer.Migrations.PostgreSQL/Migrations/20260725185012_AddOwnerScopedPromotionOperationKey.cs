using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddOwnerScopedPromotionOperationKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationKey",
            schema: "slicer",
            table: "Artifacts",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationId",
            schema: "slicer",
            table: "Artifacts",
            column: "PromotionOperationId");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationKey",
            schema: "slicer",
            table: "Artifacts",
            column: "PromotionOperationKey",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationId",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationKey",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionOperationKey",
            schema: "slicer",
            table: "Artifacts");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationId",
            schema: "slicer",
            table: "Artifacts",
            column: "PromotionOperationId",
            unique: true);
    }
}
