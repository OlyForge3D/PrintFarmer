using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Infrastructure.Tests.Migrations;

/// <summary>
/// Named-FK assertions for the two roots Dallas resolved in the #953 cascade
/// adjudication:
///  1) <c>FK_PrinterMaintenanceSchedules_Printers_PrinterId</c>: <c>Cascade → Restrict</c>
///     (removes the Printers ⇒ Schedules ⇒ MaintenanceAlerts SetNull path).
///  2) <c>FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId</c>: <c>SetNull → Restrict</c>
///     in the InitialV1 baseline directly (previously corrected only by
///     <c>20260713130922_RestrictMaintenanceLogResolvedAlertDelete</c>).
///
/// The Dallas roots are asserted as <c>Restrict / NoAction</c> — anything else regresses
/// the cascade graph and would re-introduce SQL Server error 1785 on fresh installs.
/// </summary>
public sealed class InitialV1DallasFkContractTests
{
    [Fact]
    public void PostgresInitialV1_ScheduleToPrinterFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2).Assembly,
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL",
            table: "PrinterMaintenanceSchedules",
            fkName: "FK_PrinterMaintenanceSchedules_Printers_PrinterId");
    }

    [Fact]
    public void SqlServerInitialV1_ScheduleToPrinterFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2).Assembly,
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer",
            table: "PrinterMaintenanceSchedules",
            fkName: "FK_PrinterMaintenanceSchedules_Printers_PrinterId");
    }

    [Fact]
    public void PostgresInitialV1_MaintenanceLogResolvedAlertFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2).Assembly,
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL",
            table: "MaintenanceLogs",
            fkName: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId");
    }

    [Fact]
    public void SqlServerInitialV1_MaintenanceLogResolvedAlertFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2).Assembly,
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer",
            table: "MaintenanceLogs",
            fkName: "FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId");
    }

    [Fact]
    public void PostgresAddNozzleDiameterMigration_CameraSnapshotCameraFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2).Assembly,
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL",
            table: "CameraSnapshots",
            fkName: "FK_CameraSnapshots_Cameras_CameraId");
    }

    [Fact]
    public void SqlServerAddNozzleDiameterMigration_CameraSnapshotCameraFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2).Assembly,
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer",
            table: "CameraSnapshots",
            fkName: "FK_CameraSnapshots_Cameras_CameraId");
    }

    [Fact]
    public void PostgresAddPrintedPartsInventoryMigration_PartOutputMappingGcodeFileFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2).Assembly,
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL",
            table: "PartOutputMappings",
            fkName: "FK_PartOutputMappings_GcodeFiles_GcodeFileId");
    }

    [Fact]
    public void SqlServerAddPrintedPartsInventoryMigration_PartOutputMappingGcodeFileFk_IsRestrict()
    {
        AssertMigrationChainFkOnDeleteIsRestrict(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2).Assembly,
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer",
            table: "PartOutputMappings",
            fkName: "FK_PartOutputMappings_GcodeFiles_GcodeFileId");
    }

    /// <summary>
    /// Replays the whole migration chain and asserts the given FK ends up with
    /// <c>Restrict</c>/<c>NoAction</c> on-delete, regardless of which migration introduced
    /// or last modified it. Used for FKs whose baseline lives in a post-InitialV1 migration
    /// (e.g. <c>CameraSnapshots</c>, <c>PartOutputMappings</c>).
    ///
    /// F5c hardening — asserts the FK exists BEFORE checking its behavior so a missing/
    /// renamed FK cannot silently default-pass as <c>NoAction</c> (which is the default value
    /// of <see cref="ReferentialAction"/> when a <c>GetValueOrDefault</c> lookup misses).
    /// </summary>
    private static void AssertMigrationChainFkOnDeleteIsRestrict(
        Assembly migrationAssembly,
        string activeProvider,
        string table,
        string fkName)
    {
        List<(string Id, Type Type)> ordered = new();
        foreach (Type t in migrationAssembly.GetTypes())
        {
            if (!t.IsSubclassOf(typeof(Migration)) || t.IsAbstract)
            {
                continue;
            }

            var attr = t.GetCustomAttribute<MigrationAttribute>();
            if (attr == null)
            {
                continue;
            }

            ordered.Add((attr.Id, t));
        }

        ordered.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph = new();
        foreach ((string _, Type type) in ordered)
        {
            var migration = (Migration)Activator.CreateInstance(type)!;
            var builder = new MigrationBuilder(activeProvider);
            MethodInfo up = type.GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = up.Invoke(migration, [builder]);

            foreach (MigrationOperation op in builder.Operations)
            {
                switch (op)
                {
                    case CreateTableOperation ct:
                        Dictionary<string, (string, ReferentialAction)> fks = new();
                        foreach (AddForeignKeyOperation fk in ct.ForeignKeys)
                        {
                            fks[fk.Name!] = (fk.PrincipalTable!, fk.OnDelete);
                        }

                        graph[ct.Name] = fks;
                        break;
                    case DropTableOperation dt:
                        _ = graph.Remove(dt.Name);
                        break;
                    case AddForeignKeyOperation add:
                        if (!graph.TryGetValue(add.Table, out Dictionary<string, (string, ReferentialAction)>? existing))
                        {
                            existing = new Dictionary<string, (string, ReferentialAction)>();
                            graph[add.Table] = existing;
                        }

                        existing[add.Name!] = (add.PrincipalTable!, add.OnDelete);
                        break;
                    case DropForeignKeyOperation drop:
                        if (graph.TryGetValue(drop.Table, out Dictionary<string, (string, ReferentialAction)>? existingDrop))
                        {
                            _ = existingDrop.Remove(drop.Name!);
                        }

                        break;
                }
            }
        }

        // F5c — assert the table and FK actually exist in the final graph BEFORE checking
        // the on-delete behavior. Without this, a missing FK would fall through to
        // GetValueOrDefault which returns default(ValueTuple<string, ReferentialAction>) —
        // i.e., OnDelete == NoAction — and the assertion below would silently pass.
        graph.ContainsKey(table).Should().BeTrue(
            $"Table '{table}' must exist in the final migration graph — a missing or renamed table "
            + "cannot default-pass the FK contract test.");
        Dictionary<string, (string, ReferentialAction)> tableFks = graph[table];
        tableFks.ContainsKey(fkName).Should().BeTrue(
            $"FK '{fkName}' must exist on table '{table}' in the final migration graph — a missing "
            + "or renamed FK cannot default-pass the FK contract test.");

        (string _, ReferentialAction onDelete) = tableFks[fkName];

        onDelete.Should().BeOneOf(
            [ReferentialAction.Restrict, ReferentialAction.NoAction],
            $"{fkName} on {table} must be Restrict/NoAction — anything else regresses the cascade "
            + "graph and re-introduces SQL Server error 1785 (Dallas cascade adjudication for #953).");
    }
}
