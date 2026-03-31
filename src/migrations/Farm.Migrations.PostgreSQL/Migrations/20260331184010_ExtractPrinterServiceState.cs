using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class ExtractPrinterServiceState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Step 1: Create the new table FIRST (before dropping columns)
        migrationBuilder.CreateTable(
            name: "PrinterServiceState",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                LastHistorySeedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModelSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastCapabilityUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ObicoServerId = table.Column<Guid>(type: "uuid", nullable: true),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterServiceState", x => x.PrinterId);
                table.ForeignKey(
                    name: "FK_PrinterServiceState_ObicoServers_ObicoServerId",
                    column: x => x.ObicoServerId,
                    principalTable: "ObicoServers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PrinterServiceState_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PrinterServiceState_ObicoServerId",
            table: "PrinterServiceState",
            column: "ObicoServerId");

        // Step 2: Copy existing data from Printers to PrinterServiceState
        migrationBuilder.Sql("""
            INSERT INTO "PrinterServiceState" ("PrinterId", "LastHistorySeedUtc", "LastModelSyncAt", "LastCapabilityUpdate", "ObicoServerId")
            SELECT "Id", "LastHistorySeedUtc", "LastModelSyncAt", "LastCapabilityUpdate", "ObicoServerId"
            FROM "Printers";
            """);

        // Step 3: Now safe to drop old columns and FK
        migrationBuilder.DropForeignKey(
            name: "FK_Printers_ObicoServers_ObicoServerId",
            table: "Printers");

        migrationBuilder.DropIndex(
            name: "IX_Printers_ObicoServerId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastCapabilityUpdate",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastHistorySeedUtc",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastModelSyncAt",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ObicoServerId",
            table: "Printers");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PrinterServiceState");

        migrationBuilder.AddColumn<DateTime>(
            name: "LastCapabilityUpdate",
            table: "Printers",
            type: "timestamp with time zone",
            nullable: false,
            defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

        migrationBuilder.AddColumn<DateTime>(
            name: "LastHistorySeedUtc",
            table: "Printers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastModelSyncAt",
            table: "Printers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ObicoServerId",
            table: "Printers",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Printers_ObicoServerId",
            table: "Printers",
            column: "ObicoServerId");

        migrationBuilder.AddForeignKey(
            name: "FK_Printers_ObicoServers_ObicoServerId",
            table: "Printers",
            column: "ObicoServerId",
            principalTable: "ObicoServers",
            principalColumn: "Id");
    }
}
