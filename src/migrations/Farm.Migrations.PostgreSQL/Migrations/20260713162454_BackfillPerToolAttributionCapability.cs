using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class BackfillPerToolAttributionCapability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "Printers"
            SET "SupportsPerToolAttribution" = TRUE
            WHERE "SupportsPerToolAttribution" = FALSE
              AND "Backend" = 1
              AND (
                  SELECT COUNT(*)
                  FROM "Toolheads" AS "toolhead"
                  WHERE "toolhead"."PrinterId" = "Printers"."Id"
                    AND "toolhead"."Type" = 0
              ) >= 2;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverting a data backfill would overwrite capability changes made after the upgrade.
    }
}
