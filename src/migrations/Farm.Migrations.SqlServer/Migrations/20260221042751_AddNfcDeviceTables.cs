using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddNfcDeviceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NfcDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    WifiRssi = table.Column<int>(type: "int", nullable: true),
                    NfcReaderOk = table.Column<bool>(type: "bit", nullable: false),
                    FreeHeap = table.Column<int>(type: "int", nullable: true),
                    LastHeartbeat = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastScanAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastScannedSpoolId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NfcDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NfcDevices_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NfcScanEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NfcDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpoolId = table.Column<int>(type: "int", nullable: true),
                    TagFormat = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MaterialType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BrandName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NfcScanEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NfcScanEvents_NfcDevices_NfcDeviceId",
                        column: x => x.NfcDeviceId,
                        principalTable: "NfcDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NfcDevices_PrinterId",
                table: "NfcDevices",
                column: "PrinterId",
                unique: true,
                filter: "\"PrinterId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NfcScanEvents_NfcDeviceId",
                table: "NfcScanEvents",
                column: "NfcDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_NfcScanEvents_ScannedAt",
                table: "NfcScanEvents",
                column: "ScannedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NfcScanEvents");

            migrationBuilder.DropTable(
                name: "NfcDevices");
        }
    }
}
