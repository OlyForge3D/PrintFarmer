using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Migrations;

/// <summary>
/// Regression coverage for #953 (fresh SQL Server InitialV1 migration).
///
/// The bug: <c>InitialV1</c> for AppDbContext contained multiple <c>ON DELETE SET NULL</c>
/// foreign-key constraints pointing at non-nullable columns (e.g.,
/// <c>FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId</c> against a required
/// <c>ManufacturerId</c>, and <c>FK_GcodeFiles_FolderNode_FolderId</c> against a required
/// <c>FolderId</c>). SQL Server rejects such constraints at CREATE TABLE with error 1761,
/// blocking every fresh SQL Server deployment. PostgreSQL accepts the DDL but then fails at
/// any actual DELETE of the parent row.
///
/// This test invokes the actual InitialV1 <c>Up</c> method against a captured
/// <see cref="MigrationBuilder"/> and asserts the emitted DDL operations directly — it fails
/// on the exact SQL Server DDL failure, not only on model-snapshot drift. Runs in the default
/// test suite (no Docker), so CI catches this class of bug across both providers even when
/// a real SQL Server container is unavailable.
/// </summary>
public sealed class ToolheadManufacturerFkDeleteBehaviorTests
{
    [Fact]
    public void PostgresInitialV1_HasNoSetNullForeignKeyAgainstRequiredColumn()
    {
        AssertInitialV1HasNoSetNullVsRequiredFk(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2),
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public void SqlServerInitialV1_HasNoSetNullForeignKeyAgainstRequiredColumn()
    {
        AssertInitialV1HasNoSetNullVsRequiredFk(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2),
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void PostgresInitialV1_ToolheadManufacturerFk_IsRequiredAndNotSetNull()
    {
        AssertNamedFkContract(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2),
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL",
            table: "ToolheadModelDefinitions",
            fk: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            column: "ManufacturerId");
    }

    [Fact]
    public void SqlServerInitialV1_ToolheadManufacturerFk_IsRequiredAndNotSetNull()
    {
        AssertNamedFkContract(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2),
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer",
            table: "ToolheadModelDefinitions",
            fk: "FK_ToolheadModelDefinitions_Manufacturers_ManufacturerId",
            column: "ManufacturerId");
    }

    [Fact]
    public void PostgresInitialV1_GcodeFileFolderFk_IsRequiredAndNotSetNull()
    {
        AssertNamedFkContract(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV2),
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL",
            table: "GcodeFiles",
            fk: "FK_GcodeFiles_FolderNode_FolderId",
            column: "FolderId");
    }

    [Fact]
    public void SqlServerInitialV1_GcodeFileFolderFk_IsRequiredAndNotSetNull()
    {
        AssertNamedFkContract(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV2),
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer",
            table: "GcodeFiles",
            fk: "FK_GcodeFiles_FolderNode_FolderId",
            column: "FolderId");
    }

    private static MigrationBuilder RunUp(Type migrationType, string activeProvider)
    {
        var migration = (Migration)Activator.CreateInstance(migrationType)!;
        var builder = new MigrationBuilder(activeProvider);
        MethodInfo up = migrationType.GetMethod(
            "Up",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Migration {migrationType.FullName} has no non-public instance Up method.");

        _ = up.Invoke(migration, [builder]);
        return builder;
    }

    private static void AssertNamedFkContract(
        Type migrationType,
        string activeProvider,
        string table,
        string fk,
        string column)
    {
        MigrationBuilder builder = RunUp(migrationType, activeProvider);

        CreateTableOperation createTable = builder.Operations
            .OfType<CreateTableOperation>()
            .Single(op => op.Name == table);

        AddColumnOperation col = createTable.Columns.Single(c => c.Name == column);
        col.IsNullable.Should().BeFalse(
            $"{table}.{column} is required by the domain model; deployments seed placeholder "
            + "rows (e.g., Community/Unknown manufacturer, root folder) rather than allowing NULL.");

        AddForeignKeyOperation fkOp = createTable.ForeignKeys.Single(f => f.Name == fk);
        fkOp.OnDelete.Should().NotBe(
            ReferentialAction.SetNull,
            $"ON DELETE SET NULL against required (NOT NULL) column {table}.{column} is invalid: "
            + "SQL Server rejects it at CREATE TABLE with error 1761 and PostgreSQL fails at any "
            + "actual DELETE. See #953 and #723 comment 5080524020.");
    }

    private static void AssertInitialV1HasNoSetNullVsRequiredFk(
        Type migrationType,
        string activeProvider)
    {
        MigrationBuilder builder = RunUp(migrationType, activeProvider);

        List<string> offenders = new();
        foreach (CreateTableOperation createTable in builder.Operations.OfType<CreateTableOperation>())
        {
            Dictionary<string, bool> nullability = createTable.Columns
                .ToDictionary(c => c.Name, c => c.IsNullable);
            foreach (AddForeignKeyOperation fk in createTable.ForeignKeys)
            {
                if (fk.OnDelete != ReferentialAction.SetNull)
                {
                    continue;
                }

                foreach (string col in fk.Columns.Where(col =>
                             nullability.TryGetValue(col, out bool isNullable) && !isNullable))
                {
                    offenders.Add(
                        $"table={createTable.Name} fk={fk.Name} column={col} — "
                        + "SET NULL against NOT NULL column");
                }
            }
        }

        offenders.Should().BeEmpty(
            "InitialV1 emits ON DELETE SET NULL against required (NOT NULL) column(s), which "
            + "SQL Server rejects at CREATE TABLE (error 1761) and PostgreSQL fails at DELETE. "
            + "This blocks fresh deployments. See #953 for the class of bug.");
    }
}
