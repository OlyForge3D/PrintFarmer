using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class PerToolAttributionAndAlertResolutionLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MaintenanceLogs_ResolvedAlertId",
                table: "MaintenanceLogs");

            migrationBuilder.AddColumn<bool>(
                name: "SupportsPerToolAttribution",
                table: "Printers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceLogs_ResolvedAlertId",
                table: "MaintenanceLogs",
                column: "ResolvedAlertId",
                unique: true,
                filter: "[ResolvedAlertId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MaintenanceLogs_ResolvedAlertId",
                table: "MaintenanceLogs");

            migrationBuilder.DropColumn(
                name: "SupportsPerToolAttribution",
                table: "Printers");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceLogs_ResolvedAlertId",
                table: "MaintenanceLogs",
                column: "ResolvedAlertId");
        }
    }
}
