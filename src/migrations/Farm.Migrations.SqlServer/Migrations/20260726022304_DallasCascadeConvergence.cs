using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <summary>
/// F3 — forward-corrective convergence migration for the Dallas cascade adjudication
/// of #953. Idempotently converges four foreign keys to the delete behavior currently
/// baked into their rewritten <c>CreateTable</c> DDL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this migration exists (F3).</b> Iterations 1 and 3 of the #953 fix altered
/// FK <c>ON DELETE</c> semantics directly in the rewritten <c>InitialV1</c> and
/// <c>AddPrintedPartsInventory</c> historical migrations. Coordinator explicitly required
/// a NEW forward corrective migration in both AppDbContext provider projects rather than
/// relying on an internal runbook. This migration is that migration.
/// </para>
/// <para>
/// <b>SQL Server special constraint.</b> No SQL Server deployment could ever have been
/// created against the pre-fix baseline because fresh <c>InitialV1</c> on SQL Server hard-
/// failed with error 1761 at <c>CREATE TABLE ToolheadModelDefinitions</c> (SET NULL against
/// NOT NULL <c>ManufacturerId</c>) and later with error 1785 at multi-cascading paths. This
/// migration therefore MUST be a no-op for the FKs on any conceivable existing SQL Server
/// install (there are none), but MUST also drop-and-recreate cleanly on a fresh install
/// where <c>InitialV1</c> now emits the target action. The <c>DROP CONSTRAINT / ADD
/// CONSTRAINT</c> pattern satisfies both: it is idempotent behaviorally.
/// </para>
/// <para>
/// <b>Down semantics are intentionally NOT invertible.</b> The pre-rewrite actions
/// (<c>SET NULL</c> on <c>NOT NULL</c> columns and <c>CASCADE</c> paths that participate
/// in multi-cascading-path graphs) reintroduce SQL Server errors 1761 and 1785. Reverting
/// to them would produce DDL that SQL Server cannot execute at fresh apply, so real
/// <c>apply → previous → reapply</c> validation would break. <c>Down</c> therefore keeps
/// each FK at the safe target action. This is a deliberate one-way convergence, aligned
/// with the same rewrite-gate rationale that already established both <c>InitialV1</c>
/// rewrites and their existing correctives (see <c>RestrictMaintenanceLogResolvedAlertDelete</c>
/// and <c>FixCameraSnapshotCascade</c>).
/// </para>
/// </remarks>
public partial class DallasCascadeConvergence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1) FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId : converge to NoAction.
        migrationBuilder.DropForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions");

        migrationBuilder.AddForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions",
            column: "ManufacturerId",
            principalTable: "Manufacturers",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);

        // 2) FK_GcodeFiles_FolderNode_FolderId : converge to NoAction.
        migrationBuilder.DropForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles");

        migrationBuilder.AddForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles",
            column: "FolderId",
            principalTable: "FolderNode",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);

        // 3) FK_PrinterMaintenanceSchedules_Printers_PrinterId : converge to Restrict
        //    (Dallas root #1 — breaks Printers => MaintenanceAlerts multi-cascading path).
        migrationBuilder.DropForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        // 4) FK_PartOutputMappings_GcodeFiles_GcodeFileId : converge to Restrict
        //    (Dallas Fix 4 — breaks GcodeFiles => PartOutputMappings multi-cascading path).
        migrationBuilder.DropForeignKey(
            name: "FK_PartOutputMappings_GcodeFiles_GcodeFileId",
            table: "PartOutputMappings");

        migrationBuilder.AddForeignKey(
            name: "FK_PartOutputMappings_GcodeFiles_GcodeFileId",
            table: "PartOutputMappings",
            column: "GcodeFileId",
            principalTable: "GcodeFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S4144:Methods should not have identical implementations",
        Justification = "Intentional. Down is deliberately identical to Up — see the class <remarks> for the one-way convergence rationale.")]
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally NOT invertible. Reverting these FKs to their pre-fix actions
        // (SetNull on NOT NULL columns; Cascade participating in multi-cascading-path
        // graphs) reintroduces SQL Server errors 1761 and 1785, breaking any real
        // apply → previous → reapply validation. Down preserves the safe target action
        // so real-provider round-trips remain executable. See the <remarks> above.
        migrationBuilder.DropForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions");

        migrationBuilder.AddForeignKey(
            name: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            table: "ToolheadModelDefinitions",
            column: "ManufacturerId",
            principalTable: "Manufacturers",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);

        migrationBuilder.DropForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles");

        migrationBuilder.AddForeignKey(
            name: "FK_GcodeFiles_FolderNode_FolderId",
            table: "GcodeFiles",
            column: "FolderId",
            principalTable: "FolderNode",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);

        migrationBuilder.DropForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules");

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterMaintenanceSchedules_Printers_PrinterId",
            table: "PrinterMaintenanceSchedules",
            column: "PrinterId",
            principalTable: "Printers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.DropForeignKey(
            name: "FK_PartOutputMappings_GcodeFiles_GcodeFileId",
            table: "PartOutputMappings");

        migrationBuilder.AddForeignKey(
            name: "FK_PartOutputMappings_GcodeFiles_GcodeFileId",
            table: "PartOutputMappings",
            column: "GcodeFileId",
            principalTable: "GcodeFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }
}
