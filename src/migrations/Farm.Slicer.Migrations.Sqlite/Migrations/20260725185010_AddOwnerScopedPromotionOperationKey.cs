using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddOwnerScopedPromotionOperationKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationId",
            table: "Artifacts");

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationKey",
            table: "Artifacts",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationId",
            table: "Artifacts",
            column: "PromotionOperationId");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationKey",
            table: "Artifacts",
            column: "PromotionOperationKey",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationId",
            table: "Artifacts");

        migrationBuilder.DropIndex(
            name: "IX_Artifacts_PromotionOperationKey",
            table: "Artifacts");

        migrationBuilder.DropColumn(
            name: "PromotionOperationKey",
            table: "Artifacts");

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_PromotionOperationId",
            table: "Artifacts",
            column: "PromotionOperationId",
            unique: true);
    }
}
