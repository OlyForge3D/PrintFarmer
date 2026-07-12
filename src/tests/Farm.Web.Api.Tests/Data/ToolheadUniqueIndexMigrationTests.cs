using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

public class ToolheadUniqueIndexMigrationTests
{
    [Fact]
    public void PostgreSqlMigration_DeduplicatesBeforeCreatingUniqueIndex_AndDownOnlyDropsIndex()
    {
        AssertMigration(
            new Farm.Migrations.PostgreSQL.Migrations.EnforceUniqueToolheadPrinterIndex(),
            "\"Toolheads\"",
            "\"CurrentSpoolId\" IS NOT NULL",
            "\"UpdatedAt\" DESC",
            "\"Id\" ASC");
    }

    [Fact]
    public void SqlServerMigration_DeduplicatesBeforeCreatingUniqueIndex_AndDownOnlyDropsIndex()
    {
        AssertMigration(
            new Farm.Migrations.SqlServer.Migrations.EnforceUniqueToolheadPrinterIndex(),
            "[Toolheads]",
            "[CurrentSpoolId] IS NOT NULL",
            "[UpdatedAt] DESC",
            "[Id] ASC");
    }

    private static void AssertMigration(
        Migration migration,
        string toolheadsIdentifier,
        string boundPreference,
        string updatedAtTieBreaker,
        string idTieBreaker)
    {
        IReadOnlyList<MigrationOperation> up = migration.UpOperations;
        Assert.Collection(
            up,
            operation =>
            {
                SqlOperation sql = Assert.IsType<SqlOperation>(operation);
                Assert.Contains("ROW_NUMBER()", sql.Sql, StringComparison.Ordinal);
                Assert.Contains("PARTITION BY", sql.Sql, StringComparison.Ordinal);
                Assert.Contains(toolheadsIdentifier, sql.Sql, StringComparison.Ordinal);
                Assert.Contains(boundPreference, sql.Sql, StringComparison.Ordinal);
                Assert.Contains(updatedAtTieBreaker, sql.Sql, StringComparison.Ordinal);
                Assert.Contains(idTieBreaker, sql.Sql, StringComparison.Ordinal);
                Assert.Contains("DELETE", sql.Sql, StringComparison.Ordinal);
            },
            operation =>
            {
                CreateIndexOperation create = Assert.IsType<CreateIndexOperation>(operation);
                Assert.Equal("UX_Toolheads_PrinterId_Index", create.Name);
                Assert.Equal("Toolheads", create.Table);
                Assert.Equal(new[] { "PrinterId", "Index" }, create.Columns);
                Assert.True(create.IsUnique);
            });

        DropIndexOperation drop = Assert.IsType<DropIndexOperation>(
            Assert.Single(migration.DownOperations));
        Assert.Equal("UX_Toolheads_PrinterId_Index", drop.Name);
        Assert.Equal("Toolheads", drop.Table);
    }
}
