using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
/// <remarks>
/// Retained for deployed-history coherence. Existing PostgreSQL installs at any
/// pre-fix HEAD had <c>FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId</c> as
/// <c>ON DELETE SET NULL</c> because that is what earlier InitialV1 baselines
/// emitted; this migration corrects them to <c>Restrict</c>.
///
/// After the Dallas cascade adjudication for #953, the corrected InitialV1
/// baseline emits <c>Restrict</c> directly, so on fresh installs this migration
/// drops-and-recreates the FK in its already-final state (functionally a no-op).
/// It must NOT be removed: skipping it would break the migration chain for
/// deployed installs already carrying its <c>__EFMigrationsHistory</c> row.
/// </remarks>
public partial class RestrictMaintenanceLogResolvedAlertDelete : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            principalTable: "MaintenanceAlerts",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs");

        migrationBuilder.AddForeignKey(
            name: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId",
            table: "MaintenanceLogs",
            column: "ResolvedAlertId",
            principalTable: "MaintenanceAlerts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }
}
