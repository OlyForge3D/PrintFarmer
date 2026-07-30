using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddAttentionSnoozes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AttentionSnoozes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttentionItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SnoozedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                AttentionItemAnchorAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AttentionSnoozes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AttentionSnoozes_SnoozedUntilUtc",
            table: "AttentionSnoozes",
            column: "SnoozedUntilUtc");

        migrationBuilder.CreateIndex(
            name: "IX_AttentionSnoozes_UserId_AttentionItemId",
            table: "AttentionSnoozes",
            columns: new[] { "UserId", "AttentionItemId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AttentionSnoozes");
    }
}
