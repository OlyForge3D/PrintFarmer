using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

public sealed class PartOutputSnapshotMigrationTests
{
    [Fact]
    public void PostgreSqlMigration_ContainsOnlySnapshotDelta_WithQuotedChecks()
    {
        AssertMigration(
            new Farm.Migrations.PostgreSQL.Migrations.AddPartOutputSnapshots(),
            "\"Sku\"");
    }

    [Fact]
    public void SqlServerMigration_ContainsOnlySnapshotDelta_WithBracketedChecks()
    {
        AssertMigration(
            new Farm.Migrations.SqlServer.Migrations.AddPartOutputSnapshots(),
            "[Sku]");
    }

    private static void AssertMigration(Migration migration, string quotedSku)
    {
        IReadOnlyList<MigrationOperation> up = migration.UpOperations;
        List<DropColumnOperation> drops = up.OfType<DropColumnOperation>().ToList();
        Assert.Equal(2, drops.Count);
        Assert.All(drops, operation => Assert.Equal("RowVersion", operation.Name));
        Assert.Equal(
            ["Bins", "PartInventories"],
            drops.Select(operation => operation.Table).OrderBy(value => value).ToArray());

        List<CreateTableOperation> creates = up.OfType<CreateTableOperation>().ToList();
        Assert.Equal(2, creates.Count);
        Assert.Equal(
            ["PartHarvestOutputSnapshots", "PrintJobPartOutputSnapshots"],
            creates.Select(operation => operation.Name).OrderBy(value => value).ToArray());
        Assert.DoesNotContain(creates, operation =>
            operation.Name is "Bins" or "PartInventories" or "PartInventoryAdjustments");
        Assert.All(creates, operation =>
            Assert.Contains(operation.CheckConstraints, constraint =>
                constraint.Sql.Contains(quotedSku, StringComparison.Ordinal)));

        IReadOnlyList<MigrationOperation> down = migration.DownOperations;
        Assert.Equal(2, down.OfType<DropTableOperation>().Count());
        List<AddColumnOperation> restored = down.OfType<AddColumnOperation>().ToList();
        Assert.Equal(2, restored.Count);
        Assert.All(restored, operation => Assert.Equal("RowVersion", operation.Name));
    }
}
