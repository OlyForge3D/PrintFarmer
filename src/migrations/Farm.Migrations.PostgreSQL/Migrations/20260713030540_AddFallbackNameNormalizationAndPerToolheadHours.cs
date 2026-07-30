using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddFallbackNameNormalizationAndPerToolheadHours : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_FilamentFallbackGroups_PrinterId_Name",
            table: "FilamentFallbackGroups");

        migrationBuilder.AddColumn<double>(
            name: "CumulativePrintHours",
            table: "Toolheads",
            type: "double precision",
            nullable: false,
            defaultValue: 0.0);

        migrationBuilder.AddColumn<double>(
            name: "ToolheadHoursAtMaintenance",
            table: "MaintenanceLogs",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "NameNormalized",
            table: "FilamentFallbackGroups",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: string.Empty);

        // Backfill the case-folded name for existing rows BEFORE creating the unique index
        // (issue #711, FIX A). Without this, every pre-existing group would share the ""
        // default and collide on the new (PrinterId, NameNormalized) unique index.
        migrationBuilder.Sql(
            "UPDATE \"FilamentFallbackGroups\" SET \"NameNormalized\" = LOWER(\"Name\");");

        // Existing names may differ only by case (for example, "PLA" and "pla"). Keep the
        // oldest row's normalized name and deterministically suffix later duplicates with
        // their globally unique ID before the unique index is created.
        migrationBuilder.Sql(
            """
            WITH ranked AS (
                SELECT
                    "Id",
                    ROW_NUMBER() OVER (
                        PARTITION BY "PrinterId", "NameNormalized"
                        ORDER BY "CreatedAt", "Id"
                    ) AS "DuplicateRank"
                FROM "FilamentFallbackGroups"
            )
            UPDATE "FilamentFallbackGroups" AS target
            SET "NameNormalized" =
                LEFT(target."NameNormalized", 91) || ':' || target."Id"::text
            FROM ranked
            WHERE target."Id" = ranked."Id"
              AND ranked."DuplicateRank" > 1;
            """);

        // Per-toolhead cumulative hours start at 0 for all existing toolheads (issue #711,
        // FIX B). This is the documented "no per-tool history yet" baseline: per-tool
        // maintenance schedules measure accrual from the point the migration runs.
        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroups_PrinterId_NameNormalized",
            table: "FilamentFallbackGroups",
            columns: new[] { "PrinterId", "NameNormalized" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_FilamentFallbackGroups_PrinterId_NameNormalized",
            table: "FilamentFallbackGroups");

        migrationBuilder.DropColumn(
            name: "CumulativePrintHours",
            table: "Toolheads");

        migrationBuilder.DropColumn(
            name: "ToolheadHoursAtMaintenance",
            table: "MaintenanceLogs");

        migrationBuilder.DropColumn(
            name: "NameNormalized",
            table: "FilamentFallbackGroups");

        migrationBuilder.CreateIndex(
            name: "UX_FilamentFallbackGroups_PrinterId_Name",
            table: "FilamentFallbackGroups",
            columns: new[] { "PrinterId", "Name" },
            unique: true);
    }
}
