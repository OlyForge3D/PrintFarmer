using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

public class FallbackNameNormalizationMigrationTests
{
    [Fact]
    public void PostgreSqlMigration_CaseVariantCollision_DisambiguatesBeforeUniqueIndex()
    {
        AssertCollisionHandling(
            new Farm.Migrations.PostgreSQL.Migrations.AddFallbackNameNormalizationAndPerToolheadHours(),
            "\"PrinterId\", \"NameNormalized\"",
            "\"CreatedAt\", \"Id\"",
            "target.\"Id\"::text");
    }

    [Fact]
    public void SqlServerMigration_CaseVariantCollision_DisambiguatesBeforeUniqueIndex()
    {
        AssertCollisionHandling(
            new Farm.Migrations.SqlServer.Migrations.AddFallbackNameNormalizationAndPerToolheadHours(),
            "[PrinterId], [NameNormalized]",
            "[CreatedAt], [Id]",
            "CONVERT(nvarchar(36), [groups].[Id])");
    }

    private static void AssertCollisionHandling(
        Migration migration,
        string collisionPartition,
        string insertionOrder,
        string uniqueSuffix)
    {
        List<MigrationOperation> operations = [.. migration.UpOperations];
        List<SqlOperation> sqlOperations = operations.OfType<SqlOperation>().ToList();
        Assert.Equal(2, sqlOperations.Count);

        SqlOperation backfill = sqlOperations[0];
        Assert.Contains("LOWER", backfill.Sql, StringComparison.OrdinalIgnoreCase);

        SqlOperation disambiguation = sqlOperations[1];
        Assert.Contains("ROW_NUMBER()", disambiguation.Sql, StringComparison.Ordinal);
        Assert.Contains(collisionPartition, disambiguation.Sql, StringComparison.Ordinal);
        Assert.Contains(insertionOrder, disambiguation.Sql, StringComparison.Ordinal);
        Assert.Contains("DuplicateRank", disambiguation.Sql, StringComparison.Ordinal);
        Assert.Contains("> 1", disambiguation.Sql, StringComparison.Ordinal);
        Assert.Contains(uniqueSuffix, disambiguation.Sql, StringComparison.Ordinal);

        int disambiguationIndex = operations.IndexOf(disambiguation);
        CreateIndexOperation uniqueIndex = Assert.Single(
            operations.OfType<CreateIndexOperation>(),
            index => index.Name == "UX_FilamentFallbackGroups_PrinterId_NameNormalized");
        Assert.True(uniqueIndex.IsUnique);
        Assert.True(disambiguationIndex < operations.IndexOf(uniqueIndex));
    }
}
