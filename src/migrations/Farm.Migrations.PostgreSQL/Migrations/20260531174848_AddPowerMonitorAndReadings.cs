using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPowerMonitorAndReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PowerMonitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ElectricityRateUsdPerKwh = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PowerMonitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PowerMonitors_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PowerReadings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PowerMonitorId = table.Column<int>(type: "integer", nullable: false),
                    WattsNow = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    KwhTotal = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PowerReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PowerReadings_PowerMonitors_PowerMonitorId",
                        column: x => x.PowerMonitorId,
                        principalTable: "PowerMonitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PowerMonitors_PrinterId",
                table: "PowerMonitors",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_PowerReadings_PowerMonitorId",
                table: "PowerReadings",
                column: "PowerMonitorId");

            migrationBuilder.CreateIndex(
                name: "IX_PowerReadings_RecordedAt",
                table: "PowerReadings",
                column: "RecordedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PowerReadings");

            migrationBuilder.DropTable(
                name: "PowerMonitors");
        }
    }
}
