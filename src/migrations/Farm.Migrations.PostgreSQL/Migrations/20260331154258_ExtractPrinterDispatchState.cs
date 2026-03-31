using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class ExtractPrinterDispatchState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1. Create the new PrinterDispatchStates table
        migrationBuilder.CreateTable(
            name: "PrinterDispatchStates",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                AutoDispatchState = table.Column<int>(type: "integer", nullable: false),
                BedPreConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterDispatchStates", x => x.PrinterId);
                table.ForeignKey(
                    name: "FK_PrinterDispatchStates_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // 2. Copy existing dispatch state data from Printers to the new table
        migrationBuilder.Sql("""
            INSERT INTO "PrinterDispatchStates" ("PrinterId", "AutoDispatchState", "BedPreConfirmed")
            SELECT "Id", "AutoDispatchState", "BedPreConfirmed"
            FROM "Printers"
            """);

        // 3. Drop the columns from Printers now that data is migrated
        migrationBuilder.DropColumn(
            name: "AutoDispatchState",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "BedPreConfirmed",
            table: "Printers");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AutoDispatchState",
            table: "Printers",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "BedPreConfirmed",
            table: "Printers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Restore data from PrinterDispatchStates back to Printers
        migrationBuilder.Sql("""
            UPDATE "Printers"
            SET "AutoDispatchState" = pds."AutoDispatchState",
                "BedPreConfirmed" = pds."BedPreConfirmed"
            FROM "PrinterDispatchStates" pds
            WHERE "Printers"."Id" = pds."PrinterId"
            """);

        migrationBuilder.DropTable(
            name: "PrinterDispatchStates");
    }
}
