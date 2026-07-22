using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Migrations;

/// <summary>
/// Verifies the issue #839 provider migrations (<c>AddApiKeyPurposeScopesAndExpiry</c> — the
/// canonical migration produced by the #837/#838 ancestry repair) for both PostgreSQL and SQL
/// Server add the <see cref="ApiKey.Purpose"/> and <see cref="ApiKey.Scopes"/> columns with safe,
/// non-nullable, zero-valued defaults so existing OctoPrint/legacy rows are upgraded to
/// <see cref="ApiKeyPurpose.OctoPrint"/> / <see cref="ApiKeyScope.None"/> and never silently gain
/// Desktop access. These tests inspect the generated migration operations directly (via the
/// public <c>Migration.UpOperations</c>/<c>DownOperations</c> APIs) so they run deterministically
/// without a live Postgres or SQL Server connection.
/// </summary>
public class ApiKeyPurposeAndScopesMigrationTests
{
    [Fact]
    public void PostgreSqlMigration_Up_AddsPurposeAndScopesColumnsWithSafeDefaults()
    {
        var migration = new Farm.Migrations.PostgreSQL.Migrations.AddApiKeyPurposeScopesAndExpiry();

        AssertAddsPurposeAndScopesWithSafeDefaults(migration.UpOperations);
    }

    [Fact]
    public void PostgreSqlMigration_Down_DropsPurposeAndScopesColumns()
    {
        var migration = new Farm.Migrations.PostgreSQL.Migrations.AddApiKeyPurposeScopesAndExpiry();

        AssertDropsPurposeAndScopesColumns(migration.DownOperations);
    }

    [Fact]
    public void SqlServerMigration_Up_AddsPurposeAndScopesColumnsWithSafeDefaults()
    {
        var migration = new Farm.Migrations.SqlServer.Migrations.AddApiKeyPurposeScopesAndExpiry();

        AssertAddsPurposeAndScopesWithSafeDefaults(migration.UpOperations);
    }

    [Fact]
    public void SqlServerMigration_Down_DropsPurposeAndScopesColumns()
    {
        var migration = new Farm.Migrations.SqlServer.Migrations.AddApiKeyPurposeScopesAndExpiry();

        AssertDropsPurposeAndScopesColumns(migration.DownOperations);
    }

    /// <summary>
    /// Both provider migrations must be functionally equivalent: same table, same column
    /// names/types, same non-nullable zero defaults. Divergence here would mean the two
    /// providers upgrade existing data differently.
    /// </summary>
    [Fact]
    public void BothProviderMigrations_ProduceEquivalentColumnDefinitions()
    {
        var pg = new Farm.Migrations.PostgreSQL.Migrations.AddApiKeyPurposeScopesAndExpiry();
        var sqlServer = new Farm.Migrations.SqlServer.Migrations.AddApiKeyPurposeScopesAndExpiry();

        AddColumnOperation pgPurpose = GetAddColumn(pg.UpOperations, "Purpose");
        AddColumnOperation sqlPurpose = GetAddColumn(sqlServer.UpOperations, "Purpose");
        AddColumnOperation pgScopes = GetAddColumn(pg.UpOperations, "Scopes");
        AddColumnOperation sqlScopes = GetAddColumn(sqlServer.UpOperations, "Scopes");

        pgPurpose.IsNullable.Should().Be(sqlPurpose.IsNullable);
        Convert.ToInt32(pgPurpose.DefaultValue).Should().Be(Convert.ToInt32(sqlPurpose.DefaultValue));
        pgScopes.IsNullable.Should().Be(sqlScopes.IsNullable);
        Convert.ToInt32(pgScopes.DefaultValue).Should().Be(Convert.ToInt32(sqlScopes.DefaultValue));
    }

    private static void AssertAddsPurposeAndScopesWithSafeDefaults(IReadOnlyList<MigrationOperation> upOperations)
    {
        AddColumnOperation purposeOp = GetAddColumn(upOperations, "Purpose");
        purposeOp.Table.Should().Be("ApiKeys");
        purposeOp.IsNullable.Should().BeFalse("existing rows must receive a concrete Purpose, never NULL");
        Convert.ToInt32(purposeOp.DefaultValue).Should().Be((int)ApiKeyPurpose.OctoPrint,
            "existing/legacy API keys predate the Desktop feature and must upgrade as OctoPrint-purpose keys");

        AddColumnOperation scopesOp = GetAddColumn(upOperations, "Scopes");
        scopesOp.Table.Should().Be("ApiKeys");
        scopesOp.IsNullable.Should().BeFalse("existing rows must receive a concrete Scopes value, never NULL");
        Convert.ToInt32(scopesOp.DefaultValue).Should().Be((int)ApiKeyScope.None,
            "existing/legacy API keys must never be silently granted Desktop scopes (ModelRead/ModelWrite/LibrarySync)");
    }

    private static void AssertDropsPurposeAndScopesColumns(IReadOnlyList<MigrationOperation> downOperations)
    {
        downOperations.OfType<DropColumnOperation>()
            .Should().Contain(op => op.Table == "ApiKeys" && op.Name == "Purpose");
        downOperations.OfType<DropColumnOperation>()
            .Should().Contain(op => op.Table == "ApiKeys" && op.Name == "Scopes");
    }

    private static AddColumnOperation GetAddColumn(IReadOnlyList<MigrationOperation> operations, string columnName)
    {
        return operations.OfType<AddColumnOperation>().Single(op => op.Table == "ApiKeys" && op.Name == columnName);
    }
}
