using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddFilamentSwapOverrides : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FilamentSwapOverrides",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                ToolheadIndex = table.Column<int>(type: "integer", nullable: false),
                SpoolId = table.Column<int>(type: "integer", nullable: false),
                UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ExpectedMaterial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ScannedMaterial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                AffectedJobIdsJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FilamentSwapOverrides", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FilamentSwapOverrides_PrinterId_CreatedAtUtc",
            table: "FilamentSwapOverrides",
            columns: new[] { "PrinterId", "CreatedAtUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "FilamentSwapOverrides");
    }
}
