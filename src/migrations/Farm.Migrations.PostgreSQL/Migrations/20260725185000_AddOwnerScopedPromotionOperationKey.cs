using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddOwnerScopedPromotionOperationKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles");

        migrationBuilder.AddColumn<string>(
            name: "PromotionOperationKey",
            table: "GcodeFiles",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles",
            column: "PromotionOperationId");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PromotionOperationKey",
            table: "GcodeFiles",
            column: "PromotionOperationKey",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles");

        migrationBuilder.DropIndex(
            name: "IX_GcodeFiles_PromotionOperationKey",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "PromotionOperationKey",
            table: "GcodeFiles");

        migrationBuilder.CreateIndex(
            name: "IX_GcodeFiles_PromotionOperationId",
            table: "GcodeFiles",
            column: "PromotionOperationId",
            unique: true);
    }
}
