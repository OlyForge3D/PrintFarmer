using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class ExtractPrinterServiceState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1. Create the new PrinterServiceState table
        migrationBuilder.CreateTable(
            name: "PrinterServiceState",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                LastHistorySeedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModelSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastCapabilityUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ObicoServerId = table.Column<Guid>(type: "uuid", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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

        // 2. Copy data from Printers to PrinterServiceState for all existing rows
        migrationBuilder.Sql(@"
                INSERT INTO ""PrinterServiceState"" (""PrinterId"", ""LastHistorySeedUtc"", ""LastModelSyncAt"", ""LastCapabilityUpdate"", ""ObicoServerId"")
                SELECT ""Id"", ""LastHistorySeedUtc"", ""LastModelSyncAt"", ""LastCapabilityUpdate"", ""ObicoServerId""
                FROM ""Printers"";
            ");

        // 3. Drop the columns from Printers table
        migrationBuilder.DropForeignKey(
            name: "FK_Printers_ObicoServers_ObicoServerId",
            table: "Printers");

        migrationBuilder.DropIndex(
            name: "IX_Printers_ObicoServerId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastHistorySeedUtc",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastModelSyncAt",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "LastCapabilityUpdate",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "ObicoServerId",
            table: "Printers");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 1. Add columns back to Printers table
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

        migrationBuilder.AddColumn<DateTime>(
            name: "LastCapabilityUpdate",
            table: "Printers",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "NOW()");

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
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        // 2. Copy data back from PrinterServiceState to Printers
        migrationBuilder.Sql(@"
                UPDATE ""Printers"" p
                SET ""LastHistorySeedUtc"" = s.""LastHistorySeedUtc"",
                    ""LastModelSyncAt"" = s.""LastModelSyncAt"",
                    ""LastCapabilityUpdate"" = s.""LastCapabilityUpdate"",
                    ""ObicoServerId"" = s.""ObicoServerId""
                FROM ""PrinterServiceState"" s
                WHERE p.""Id"" = s.""PrinterId"";
            ");

        // 3. Drop the PrinterServiceState table
        migrationBuilder.DropTable(
            name: "PrinterServiceState");
    }
}
